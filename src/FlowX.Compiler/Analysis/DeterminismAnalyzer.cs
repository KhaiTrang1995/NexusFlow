using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// The determinism rules for the code a flow runs: FLOWX1007, FLOWX1008 and FLOWX1009.
/// </summary>
/// <remarks>
/// <para>
/// <c>06 §5</c> draws a boundary and puts the clock, identifiers and randomness on the
/// far side of it — journaled, never replayed — with <c>CapabilityContext</c> as the seam
/// that makes them reproducible. Three rows of that table have named these ids since
/// before there was a journal, and <c>07 §3</c> names them again as rules 6 and 7 of the
/// capability model. Both documents said "not enforced" in the same breath, which is the
/// state this analyzer ends.
/// </para>
/// <para>
/// <strong>What changed at WP-52, and it was not the analysis.</strong> These three were
/// blocked on <em>severity</em>: ADR-0003 makes them errors under <c>Durable</c> and
/// informational under <c>Ephemeral</c>, the runtime read <c>ExecutionProfile</c> nowhere,
/// so <c>Ephemeral</c> was the only profile anything executed and an Info diagnostic never
/// reaches a build log. They would have shipped doing nothing anywhere. The runtime reads
/// the profile now and journals a durable flow, so an error under <c>Durable</c> is one
/// something can run into.
/// </para>
/// <para>
/// <strong>Who the rules are about.</strong> A capability, and the parts of a flow class
/// that are not builder lambdas. The lambdas themselves belong to
/// <see cref="PredicatePurityAnalyzer"/> (FLOWX1011), which applies a strictly stronger
/// rule to them — <em>only</em> the context, the flow input and prior step results — and
/// reporting the same <c>DateTime.UtcNow</c> under two ids would make a reader choose
/// which one to believe. So this analyzer skips any node inside a delegate passed to a
/// FlowX builder, and the two rules partition the flow class between them rather than
/// overlapping on it.
/// </para>
/// <para>
/// <strong>Why a capability is in scope at all, when its body is the non-deterministic
/// zone.</strong> Because the zone is journaled, and what the journal records is exactly
/// <c>ctx.UtcNow</c>, the ids <c>ctx.NewId()</c> produced, and the seed
/// <c>ctx.Random</c> was built from — <c>NondeterminismCapture</c>, one row per step
/// boundary. A value the capability took ambiently is in none of those fields, so it is
/// precisely the part of the step a replay cannot reconstruct. The same holds one level
/// down and today, without any replay at all: a retried attempt re-executes the
/// capability, and <c>ctx.IdempotencyKey</c> is stable across the retry where
/// <c>Guid.NewGuid()</c> is not.
/// </para>
/// <para>
/// <strong>Severity is decided per report, by what the compilation can prove.</strong>
/// FLOWX1011 escalates on the flow's declared profile and FLOWX1025 on whether the
/// offending attribute is declared in the compilation being built; both choose severity by
/// who can act on the finding, and this analyzer follows them. A flow answers the question
/// itself. A capability cannot — it has no profile, it is reached from flows that may not
/// be in this compilation at all — so the escalation is a <em>proof</em> obligation: an
/// error only where a <c>Durable</c> flow in this compilation names the capability as a
/// step, directly or through a sub-flow it composes. Everywhere else a warning, which
/// still stops the build in a repository that sets <c>TreatWarningsAsErrors</c> and is
/// still one <c>.editorconfig</c> line for a consumer who has decided otherwise. Missing
/// an escalation costs little; inventing one would break a build on a claim the compiler
/// cannot support. The full reasoning, taken as a set rather than a row at a time, is on
/// <c>docs/diagnostics/README.md</c>.
/// </para>
/// <para>
/// <strong>What it cannot prove.</strong> Stated here as well as on the pages, because a
/// rule that stops a build has to be honest about its edges:
/// </para>
/// <list type="bullet">
/// <item>
/// <strong>Nothing is interprocedural.</strong> A capability that calls
/// <c>_pricing.CurrentRate()</c> is accepted and that method may read a clock. This is the
/// largest gap and it is not closable with a longer list.
/// </item>
/// <item>
/// <strong>The catalogue is a list, not a proof.</strong> A clock type nobody named in
/// <see cref="AmbientReads"/> is not detected — the same admitted limit as
/// <see cref="CapabilityAnalyzer"/>'s transport list. Silence here is never a statement
/// that code is deterministic.
/// </item>
/// <item>
/// <strong>FLOWX1009 is a declaration-site rule.</strong> It proves that a type declares
/// state something can assign; it says nothing about a mutable static declared on some
/// other type and read from a capability body. <c>06 §5</c> words the row as "no mutable
/// static state <em>reachable from</em> a flow", which is the wider claim, and this is
/// deliberately the narrower one — a capability is the impure zone and legitimately reads
/// the outside world, so a read-site rule there would fire on configuration. Inside a flow
/// delegate FLOWX1011 does report the read.
/// </item>
/// <item>
/// <strong><c>readonly</c> is taken at face value and is only shallowly true.</strong> A
/// <c>readonly List&lt;T&gt;</c> is mutable state this rule permits, exactly as FLOWX1011
/// permits a <c>static readonly</c> one.
/// </item>
/// <item>
/// <strong>Only this compilation is visible.</strong> A capability whose durable caller
/// lives in a referenced assembly is reported as a warning, not an error, and a mutable
/// field inherited from a base class in another assembly is not reported at all.
/// </item>
/// </list>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttribute = "FlowX.FlowAttribute";
    private const string CapabilityMetadataName = "ICapability`2";
    private const string FlowXNamespace = "FlowX";
    private const string ProfileArgument = "Profile";
    private const string BuilderInterfaceSuffix = "Builder";

    /// <summary><c>ExecutionProfile.Durable</c>, as it appears in attribute metadata.</summary>
    private const int DurableProfile = 1;

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            FlowXDiagnostics.ClockIsReadAmbiently,
            FlowXDiagnostics.IdentityIsTakenAmbiently,
            FlowXDiagnostics.MutableStateIsHeld);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated partial holds the plan and the dispatcher. Its dispatcher field is
        // ours, and reporting a rule about hand-written state against a file nobody can
        // edit is how a catalogue gets suppressed wholesale.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Two phases, for the same reason SubFlowCycleAnalyzer has them: the severity of a
        // finding inside a capability is a property of the flows that reach it, which is
        // not visible from the capability's own declaration. The findings are collected as
        // the declarations are visited and graded once, at the end, when every flow in the
        // compilation is known.
        context.RegisterCompilationStartAction(static start =>
        {
            var pass = new Pass();

            start.RegisterSyntaxNodeAction(
                pass.Add,
                SyntaxKind.ClassDeclaration,
                SyntaxKind.RecordDeclaration,
                SyntaxKind.StructDeclaration,
                SyntaxKind.RecordStructDeclaration);

            start.RegisterCompilationEndAction(pass.Report);
        });
    }

    /// <summary>One finding, held until the compilation can say how severe it is.</summary>
    private readonly struct Finding
    {
        public Finding(DiagnosticDescriptor descriptor, Location location, INamedTypeSymbol subject, object[] arguments)
        {
            Descriptor = descriptor;
            Location = location;
            Subject = subject;
            Arguments = arguments;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Location Location { get; }

        /// <summary>The capability or flow the finding is about — what decides the severity.</summary>
        public INamedTypeSymbol Subject { get; }

        public object[] Arguments { get; }
    }

    /// <summary>Collects findings and composition edges, then grades and reports them.</summary>
    /// <remarks>
    /// The syntax action runs concurrently, so the collection is guarded; the grading runs
    /// once, on one thread, at compilation end.
    /// </remarks>
    private sealed class Pass
    {
        private readonly object _sync = new object();

        private readonly List<Finding> _findings = new List<Finding>();

        /// <summary>Flow type → the capability and flow types its chain names.</summary>
        private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _referenced =
            new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

        /// <summary>The flows that declare <c>Profile = ExecutionProfile.Durable</c>.</summary>
        private readonly HashSet<INamedTypeSymbol> _durable =
            new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        internal void Add(SyntaxNodeAnalysisContext context)
        {
            if (context.Node is not TypeDeclarationSyntax declaration ||
                context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken)
                    is not INamedTypeSymbol type)
            {
                return;
            }

            var isFlow = IsFlow(type);

            if (!isFlow && !IsCapability(type))
            {
                return;
            }

            var findings = new List<Finding>();

            foreach (var member in declaration.Members)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // A nested type is its own subject and is visited on its own; walking it
                // from here would report a nested capability against its container's name.
                if (member is BaseTypeDeclarationSyntax)
                {
                    continue;
                }

                MutableState(member, type, context, findings);
                AmbientIn(member, type, context, findings);
            }

            var referenced = isFlow
                ? Referenced(declaration, context.SemanticModel, context.CancellationToken)
                : null;

            lock (_sync)
            {
                _findings.AddRange(findings);

                if (!isFlow)
                {
                    return;
                }

                if (IsDurable(type))
                {
                    _durable.Add(type);
                }

                if (_referenced.TryGetValue(type, out var existing))
                {
                    existing.AddRange(referenced!);
                }
                else
                {
                    _referenced[type] = referenced!;
                }
            }
        }

        internal void Report(CompilationAnalysisContext context)
        {
            if (_findings.Count == 0)
            {
                // The reach walk costs a pass over every flow in the compilation. Nothing
                // needs grading, so nothing pays for it.
                return;
            }

            var durable = DurableReach(context.CancellationToken);

            // Ordered so that two builds of the same source report in the same order; a
            // concurrent syntax action does not otherwise guarantee it.
            foreach (var finding in _findings
                .OrderBy(f => f.Location.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(f => f.Location.SourceSpan.Start)
                .ThenBy(f => f.Descriptor.Id, StringComparer.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    finding.Descriptor,
                    finding.Location,
                    durable.Contains(finding.Subject) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                    additionalLocations: null,
                    properties: null,
                    finding.Arguments));
            }
        }

        /// <summary>
        /// Every type on a durable flow's replay path: the durable flows themselves, the
        /// capabilities they step through, and the same again for each sub-flow they
        /// compose.
        /// </summary>
        /// <remarks>
        /// The transitive step matters and is not decoration. A sub-flow runs <em>inside</em>
        /// its parent's instance, so a capability reached only through an
        /// <c>Ephemeral</c>-declared sub-flow of a <c>Durable</c> parent is on a replay path
        /// just the same, and grading it as a warning would put the escalation exactly one
        /// composition away from being avoidable.
        /// </remarks>
        private HashSet<INamedTypeSymbol> DurableReach(CancellationToken cancellationToken)
        {
            var reached = new HashSet<INamedTypeSymbol>(_durable, SymbolEqualityComparer.Default);
            var queue = new Queue<INamedTypeSymbol>(_durable);

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_referenced.TryGetValue(queue.Dequeue(), out var references))
                {
                    continue;
                }

                foreach (var reference in references)
                {
                    if (!reached.Add(reference))
                    {
                        continue;
                    }

                    if (_referenced.ContainsKey(reference))
                    {
                        queue.Enqueue(reference);
                    }
                }
            }

            return reached;
        }

        /// <summary>FLOWX1009 — a field or property something can assign after construction.</summary>
        /// <remarks>
        /// Read off the declaration rather than off the symbol's members, so that each part
        /// of a partial class reports its own and a type declared twice is not reported
        /// twice for one field. An <c>init</c> accessor is not a setter for this purpose:
        /// it can only run while the object is being constructed, which is the property
        /// this rule is asking about.
        /// </remarks>
        private static void MutableState(
            MemberDeclarationSyntax member,
            INamedTypeSymbol type,
            SyntaxNodeAnalysisContext context,
            List<Finding> findings)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken)
                                is IFieldSymbol declared &&
                            !declared.IsConst &&
                            !declared.IsReadOnly)
                        {
                            findings.Add(new Finding(
                                FlowXDiagnostics.MutableStateIsHeld,
                                variable.Identifier.GetLocation(),
                                type,
                                [type.Name, Kind(declared.IsStatic, "field"), declared.Name]));
                        }
                    }

                    break;

                case PropertyDeclarationSyntax property:
                    if (context.SemanticModel.GetDeclaredSymbol(property, context.CancellationToken)
                            is IPropertySymbol { SetMethod: { IsInitOnly: false } } settable)
                    {
                        findings.Add(new Finding(
                            FlowXDiagnostics.MutableStateIsHeld,
                            property.Identifier.GetLocation(),
                            type,
                            [type.Name, Kind(settable.IsStatic, "property"), settable.Name]));
                    }

                    break;

                default:
                    break;
            }
        }

        /// <summary>FLOWX1007 and FLOWX1008 — the ambient reads inside one member.</summary>
        private static void AmbientIn(
            MemberDeclarationSyntax member,
            INamedTypeSymbol type,
            SyntaxNodeAnalysisContext context,
            List<Finding> findings)
        {
            var model = context.SemanticModel;
            var cancellationToken = context.CancellationToken;

            foreach (var node in member.DescendantNodesAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (AmbientReads.IsInsideNameOf(node, member))
                {
                    continue;
                }

                var found = node is ObjectCreationExpressionSyntax creation
                    ? AmbientReads.Constructed(creation, model, cancellationToken)
                    : Ambient(node, model, cancellationToken);

                if (found is null)
                {
                    continue;
                }

                var descriptor = found.Value.Impurity switch
                {
                    Impurity.Clock => FlowXDiagnostics.ClockIsReadAmbiently,
                    Impurity.Randomness => FlowXDiagnostics.IdentityIsTakenAmbiently,

                    // Everything else the catalogue knows about — the environment, the
                    // console, a file, an HTTP client, a mutable static on another type —
                    // is not this rule's subject. A capability is the impure zone by
                    // design: 06 §5 puts capability bodies in it and journals their
                    // results. What it may not do is take the three things the journal
                    // reproduces from somewhere the journal cannot see.
                    _ => null,
                };

                if (descriptor is null ||
                    IsInsideABuilderDelegate(found.Value.Node, member, model, cancellationToken))
                {
                    continue;
                }

                findings.Add(new Finding(
                    descriptor,
                    found.Value.Node.GetLocation(),
                    type,
                    [type.Name, found.Value.Node.ToString()]));
            }
        }

        /// <summary>The ambient read at one access root, or <c>null</c>.</summary>
        /// <remarks>
        /// Only a <em>static</em> access is asked about. A capability holds injected
        /// dependencies and reads its own input and context, and reporting those would
        /// report the whole programming model.
        /// </remarks>
        private static (SyntaxNode Node, Impurity Impurity)? Ambient(
            SyntaxNode node,
            SemanticModel model,
            CancellationToken cancellationToken)
        {
            if (!AmbientReads.IsAccessRoot(node))
            {
                return null;
            }

            var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;

            return symbol switch
            {
                // A type name is not a read. What matters is the static member reached
                // through it, if any — System.DateTime.UtcNow is three nested nodes.
                INamespaceOrTypeSymbol => AmbientReads.StaticMemberOn(node, model, cancellationToken),

                // A member named with no receiver, when it is a static of this type.
                { IsStatic: true } => AmbientReads.StaticMember(node, symbol),
                _ => null,
            };
        }

        /// <summary>
        /// Whether the node sits inside a delegate handed to a FlowX builder, which is
        /// FLOWX1011's subject and not this one's.
        /// </summary>
        /// <remarks>
        /// The two rules would otherwise both report <c>DateTime.UtcNow</c> in a
        /// <c>When</c>, under different ids and different wording, and a reader shown two
        /// diagnostics for one mistake stops trusting either. FLOWX1011 keeps the lambdas
        /// because its rule there is strictly stronger — a captured local and an injected
        /// service are reported as well as a clock — and this rule keeps everything else in
        /// the class, which FLOWX1011 has never covered.
        /// </remarks>
        private static bool IsInsideABuilderDelegate(
            SyntaxNode node,
            SyntaxNode boundary,
            SemanticModel model,
            CancellationToken cancellationToken)
        {
            for (var current = node; current is not null && current != boundary.Parent; current = current.Parent)
            {
                if (current is AnonymousFunctionExpressionSyntax &&
                    current.Parent is ArgumentSyntax argument &&
                    argument.Parent?.Parent is InvocationExpressionSyntax invocation &&
                    IsBuilderCall(invocation, model, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The capability and flow types one flow declaration's chain names.
        /// </summary>
        /// <remarks>
        /// Every type argument of every builder call, filtered by what the type turns out
        /// to be rather than by which method was called. <c>Step&lt;T&gt;</c>,
        /// <c>Step&lt;T, TIn&gt;</c>, <c>Branch&lt;T&gt;</c>, <c>CompensateWith&lt;T&gt;</c>
        /// and <c>SubFlow&lt;T, TIn&gt;</c> are then one rule instead of five, and a
        /// construct the DSL grows later is covered on the day it is added rather than the
        /// day someone remembers to add its name here. <c>Emit&lt;TEvent&gt;</c> names a
        /// contract, which is neither a capability nor a flow, and falls out on its own.
        /// </remarks>
        private static List<INamedTypeSymbol> Referenced(
            TypeDeclarationSyntax declaration,
            SemanticModel model,
            CancellationToken cancellationToken)
        {
            var referenced = new List<INamedTypeSymbol>();

            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                    member.Name is not GenericNameSyntax generic ||
                    !IsBuilderCall(invocation, model, cancellationToken))
                {
                    continue;
                }

                foreach (var argument in generic.TypeArgumentList.Arguments)
                {
                    if (model.GetSymbolInfo(argument, cancellationToken).Symbol is INamedTypeSymbol named &&
                        named.TypeKind != TypeKind.Error &&
                        (IsCapability(named) || IsFlow(named)) &&
                        !referenced.Contains(named, SymbolEqualityComparer.Default))
                    {
                        referenced.Add(named);
                    }
                }
            }

            return referenced;
        }

        /// <summary>Whether the invocation resolves to a method on one of FlowX's builders.</summary>
        /// <remarks>
        /// Resolved semantically rather than matched on the name, for the reason
        /// <see cref="PredicatePurityAnalyzer"/> gives at length: <c>Step</c>,
        /// <c>Return</c> and <c>When</c> are words other fluent libraries use, and a
        /// determinism rule reasoning about a mocking framework's <c>When</c> would be
        /// indefensible. <c>IStepBuilder</c>, <c>IConditionalBuilder</c>,
        /// <c>ISwitchBuilder</c>, <c>IAwaitBuilder</c> and <c>IParallelBuilder</c> are all
        /// matched, because a call on any of them is a call on the flow's chain.
        /// </remarks>
        private static bool IsBuilderCall(
            InvocationExpressionSyntax invocation,
            SemanticModel model,
            CancellationToken cancellationToken) =>
            model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
            method.ContainingType is { } container &&
            container.TypeKind == TypeKind.Interface &&
            container.ContainingNamespace?.ToDisplayString() == FlowXNamespace &&
            container.Name.EndsWith(BuilderInterfaceSuffix, StringComparison.Ordinal);

        private static string Kind(bool isStatic, string noun) =>
            (isStatic ? "static " : "instance ") + noun;

        private static bool IsCapability(INamedTypeSymbol type) =>
            type.AllInterfaces.Any(i =>
                i.MetadataName == CapabilityMetadataName &&
                i.ContainingNamespace?.ToDisplayString() == FlowXNamespace);

        private static bool IsFlow(INamedTypeSymbol type) =>
            type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FlowAttribute);

        /// <summary>Whether the flow declares <c>Profile = ExecutionProfile.Durable</c>.</summary>
        /// <remarks>
        /// The attribute's default is <c>Ephemeral</c> — ADR-0003, durability is opted into —
        /// so an omitted argument correctly reads as the lower severity.
        /// </remarks>
        private static bool IsDurable(INamedTypeSymbol flow)
        {
            foreach (var attribute in flow.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != FlowAttribute)
                {
                    continue;
                }

                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument.Key == ProfileArgument && argument.Value.Value is int profile)
                    {
                        return profile == DurableProfile;
                    }
                }
            }

            return false;
        }
    }
}
