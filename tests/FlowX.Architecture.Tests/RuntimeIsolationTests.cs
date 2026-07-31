using Mono.Cecil;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The two rules docs/03-Design-Principles.md states as IL scans: P4's reflection ban and
/// P7's ban on mutable static state in the runtime.
/// </summary>
/// <remarks>
/// <para>
/// Both are named in docs/05-Architecture.md §12 — as <c>NoReflectionOnHotPath</c> and
/// <c>RuntimeHasNoMutableStatics</c> — and neither existed. The table has been read as
/// though they did, which is the expensive kind of absence: a reviewer who sees the row
/// stops looking for the rule.
/// </para>
/// <para>
/// They are checked against the built assemblies rather than the source because that is
/// what the principles say and what the failure modes require. Reflection can arrive
/// through a generator, through an extension method whose return type is a
/// <c>MemberInfo</c>, or through a helper that never spells the namespace; a mutable static
/// can arrive as a field the source calls <c>readonly</c> on a type that is not. The IL is
/// the artifact that ships, so the IL is what is asked.
/// </para>
/// </remarks>
public sealed class RuntimeIsolationTests
{
    /// <summary>
    /// The assemblies a flow invocation actually runs through.
    /// </summary>
    /// <remarks>
    /// P4 and P7 both name <c>FlowX.Runtime</c>. <c>FlowX.Core</c> is included because the
    /// execution plan, the step graph and the compensation stack are read on every
    /// invocation — the phrase "hot path" describes a code path, and that path does not
    /// stop at an assembly boundary. <c>FlowX.Abstractions</c> is included because every
    /// step's input and output crosses it.
    /// </remarks>
    private static readonly string[] HotPath = ["FlowX.Abstractions", "FlowX.Core", "FlowX.Runtime"];

    /// <summary>
    /// Namespaces whose whole purpose is to inspect or invoke members at run time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>System.Reflection</c> is the one P4 names. The rest are the ways the same thing
    /// is reached without it: <c>Activator</c> constructs a type nobody named at compile
    /// time, <c>AssemblyLoadContext</c> brings in code the manifest never saw, and the
    /// C# runtime binder is what <c>dynamic</c> compiles into. Each defeats NativeAOT
    /// (constraint C2) in the same way and for the same reason.
    /// </para>
    /// <para>
    /// <c>typeof(T)</c> and <c>GetType()</c> are deliberately <em>not</em> here. Both live
    /// in <c>System</c>, both are resolved from a metadata token the compiler baked in, and
    /// the runtime uses the first as a dictionary key in the flow's state bag — the
    /// compile-time technique P4 asks for, not an escape from it. What P4 forbids is
    /// discovering members at run time, and that always ends in one of the namespaces
    /// below.
    /// </para>
    /// </remarks>
    private static readonly string[] ReflectionNamespaces =
    [
        "System.Reflection",
        "System.Runtime.Loader",
        "Microsoft.CSharp.RuntimeBinder",
    ];

    /// <summary>Types outside those namespaces that do the same job.</summary>
    private static readonly string[] ReflectionTypes =
    [
        "System.Activator",
        "System.AppDomain",
    ];

    /// <summary>
    /// Members that read a name off a type token and discover nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <c>typeof(T).Name</c> compiles to a call on
    /// <c>System.Reflection.MemberInfo</c> — <c>Name</c> is declared there, not on
    /// <c>System.Type</c> — so a namespace-only rule reports every diagnostic message in the
    /// runtime that says which contract was missing. Those messages are the difference
    /// between "no step produced a Reservation" and "no step produced a value", and the only
    /// way to make a namespace-only rule pass would be to delete them.
    /// </para>
    /// <para>
    /// The exemption is one property. <c>Name</c> is answered from the metadata token the
    /// compiler already emitted: it discovers nothing, constructs nothing, is trim-safe, and
    /// NativeAOT resolves it statically. Everything that <em>looks up</em> a member —
    /// <c>GetMethod</c>, <c>GetProperties</c>, <c>Invoke</c>, <c>CreateInstance</c> — returns
    /// or accepts a reflection type and is still caught, including when it is reached
    /// through a <c>MemberInfo</c> variable, because holding one is itself a usage with no
    /// <c>Via</c> to exempt.
    /// </para>
    /// </remarks>
    private static readonly string[] MetadataNameReads = ["get_Name"];

    /// <summary>
    /// Principle P4: nothing on the hot path discovers or invokes a member at run time.
    /// </summary>
    /// <remarks>
    /// The point is not that reflection is slow, though it is. It is that a platform which
    /// resolves anything at run time cannot be published with NativeAOT (constraint C2) and
    /// cannot have a manifest that is complete by construction — the two claims ADR-0002 and
    /// ADR-0005 rest on. Dispatch is a generated <c>switch</c> precisely so this gate has
    /// nothing to find.
    /// </remarks>
    [Fact]
    public void NoReflectionOnHotPath()
    {
        var findings = new List<string>();

        foreach (var assembly in HotPath)
        {
            using var module = CompiledAssemblies.Read(assembly);

            findings.AddRange(ReflectionUsages(module)
                .Select(u => $"{assembly}: {u.Member} uses {u.UsedName}"));
        }

        findings.ShouldBeEmpty(
            "Reflection on the execution path. NativeAOT cannot see it (constraint C2) and " +
            "the manifest cannot describe it (ADR-0005) — anything derivable at build time " +
            "is computed at build time (principle P4):" +
            Environment.NewLine + string.Join(Environment.NewLine, findings.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The reflection scan can see reflection.
    /// </summary>
    /// <remarks>
    /// Without this, the gate above degrades silently into a pass. A renamed namespace
    /// constant, an operand shape the walk does not decode, an assembly it failed to open —
    /// each produces an empty finding list, and an empty list satisfies "no reflection"
    /// perfectly. This assembly reflects heavily and by design
    /// (<c>Directory.Build.props</c> says so); if the scan cannot find it here, it is not
    /// finding it anywhere.
    /// </remarks>
    [Fact]
    public void TheReflectionScanFindsReflectionWhereItIsExpected()
    {
        using var module = CompiledAssemblies.Read("FlowX.Architecture.Tests");

        ReflectionUsages(module).ShouldNotBeEmpty(
            "The IL walk found no reflection in the architecture tests, which read the " +
            "contract surface with reflection on purpose. NoReflectionOnHotPath is now " +
            "passing vacuously.");
    }

    /// <summary>
    /// Principle P7: a runtime instance holds no mutable static state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scale-out is a replica count, and a rolling update must never lose an in-flight flow.
    /// Neither is true of a process that accumulates state nobody journaled: a static field
    /// written during execution is state that exists on one replica, survives no restart,
    /// and is invisible to the journal that is supposed to be the only durable thing.
    /// </para>
    /// <para>
    /// The rule is about mutability, not about statics. A <c>static readonly</c> descriptor
    /// built once at type initialisation is exactly what P4 asks for — the generated plan is
    /// one — and a <c>const</c> is not state at all. What is forbidden is a static field
    /// that can be assigned after initialisation, because that is the one shape that makes
    /// two concurrent flows able to see each other.
    /// </para>
    /// <para>
    /// <c>[ThreadStatic]</c> is included. It narrows a race to one thread and keeps every
    /// other property that makes this rule exist: it survives the invocation, it is invisible
    /// to the journal, and with pooled threads it is read by the next flow to run there.
    /// </para>
    /// </remarks>
    [Fact]
    public void RuntimeHasNoMutableStatics()
    {
        using var module = CompiledAssemblies.Read("FlowX.Runtime");

        var fields = IlSurvey.AllTypes(module)
            .SelectMany(static type => type.Fields)
            .Where(static field => field.IsStatic)
            .ToList();

        fields.ShouldNotBeEmpty(
            "No static field at all was found in FlowX.Runtime, which has several. The " +
            "survey has stopped seeing fields, so this gate is passing vacuously.");

        var mutable = fields
            .Where(static field => !field.IsInitOnly && !field.IsLiteral)
            .Where(static field => !IlSurvey.IsCompilerGenerated(field))
            .Select(static field => $"{field.DeclaringType.FullName}.{field.Name} : {field.FieldType.Name}")
            .ToList();

        mutable.ShouldBeEmpty(
            "Mutable static state in FlowX.Runtime. A replica that remembers something " +
            "between invocations is not stateless, and a rolling update loses whatever it " +
            "remembered (principle P7). Put it on the invocation, or journal it:" +
            Environment.NewLine + string.Join(Environment.NewLine, mutable));
    }

    // ------------------------------------------------------------------ helpers

    private static List<Usage> ReflectionUsages(ModuleDefinition module) =>
        IlSurvey.AllTypes(module)
            .Where(static type => !IlSurvey.IsCompilerGenerated(type))
            .SelectMany(IlSurvey.Usages)
            .Where(static usage => IsReflection(usage))
            .ToList();

    private static bool IsReflection(Usage usage)
    {
        if (usage.Via is not null && MetadataNameReads.Contains(usage.Via, StringComparer.Ordinal))
        {
            return false;
        }

        return ReflectionNamespaces.Any(prefix =>
            usage.UsedNamespace.Equals(prefix, StringComparison.Ordinal)
            || usage.UsedNamespace.StartsWith(prefix + ".", StringComparison.Ordinal))
            || ReflectionTypes.Contains(usage.UsedName, StringComparer.Ordinal);
    }
}
