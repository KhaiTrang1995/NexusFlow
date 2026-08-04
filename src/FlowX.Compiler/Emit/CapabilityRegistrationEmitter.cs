using System.Collections.Generic;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Turns the capabilities a dispatcher takes by constructor into the registrations a composition
/// root would otherwise write by hand, one line per type.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="EndpointEmitter"/> declined to emit these, and the reason it gave has
/// stopped being true.</strong> It said a service lifetime is declared nowhere in the flow model,
/// so a generator picking one would be inventing a fact. The runtime has since settled the
/// question by construction: <c>FlowCatalog</c>, <c>FlowBusCatalog</c>, <c>FlowChangeCatalog</c>,
/// <c>FlowScheduleCatalog</c> and <c>FlowStreamCatalog</c> each hold a <em>resolved</em>
/// dispatcher for the life of the node — a recovery sweep resumes an instance days after the
/// invocation that started it, and there is no scope left to resolve one from. FlowX creates no
/// DI scope of its own anywhere. So singleton is not a preference this file expresses; it is the
/// only lifetime that works on every path, and emitting it publishes a fact rather than inventing
/// one.
/// </para>
/// <para>
/// <strong><c>TryAddSingleton</c>, so a hand-written registration still wins.</strong> A capability
/// behind an interface, a decorated one, or one with a factory is registered before this call and
/// is left alone. So is a scoped one: a flow reached only over HTTP — the MCP surface included,
/// being a route — runs inside the request's scope, which is what <c>samples/ai-agent</c> needs
/// for a capability that talks back to its caller. The generated line is the default, not the law.
/// </para>
/// <para>
/// Pure, like <see cref="FlowEmitter"/>: two lists in, a string out, no Roslyn.
/// </para>
/// </remarks>
public static class CapabilityRegistrationEmitter
{
    /// <summary>The file the emitted registrations are written to.</summary>
    public const string FileName = "FlowXCapabilities.g.cs";

    private const string Services = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";

    private const string TryAdd =
        "global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";

    private const string Declaration = "global::FlowX.Hosting.FlowXTriggerDeclaration";

    /// <summary>Emits the capability and dispatcher registrations for this compilation.</summary>
    /// <param name="assemblyName">The compilation's assembly name, which names the class.</param>
    /// <param name="capabilities">Every capability type a dispatcher takes, ordinally sorted.</param>
    /// <param name="dispatchers">Every flow whose dispatcher this application can run, ordinally sorted.</param>
    /// <param name="declarations">
    /// Every address a flow declared, or empty when the host cannot check them. Emitted here
    /// rather than in a file of its own because this method is the one call every application
    /// makes before <c>Build()</c>, and a declaration recorded anywhere later would be recorded
    /// after the check that reads it.
    /// </param>
    /// <exception cref="System.ArgumentNullException">Any list is null.</exception>
    public static string Emit(
        string assemblyName,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> dispatchers,
        IReadOnlyList<DeclaredTriggerModel> declarations)
    {
        if (capabilities is null)
        {
            throw new System.ArgumentNullException(nameof(capabilities));
        }

        if (dispatchers is null)
        {
            throw new System.ArgumentNullException(nameof(dispatchers));
        }

        if (declarations is null)
        {
            throw new System.ArgumentNullException(nameof(declarations));
        }

        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated");
        writer.OpenBrace();

        EmitClass(writer, ClassNameFor(assemblyName), capabilities, dispatchers, declarations);

        writer.CloseBrace();

        return writer.ToString();
    }

    /// <summary>The class name for one assembly's registrations, e.g. <c>CrmCapabilities</c>.</summary>
    /// <remarks>
    /// Named from the assembly for <see cref="BusEmitter.ClassNameFor"/>'s reason: a solution with
    /// two flow libraries would otherwise emit two <c>FlowX.Generated.FlowXCapabilities</c>, and
    /// the application referencing both would get an ambiguous call with no way to name which.
    /// </remarks>
    /// <param name="assemblyName">The compilation's assembly name.</param>
    public static string ClassNameFor(string assemblyName)
    {
        var identifier = new System.Text.StringBuilder();

        foreach (var character in assemblyName ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                identifier.Append(character);
            }
        }

        if (identifier.Length == 0 || char.IsDigit(identifier[0]))
        {
            return "FlowXCapabilities";
        }

        return identifier.Append("Capabilities").ToString();
    }

    private static string Quote(string value) =>
        "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void EmitClass(
        SourceWriter writer,
        string className,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> dispatchers,
        IReadOnlyList<DeclaredTriggerModel> declarations)
    {
        writer.Line("/// <summary>The capabilities this application's flows invoke.</summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// Generated from the constructors the dispatchers declare, so a capability a flow");
        writer.Line("/// steps through cannot be missing from the container. Singleton because the");
        writer.Line("/// catalogues hold a resolved dispatcher for the life of the node and no scope exists");
        writer.Line("/// to resolve another from; <c>TryAdd</c> so a hand-written registration still wins.");
        writer.Line("/// </remarks>");
        writer.Line("public static class " + className);
        writer.OpenBrace();

        writer.Line("/// <summary>Registers every capability and dispatcher this application can run.</summary>");
        writer.Line("/// <param name=\"services\">The collection being built.</param>");
        writer.Line("/// <returns>The same collection, so registrations chain.</returns>");
        writer.Line("public static " + Services + " AddFlowXCapabilities(");
        writer.Line("    this " + Services + " services)");
        writer.OpenBrace();

        foreach (var capability in capabilities)
        {
            writer.Line(TryAdd + ".TryAddSingleton<global::" + capability + ">(services);");
        }

        if (capabilities.Count > 0 && dispatchers.Count > 0)
        {
            writer.Line();
        }

        foreach (var dispatcher in dispatchers)
        {
            writer.Line(TryAdd + ".TryAddSingleton<global::" + dispatcher + ".Dispatcher>(services);");
        }

        if (declarations.Count > 0)
        {
            writer.Line();
            writer.Line("// What each flow declared, so the host can refuse to start when nothing serves it.");

            foreach (var declaration in declarations)
            {
                writer.Line(
                    Declaration + ".Declare(services, " +
                    Quote(declaration.FlowId) + ", " +
                    Quote(declaration.Version) + ", " +
                    Quote(declaration.Kind) + ", " +
                    Quote(declaration.Address) + ");");
            }
        }

        writer.Line();
        writer.Line("return services;");

        writer.CloseBrace();
        writer.CloseBrace();
    }
}
