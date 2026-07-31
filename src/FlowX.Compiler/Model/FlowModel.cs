using System.Collections.Generic;
using System.Linq;

namespace FlowX.Compiler.Model;

/// <summary>
/// What a <c>Define</c> method declared, expressed without a single Roslyn type.
/// </summary>
/// <remarks>
/// <para>
/// This layer exists because of risk <strong>R1</strong> in
/// <a href="../../../docs/05-Architecture.md">05-Architecture §11</a>: source
/// generators become a platform's own legacy when their parsing, their business
/// rules and their string emission are tangled together, because none of the three
/// can then be tested without the other two.
/// </para>
/// <para>
/// So the compiler is three layers with one direction of flow:
/// <c>Analysis (Roslyn) → Model (this, pure) → Emit (strings, pure)</c>.
/// Only the analysis layer references Roslyn. Everything that decides <em>what a
/// flow means</em> lives here and is unit-testable with plain objects, and
/// everything that decides <em>what text to write</em> reads only this.
/// </para>
/// <para>
/// If a future change makes this type reference a Roslyn symbol, R1 has
/// materialised and the fitness function <c>ModelLayerHasNoRoslynDependency</c>
/// will say so.
/// </para>
/// </remarks>
public sealed class FlowModel
{
    /// <summary>Creates a model of one declared flow.</summary>
    public FlowModel(
        string flowId,
        string version,
        string profile,
        string? deadline,
        string containingNamespace,
        string typeName,
        string inputTypeName,
        string outputTypeName,
        IReadOnlyList<StepModel> steps,
        string? declarationLocation = null,
        string? returnProjection = null,
        string? returnLocation = null,
        IReadOnlyList<string>? usings = null,
        IReadOnlyList<string>? sensitiveInputMembers = null,
        IReadOnlyList<string>? sensitiveOutputMembers = null)
    {
        FlowId = flowId;
        Version = version;
        Profile = profile;
        Deadline = deadline;
        ContainingNamespace = containingNamespace;
        TypeName = typeName;
        InputTypeName = inputTypeName;
        OutputTypeName = outputTypeName;
        Steps = steps;
        DeclarationLocation = declarationLocation;
        ReturnProjection = returnProjection;
        ReturnLocation = returnLocation;
        Usings = usings ?? System.Array.Empty<string>();
        SensitiveInputMembers = sensitiveInputMembers ?? System.Array.Empty<string>();
        SensitiveOutputMembers = sensitiveOutputMembers ?? System.Array.Empty<string>();

        // Both derived lists are folded out of the step tree here, once, from a single
        // walk of it. Every input they read is complete by the time this constructor
        // runs — the analysis layer finishes the step tree before it builds the model —
        // so the answers cannot change later, and computing them now keeps the type
        // free of mutable state that a generator's cached instances would share across
        // threads. The alternative, a lazily-filled field, would buy nothing: every
        // model that is built is handed to the emitter, which reads both of these
        // several times per flow.
        var allSteps = AllSteps.ToList();

        ComposedFlows = allSteps
            .Where(step => step.Kind == StepKindModel.SubFlow && step.SubFlowTypeName != null)
            .Select(step => step.SubFlowTypeName!)
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(name => name, System.StringComparer.Ordinal)
            .ToList();

        ReferencedCapabilities = allSteps
            .SelectMany(step => new[] { step.CapabilityTypeName, step.CompensationTypeName })
            .Where(name => name != null)
            .Select(name => name!)
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(name => name, System.StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Business identity from <c>[Flow]</c>, e.g. <c>order.place</c>.</summary>
    public string FlowId { get; }

    /// <summary>SemVer from <c>[Flow]</c>.</summary>
    public string Version { get; }

    /// <summary>Execution profile name: <c>Ephemeral</c>, <c>Durable</c> or <c>Streaming</c>.</summary>
    public string Profile { get; }

    /// <summary>ISO-8601 duration from <c>[FlowDeadline]</c>, or <c>null</c> for the runtime default.</summary>
    public string? Deadline { get; }

    /// <summary>Namespace of the declaring type, so the generated partial lands beside it.</summary>
    public string ContainingNamespace { get; }

    /// <summary>Simple name of the declaring type.</summary>
    public string TypeName { get; }

    /// <summary>Fully-qualified input contract.</summary>
    public string InputTypeName { get; }

    /// <summary>Fully-qualified output contract.</summary>
    public string OutputTypeName { get; }

    /// <summary>
    /// The top-level steps, in declaration order. Steps inside a conditional's blocks
    /// hang off the conditional and are <em>not</em> here.
    /// </summary>
    /// <remarks>
    /// This is the declared shape, which is what the manifest publishes and what a reader
    /// recognises as their own <c>Define</c> method. Anything that needs the whole flow —
    /// descriptors, the dispatcher's switch, the capability list — wants
    /// <see cref="AllSteps"/> instead.
    /// </remarks>
    public IReadOnlyList<StepModel> Steps { get; }

    /// <summary>Every step of the flow, nested ones included, in flat-layout order.</summary>
    /// <remarks>
    /// The order matters: it is the order the compiled step array runs in, so emitting a
    /// dispatcher case per entry produces a switch whose cases ascend.
    /// </remarks>
    public IEnumerable<StepModel> AllSteps => Steps.SelectMany(step => step.SelfAndNested);

    /// <summary><c>file:line</c> of the declaration, for diagnostics and the manifest.</summary>
    public string? DeclarationLocation { get; }

    /// <summary>
    /// Source text of the <c>.Return(...)</c> lambda, or <c>null</c> when the flow
    /// declares no output projection.
    /// </summary>
    /// <remarks>
    /// Deliberately the author's own text, copied verbatim rather than reconstructed.
    /// A reconstruction would have to re-render every expression form C# has, and would
    /// be wrong on the first one it had not met; copying is exact by construction, and
    /// the <c>#line</c> directive around it points any error back at the real source.
    /// </remarks>
    public string? ReturnProjection { get; }

    /// <summary><c>file:line</c> of the <c>.Return(...)</c> call.</summary>
    public string? ReturnLocation { get; }

    /// <summary>Input-contract members carrying <c>[Sensitive]</c>, ordinally sorted.</summary>
    /// <remarks>
    /// Recorded in the manifest so a reviewer, <c>flowx diff</c> or an agent can see which
    /// fields carry secrets. It is not redaction — nothing in this release strips these
    /// values from anything. Saying which members are sensitive is the prerequisite for
    /// that, and is worth having on its own; claiming more would repeat the mistake the
    /// attribute's own documentation made.
    /// </remarks>
    public IReadOnlyList<string> SensitiveInputMembers { get; }

    /// <summary>Output-contract members carrying <c>[Sensitive]</c>, ordinally sorted.</summary>
    public IReadOnlyList<string> SensitiveOutputMembers { get; }

    /// <summary>
    /// The <c>using</c> directives of the file that declared the flow, in source order.
    /// </summary>
    /// <remarks>
    /// Carried because <see cref="ReturnProjection"/> is copied verbatim: the author's
    /// lambda names types the way their file's usings allow, so emitting it into a file
    /// with different usings would not compile. Copying the directives makes the emitted
    /// expression resolve exactly as it did where it was written.
    /// </remarks>
    public IReadOnlyList<string> Usings { get; }

    /// <summary>Fully-qualified name of the declaring type.</summary>
    public string FullTypeName => string.IsNullOrEmpty(ContainingNamespace)
        ? TypeName
        : ContainingNamespace + "." + TypeName;

    /// <summary>True when any step declared a compensation, inside a branch or not.</summary>
    public bool HasCompensation => AllSteps.Any(step => step.CompensationTypeName != null);

    /// <summary>Every distinct flow type this flow composes, ordinally sorted.</summary>
    /// <remarks>
    /// <para>
    /// Drives the generated dispatcher's constructor: a composing flow takes the child's
    /// dispatcher the same way it takes a capability, so the child is injected rather than
    /// constructed — which is what lets the child's own capabilities be resolved by the
    /// container and keeps this flow from having to know them.
    /// </para>
    /// <para>
    /// Computed once, in the constructor: the emitter reads it more than once per flow,
    /// and a property that rebuilt the list per read would hand out a fresh copy every
    /// time for an answer that cannot change.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> ComposedFlows { get; }

    /// <summary>Every distinct capability type the flow invokes, compensations included.</summary>
    /// <remarks>Computed once, in the constructor, for the reason on <see cref="ComposedFlows"/>.</remarks>
    public IReadOnlyList<string> ReferencedCapabilities { get; }
}
