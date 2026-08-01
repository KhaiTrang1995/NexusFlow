using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Diagnostics;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FlowX.Compiler;

/// <summary>
/// Emits a compiled execution plan and step dispatcher for every <c>[Flow]</c> in the
/// compilation.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. Everything this class does is: find flow declarations, hand each
/// to <see cref="FlowAnalyzer"/>, hand the resulting model to <see cref="FlowEmitter"/>,
/// and report whatever diagnostics came back. All of the judgement lives in the two
/// layers it calls, and both are testable without a compilation.
/// </para>
/// <para>
/// <strong>Incremental, and it matters.</strong> A generator that re-runs on every
/// keystroke makes the IDE unusable on a large solution, which is how a team ends up
/// disabling the generator and hand-writing what it produced. The pipeline is keyed on
/// syntax that names <c>[Flow]</c> so typing inside an unrelated method costs nothing,
/// and budget B12 caps total build overhead at 8 %.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class FlowPlanGenerator : IIncrementalGenerator
{
    private const string FlowAttributeName = "FlowX.FlowAttribute";
    private const string CapabilityAttributeName = "FlowX.CapabilityAttribute";
    private const string JsonSerializableAttributeName = "System.Text.Json.Serialization.JsonSerializableAttribute";
    private const string JsonSerializerContextName = "System.Text.Json.Serialization.JsonSerializerContext";

    /// <summary>
    /// The one thing this generator knows about the HTTP transport: a name to look for.
    /// </summary>
    /// <remarks>
    /// Not a reference. A Roslyn component links against nothing but Roslyn, and a
    /// generator that referenced <c>FlowX.Http</c> would put a plugin underneath the
    /// compiler — the exact inversion <c>RuntimeDoesNotReferenceAnyPlugin</c> exists to
    /// forbid. Looking the type up in the user's compilation asks the only question that
    /// matters, "did <em>they</em> reference it", and answers it without a dependency.
    /// </remarks>
    private const string HttpEndpointExtensionsName = "FlowX.Http.FlowEndpointExtensions";

    /// <summary>
    /// The one thing this generator knows about the host that fires a schedule: a name to look
    /// for.
    /// </summary>
    /// <remarks>
    /// The same arrangement <see cref="HttpEndpointExtensionsName"/> has, and for the same
    /// reason. <c>FlowX.Hosting</c> is not a plugin, but the generator cannot reference it either
    /// — it is a netstandard2.0 analyzer — and a flow library compiled on its own has no reason
    /// to depend on a host. Asking the user's compilation whether the type exists answers the
    /// only question that matters.
    /// </remarks>
    private const string ScheduleRegistrationName = "FlowX.Hosting.FlowScheduleRegistration";

    /// <summary>The id whose reporting this class decides rather than passes through.</summary>
    private static readonly string EmitDiagnosticId = FlowXDiagnostics.EmitIsNotYetPublished.Id;

    /// <summary>The second such id, and it is settled the same way for the same reason.</summary>
    private static readonly string StateDiagnosticId = FlowXDiagnostics.StateIsNotSerialisable.Id;

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var flows = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FlowAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => Analyze(ctx, cancellationToken))
            .Where(static result => result is not null);

        // The manifest describes the whole application, so it needs every flow at once.
        // Collect() introduces a barrier — any flow changing regenerates the manifest —
        // which is correct: a manifest built from a stale subset would be worse than no
        // manifest, because `flowx diff` would trust it.
        var application = context.CompilationProvider.Select(
            static (compilation, _) => compilation.AssemblyName ?? "Application");

        // Source pointers in the manifest are written relative to this, so the document
        // does not carry the build agent's directory layout. Supplied by the props file
        // shipped in the analyzer package; null when a host does not provide it, which
        // Relativise handles by leaving the path alone.
        var projectDirectory = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) => options.GlobalOptions.TryGetValue("build_property.projectdir", out var dir)
                ? dir
                : null);

        // Triggers are read from attributes on the flow's class rather than from its
        // Define chain, so they come down their own pipeline and never enter FlowModel.
        // The plan emitter has no use for them — the flow body cannot observe a trigger
        // (ADR-0004) — and keeping them out of the model is what makes that true in the
        // code and not only in the documentation.
        var triggers = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FlowAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ReadTriggers(ctx, cancellationToken))
            .Where(static result => result is not null);

        // Error catalogues are keyed on [Capability], not on [Flow], so every capability
        // in the compilation is read — including one no flow has a step for yet. The
        // manifest joins on id@version and uses what it needs.
        var errorCatalogues = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                CapabilityAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ErrorCatalogueReader.Read(
                    ctx.TargetSymbol as INamedTypeSymbol, ctx.SemanticModel.Compilation, cancellationToken))
            .Where(static result => result is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(errorCatalogues.Collect())
                .Combine(application)
                .Combine(projectDirectory),
            static (production, data) =>
            {
                var ((((analysed, declared), catalogues), applicationName), directory) = data;
                ProduceManifest(production, analysed, declared, catalogues, applicationName, directory);
            });

        // Whether this compilation can host an HTTP endpoint at all, expressed as one
        // bool so nothing downstream re-runs when an unrelated reference changes. The
        // generator knows the transport only by this name: it links against no plugin
        // and could not, being a netstandard2.0 analyzer, which is what keeps
        // RuntimeDoesNotReferenceAnyPlugin true by construction rather than by care.
        var httpAvailable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(HttpEndpointExtensionsName) is not null);

        // The serialiser contexts declared in this compilation. Read from
        // [JsonSerializable] rather than from the properties System.Text.Json generates
        // from it: the attribute is the author's declaration and is readable now,
        // whereas the properties are another generator's output and would put an
        // ordering dependency between two generators that have none.
        var jsonContexts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                JsonSerializableAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ReadJsonContext(ctx, cancellationToken))
            .Where(static result => result is not null);

        // The plan is emitted per flow, and joined to the compilation's serialiser contexts
        // because an `.Emit` step's body is written through one. Combined rather than
        // collected on both sides: one flow changing still regenerates one file, and a
        // context changing regenerates the flows whose events it declares — which is
        // correct, since that is exactly when a dispatcher gains or loses a DescribeStep.
        context.RegisterSourceOutput(
            flows.Combine(jsonContexts.Collect()),
            static (production, data) => Produce(production, data.Left!, data.Right));

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(jsonContexts.Collect())
                .Combine(httpAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var ((((analysed, declared), contexts), available), assembly) = data;
                ProduceEndpoints(production, analysed, declared, contexts, available, assembly);
            });

        // Whether this compilation can register a schedule at all, expressed as one bool for the
        // reason httpAvailable is: nothing downstream re-runs when an unrelated reference
        // changes.
        var schedulingAvailable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(ScheduleRegistrationName) is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(schedulingAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var (((analysed, declared), available), assembly) = data;
                ProduceSchedules(production, analysed, declared, available, assembly);
            });
    }

    /// <summary>
    /// Emits one registration per <c>[CronTrigger]</c> the host can actually fire, or nothing
    /// at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all is the common case: a project with no flow that declares a schedule, or a
    /// flow library with no host to register into, gets no file — zero types, zero IL.
    /// </para>
    /// <para>
    /// <strong>A flow this host could not fire is skipped here and reported by
    /// <c>TriggerDeclarationAnalyzer</c>, not by both.</strong> The two conditions are the same
    /// two <c>FLOWX1037</c> names — an input contract that is not <c>ScheduledFire</c>, and a
    /// profile that is not <c>Durable</c> — and the analyzer has the attribute's own span to
    /// point at where this has a collected model and nothing else. Reporting from both would put
    /// the same defect in the build log twice, in one case with no file name.
    /// </para>
    /// </remarks>
    private static void ProduceSchedules(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        bool schedulingAvailable,
        string assemblyName)
    {
        if (!schedulingAvailable)
        {
            return;
        }

        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .OrderBy(static m => m.FlowId, StringComparer.Ordinal)
            .ToList();

        var schedules = new List<ScheduleModel>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flow in models)
        {
            if (!declared.TryGetValue(flow.FlowId, out var flowTriggers) || !CanBeFired(flow))
            {
                continue;
            }

            foreach (var trigger in flowTriggers.Triggers.Where(IsScheduleAddress))
            {
                schedules.Add(new ScheduleModel(
                    flow.FlowId,
                    flow.FullTypeName,
                    ScheduleMethodName(flow.TypeName, names),
                    trigger.Cron!,
                    trigger.TimeZone ?? "UTC",
                    MissedFireFor(flowTriggers, trigger.Cron!)));
            }
        }

        if (schedules.Count > 0)
        {
            production.AddSource(
                ScheduleEmitter.FileName,
                SourceText.From(ScheduleEmitter.Emit(assemblyName, schedules), Encoding.UTF8));
        }
    }

    /// <summary>Whether a firing of this flow could be started, and started once.</summary>
    /// <remarks>
    /// The two conditions <c>FLOWX1037</c> reports, restated as a predicate: a cron firing has no
    /// body, so the flow has to bind the occurrence; and an ephemeral flow journals no instance,
    /// so nothing would refuse a second node's firing of the same occurrence.
    /// </remarks>
    private static bool CanBeFired(FlowModel flow) =>
        string.Equals(flow.InputTypeName, "FlowX.ScheduledFire", StringComparison.Ordinal) &&
        string.Equals(flow.Profile, "Durable", StringComparison.Ordinal);

    /// <summary>A schedule trigger this build could read an expression off.</summary>
    /// <remarks>
    /// A trigger whose kind is <c>Schedule</c> but whose arguments this compiler could not
    /// interpret reaches the manifest as a bare kind, and must produce no registration for the
    /// same reason it produces no endpoint: an expression nobody read is not a schedule to fire.
    /// </remarks>
    private static bool IsScheduleAddress(TriggerModel trigger) =>
        string.Equals(trigger.Kind, "Schedule", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(trigger.Cron);

    /// <summary>
    /// The missed-fire policy declared beside this expression, or the attribute's own default.
    /// </summary>
    /// <remarks>
    /// Joined on the expression rather than on position, because <c>FlowTriggersModel</c> sorts
    /// its triggers ordinally so the manifest is byte-stable and the declarations are in source
    /// order. A flow declaring the same expression twice gets the first policy for both, which is
    /// a declaration nobody should write and which fires one schedule either way — the two
    /// registrations collapse onto one key in <c>FlowScheduleCatalog</c>.
    /// </remarks>
    private static string MissedFireFor(FlowTriggersModel triggers, string cron) => triggers.Schedules
        .Where(schedule => string.Equals(schedule.Cron, cron, StringComparison.Ordinal))
        .Select(static schedule => schedule.MissedFire)
        .FirstOrDefault() ?? "RunOnce";

    /// <summary>The extension method one schedule is registered by.</summary>
    /// <remarks>
    /// Named from the flow's type for <see cref="MethodName"/>'s reason:
    /// <c>services.AddReconcileLedgerFlowSchedule()</c> reads as the flow it registers. A flow
    /// declaring two schedules takes a numeric suffix, deterministically, in the order the flows
    /// were sorted by id.
    /// </remarks>
    private static string ScheduleMethodName(string typeName, HashSet<string> taken)
    {
        var candidate = "Add" + typeName + "Schedule";
        var suffix = 2;

        while (!taken.Add(candidate))
        {
            candidate = "Add" + typeName + "Schedule" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// Reads one <c>JsonSerializerContext</c> and the contracts it declares.
    /// </summary>
    /// <remarks>
    /// Anything that is not a serialiser context, or that generated code in this assembly
    /// could not name, is dropped here rather than filtered later — a context nested
    /// inside a private type is not a candidate for anything, and letting it reach the
    /// emitter would only give the emitter a rule to restate.
    /// </remarks>
    private static JsonContextModel? ReadJsonContext(
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetSymbol is not INamedTypeSymbol symbol || !IsJsonSerializerContext(symbol) ||
            !IsVisibleInAssembly(symbol))
        {
            return null;
        }

        var contracts = context.Attributes
            .Where(static a => a.ConstructorArguments.Length > 0)
            .Select(static a => a.ConstructorArguments[0].Value as INamedTypeSymbol)
            .Where(static t => t is not null)
            .Select(static t => Display(t!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        return contracts.Count == 0 ? null : new JsonContextModel(Display(symbol), contracts);
    }

    private static bool IsJsonSerializerContext(INamedTypeSymbol symbol)
    {
        for (var type = symbol.BaseType; type is not null; type = type.BaseType)
        {
            if (string.Equals(type.ToDisplayString(), JsonSerializerContextName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether other code in the same assembly can name this type.</summary>
    private static bool IsVisibleInAssembly(INamedTypeSymbol symbol)
    {
        for (var type = symbol; type is not null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The fully qualified name of a symbol, in the form emitted source uses.</summary>
    private static string Display(ISymbol symbol) => symbol
        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        .Replace("global::", string.Empty);

    /// <summary>
    /// Emits one endpoint registration per <c>[HttpTrigger]</c>, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all is the common case and the important one. A project with no HTTP
    /// transport, or with no flow that declares an address on it, gets no file — so the
    /// cost of this feature to a Kafka-only application is zero types, zero IL and zero
    /// build output, which is the property that lets a transport be a plugin.
    /// </para>
    /// <para>
    /// A flow with an <c>[HttpTrigger]</c> but no <c>.Return(...)</c> is skipped: the
    /// endpoint writes the flow's declared output, and a flow that declares none has no
    /// <c>Projection</c> field to write it with.
    /// </para>
    /// </remarks>
    private static void ProduceEndpoints(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        ImmutableArray<JsonContextModel?> jsonContexts,
        bool httpAvailable,
        string assemblyName)
    {
        if (!httpAvailable)
        {
            return;
        }

        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var contexts = jsonContexts
            .Where(static c => c is not null)
            .Select(static c => c!)
            .ToList();

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .OrderBy(static m => m.FlowId, StringComparer.Ordinal)
            .ToList();

        var endpoints = new List<HttpEndpointModel>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flow in models)
        {
            if (flow.ReturnProjection is null ||
                !declared.TryGetValue(flow.FlowId, out var flowTriggers))
            {
                continue;
            }

            foreach (var trigger in flowTriggers.Triggers.Where(IsHttpAddress))
            {
                endpoints.Add(new HttpEndpointModel(
                    flow.FlowId,
                    flow.FullTypeName,
                    MethodName(flow.TypeName, names),
                    flow.InputTypeName,
                    flow.OutputTypeName,
                    trigger.Method!,
                    trigger.Route!,
                    trigger.Idempotent == true,
                    ContextFor(contexts, flow.InputTypeName, flow.OutputTypeName),
                    SignalsOf(flow)));
            }
        }

        if (endpoints.Count > 0)
        {
            production.AddSource(
                EndpointEmitter.FileName,
                SourceText.From(EndpointEmitter.Emit(assemblyName, endpoints), Encoding.UTF8));
        }
    }

    /// <summary>
    /// Every signal a flow can suspend at, deduplicated by identity in step order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Read from the flow's body, which is the one field of
    /// <see cref="HttpEndpointModel"/> that does not come from the trigger attribute.</strong>
    /// A wait is declared in <c>Define</c>, and that is where this reads it, so a route serving
    /// a signal nothing waits for cannot be generated — see
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md">ADR-0022</a>,
    /// rejected option F.
    /// </para>
    /// <para>
    /// <c>AllSteps</c> rather than <c>Steps</c>, so a wait inside a conditional or a loop body
    /// gets its route: it is still a wait the flow can stop at, and a sender delivering to it
    /// does not know which branch put it there. A wait whose contract this compilation could
    /// not resolve produces no route, for the reason an unreadable trigger produces no
    /// endpoint — the emitted call needs a type to name.
    /// </para>
    /// </remarks>
    private static List<SignalEndpointModel> SignalsOf(FlowModel flow)
    {
        var signals = new List<SignalEndpointModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in flow.AllSteps)
        {
            if (step.Kind == StepKindModel.AwaitSignal &&
                step.SignalType is { Length: > 0 } signal &&
                step.SignalContractTypeName is { Length: > 0 } contract &&
                seen.Add(signal))
            {
                signals.Add(new SignalEndpointModel(signal, contract));
            }
        }

        return signals;
    }

    /// <summary>An HTTP trigger this build could read an address off.</summary>
    /// <remarks>
    /// A trigger whose kind is <c>Http</c> but whose arguments this compiler could not
    /// interpret reaches the manifest as a bare kind, and it must produce no endpoint for
    /// the same reason: a route nobody read is not a route to serve.
    /// </remarks>
    private static bool IsHttpAddress(TriggerModel trigger) =>
        string.Equals(trigger.Kind, "Http", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(trigger.Method) &&
        !string.IsNullOrEmpty(trigger.Route);

    /// <summary>
    /// The single serialiser context declaring both contracts, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <c>null</c> for none and for several, and the two are the same answer: the
    /// no-argument overload exists only where there is nothing to choose. Picking the
    /// first of several would make the wire format depend on file order.
    /// </remarks>
    private static string? ContextFor(List<JsonContextModel> contexts, string input, string output)
    {
        string? found = null;

        foreach (var candidate in contexts.Where(c => c.Declares(input, output)))
        {
            if (found is not null)
            {
                return null;
            }

            found = candidate.TypeName;
        }

        return found;
    }

    /// <summary>
    /// The extension method one endpoint is registered by.
    /// </summary>
    /// <remarks>
    /// Named from the flow's type rather than from its id, because this is a C# member a
    /// developer types: <c>app.MapPlaceOrderFlow()</c> reads as the flow it maps, where
    /// <c>MapOrderPlace()</c> would need the id in front of you. Two flows of the same
    /// type name in different namespaces, or one flow declaring two addresses, take a
    /// numeric suffix in the order the flows were sorted by id — deterministic, and rare
    /// enough that the alternative of mangling every name is the wrong trade.
    /// </remarks>
    private static string MethodName(string typeName, HashSet<string> taken)
    {
        var candidate = "Map" + typeName;
        var suffix = 2;

        while (!taken.Add(candidate))
        {
            candidate = "Map" + typeName + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>Reads the trigger attributes off one flow declaration.</summary>
    /// <remarks>
    /// The flow id is read from <c>[Flow]</c> here rather than taken from the analysed
    /// model, because this pipeline runs independently of that one: a trigger set that
    /// waited for a flow to analyse cleanly would vanish from the manifest whenever the
    /// flow body had an unrelated error, which is precisely when a reviewer is looking.
    /// </remarks>
    private static FlowTriggersModel? ReadTriggers(
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        var declared = TriggerReader.Read(symbol);

        if (declared.Count == 0)
        {
            return null;
        }

        var attribute = context.Attributes.FirstOrDefault(a => a.ConstructorArguments.Length > 0);
        var flowId = attribute?.ConstructorArguments[0].Value as string ?? symbol.Name;

        return new FlowTriggersModel(flowId, declared, TriggerReader.ReadSchedules(symbol));
    }

    private static void ProduceManifest(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        ImmutableArray<CapabilityErrorCatalogue?> errorCatalogues,
        string applicationName,
        string? projectDirectory)
    {
        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .ToList();

        if (models.Count == 0)
        {
            // No flows, or none that analysed cleanly. Emitting an empty manifest here
            // would let a build with errors publish a document claiming the application
            // has no flows, which is a more dangerous lie than emitting nothing.
            return;
        }

        var manifest = ManifestWriter.Write(
            applicationName,
            "1.0.0",
            models,
            projectDirectory,
            [.. triggers.Where(static t => t is not null).Select(static t => t!)],
            [.. errorCatalogues.Where(static c => c is not null).Select(static c => c!)]);

        production.AddSource("FlowXManifest.g.cs", SourceText.From(EmitManifestHolder(manifest), Encoding.UTF8));
    }

    /// <summary>
    /// Wraps the manifest JSON in a C# constant.
    /// </summary>
    /// <remarks>
    /// A source generator must not write files. It runs inside the IDE on every
    /// keystroke, its output is cached by the compiler, and file IO from that position
    /// breaks incrementality and races with the build. So the manifest travels as a
    /// compiled-in constant, and <c>flowx manifest</c> (WP-9) writes it to disk from
    /// there. The build artifact ADR-0005 asks for is produced by the CLI; the content
    /// is produced here, deterministically.
    /// </remarks>
    private static string EmitManifestHolder(string manifest)
    {
        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated;");
        writer.Line();
        writer.Line("/// <summary>The application's compiled manifest. See ADR-0005.</summary>");
        writer.Line("public static class FlowXManifest");
        writer.OpenBrace();
        writer.Line("/// <summary>The manifest document, byte-identical across builds of identical source.</summary>");
        writer.Line("public const string Json = @\"" + manifest.Replace("\"", "\"\"") + "\";");
        writer.CloseBrace();

        return writer.ToString();
    }

    private static AnalysisResult? Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetNode is not ClassDeclarationSyntax declaration ||
            context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        return FlowAnalyzer.Analyze(symbol, declaration, context.SemanticModel);
    }

    /// <summary>
    /// Emits one flow's plan and dispatcher, and settles that flow's <c>FLOWX1024</c>s.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the warning is decided here and not in analysis.</strong> Whether an
    /// <c>.Emit</c> step stages anything turns on two facts. The flow's profile is one, and
    /// <c>FlowAnalyzer</c> knows it. The other — whether exactly one
    /// <c>JsonSerializerContext</c> in this compilation declares the event contract — is a
    /// question about every tree in the build, and a per-flow transform that walked them all
    /// to answer it would trade the generator's incrementality for a warning. So analysis
    /// raises a provisional diagnostic carrying the contract in its properties, and this
    /// drops it or restates it with the reason that applies.
    /// </para>
    /// <para>
    /// Restated rather than mutated, because a <c>Diagnostic</c>'s message arguments are
    /// fixed at creation. The location is the provisional one's, so a
    /// <c>#pragma warning disable</c> around the author's <c>.Emit</c> still suppresses it.
    /// </para>
    /// </remarks>
    private static void Produce(
        SourceProductionContext production,
        AnalysisResult result,
        ImmutableArray<JsonContextModel?> jsonContexts)
    {
        var contexts = jsonContexts
            .Where(static c => c is not null)
            .Select(static c => c!)
            .ToList();

        var durable = string.Equals(result.Model?.Profile, "Durable", StringComparison.Ordinal);

        foreach (var diagnostic in result.Diagnostics)
        {
            if (string.Equals(diagnostic.Id, EmitDiagnosticId, StringComparison.Ordinal))
            {
                Report(production, SettleEmitDiagnostic(diagnostic, contexts, durable));
                continue;
            }

            if (string.Equals(diagnostic.Id, StateDiagnosticId, StringComparison.Ordinal))
            {
                Report(production, SettleStateDiagnostic(diagnostic, contexts));
                continue;
            }

            production.ReportDiagnostic(diagnostic);
        }

        if (!result.IsSuccess || result.Model is null)
        {
            // Diagnostics have been reported; emitting a partial plan on top of them
            // would bury the real error under a cascade of "type not found".
            return;
        }

        production.AddSource(
            FlowEmitter.FileNameFor(result.Model),
            SourceText.From(FlowEmitter.Emit(result.Model, contexts), Encoding.UTF8));
    }

    /// <summary>
    /// Turns one provisional <c>FLOWX1024</c> into the diagnostic to report, or into nothing.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the event is staged and published, so there is nothing to warn about.
    /// </returns>
    private static Diagnostic? SettleEmitDiagnostic(
        Diagnostic provisional, List<JsonContextModel> contexts, bool durable)
    {
        provisional.Properties.TryGetValue(EmitReasons.ContractProperty, out var contract);
        provisional.Properties.TryGetValue(EmitReasons.NameProperty, out var name);

        if (!durable)
        {
            return Restate(provisional, name, EmitReasons.Ephemeral);
        }

        if (contract is null)
        {
            // The type argument did not resolve, so analysis produced no step either. C# is
            // already reporting something more useful about the same span.
            return null;
        }

        return FlowEmitter.SingleContextFor(contexts, contract) is null
            ? Restate(
                provisional,
                name,
                string.Format(CultureInfo.InvariantCulture, EmitReasons.NoSerializerContextFormat, contract))
            : null;
    }

    private static Diagnostic Restate(Diagnostic provisional, string? name, string reason) =>
        Diagnostic.Create(
            FlowXDiagnostics.EmitIsNotYetPublished,
            provisional.Location,
            provisional.Properties,
            name ?? "TEvent",
            reason);

    /// <summary>
    /// Turns one provisional <c>FLOWX1006</c> into the diagnostic to report, or into nothing.
    /// </summary>
    /// <returns>
    /// <c>null</c> when exactly one serialiser context declares the contract, so the generated
    /// payload writer can name metadata for it and there is nothing to report.
    /// </returns>
    /// <remarks>
    /// The profile is not re-read here, unlike <c>FLOWX1024</c>'s settlement: analysis raises
    /// this provisional only for a <c>Durable</c> flow, because an ephemeral one keeps no
    /// journal and the rule protects nothing there.
    /// </remarks>
    private static Diagnostic? SettleStateDiagnostic(
        Diagnostic provisional, List<JsonContextModel> contexts)
    {
        provisional.Properties.TryGetValue(StateBagReasons.ContractProperty, out var contract);
        provisional.Properties.TryGetValue(StateBagReasons.NameProperty, out var name);
        provisional.Properties.TryGetValue(StateBagReasons.FlowProperty, out var flowId);

        if (contract is null)
        {
            // The contract did not resolve, so analysis produced no usable name either. C# is
            // already reporting something more useful about the same span.
            return null;
        }

        return FlowEmitter.SingleContextFor(contexts, contract) is null
            ? Diagnostic.Create(
                FlowXDiagnostics.StateIsNotSerialisable,
                provisional.Location,
                provisional.Properties,
                name ?? contract,
                flowId ?? string.Empty,
                string.Format(
                    CultureInfo.InvariantCulture,
                    StateBagReasons.NoSerializerContextFormat,
                    contract))
            : null;
    }

    /// <summary>Reports a settled diagnostic, or nothing when it settled to nothing.</summary>
    private static void Report(SourceProductionContext production, Diagnostic? settled)
    {
        if (settled is not null)
        {
            production.ReportDiagnostic(settled);
        }
    }
}
