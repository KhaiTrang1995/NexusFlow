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
        string? declarationLocation = null)
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

    /// <summary>The steps, in declaration order.</summary>
    public IReadOnlyList<StepModel> Steps { get; }

    /// <summary><c>file:line</c> of the declaration, for diagnostics and the manifest.</summary>
    public string? DeclarationLocation { get; }

    /// <summary>Fully-qualified name of the declaring type.</summary>
    public string FullTypeName => string.IsNullOrEmpty(ContainingNamespace)
        ? TypeName
        : ContainingNamespace + "." + TypeName;

    /// <summary>True when any step declared a compensation.</summary>
    public bool HasCompensation => Steps.Any(step => step.CompensationTypeName != null);

    /// <summary>Every distinct capability type the flow invokes, compensations included.</summary>
    public IReadOnlyList<string> ReferencedCapabilities => Steps
        .SelectMany(step => new[] { step.CapabilityTypeName, step.CompensationTypeName })
        .Where(name => name != null)
        .Select(name => name!)
        .Distinct(System.StringComparer.Ordinal)
        .OrderBy(name => name, System.StringComparer.Ordinal)
        .ToList();
}
