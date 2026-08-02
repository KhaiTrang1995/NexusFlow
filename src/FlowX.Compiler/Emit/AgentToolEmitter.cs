using System.Collections.Generic;
using System.Linq;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Turns each flow's <c>[AgentTrigger]</c> into the tool binding a composition root would
/// otherwise write by hand — or, before this existed, could not write at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The arrangement <see cref="EndpointEmitter"/>, <see cref="ScheduleEmitter"/> and
/// <see cref="BusEmitter"/> have.</strong> The emitted file is C# in the <em>user's</em>
/// assembly, calling <c>FlowX.Mcp.FlowAgentToolRegistration</c> by name; this project links
/// against nothing, and the only thing the generator knows about the agent surface is that
/// string, which <see cref="FlowPlanGenerator"/> looks up in the compilation before asking
/// for any of this. An application that does not reference <c>FlowX.Mcp</c> gets no file, no
/// type and no IL.
/// </para>
/// <para>
/// <strong>Where this one differs from the other three, and why.</strong> Each of them
/// copies the trigger's <em>address</em> — a route, a cron expression, a topic and group —
/// off the <see cref="TriggerModel"/> the manifest published, because a router, a scheduler
/// and a consumer all need the address before anything reads a manifest. An agent tool has
/// no address: it is named by the flow's id and described by the manifest's own
/// <c>trigger.description</c>, <c>capability.authorization</c> and
/// <c>capability.sideEffects</c>. So this emitter copies <em>nothing</em> from the trigger
/// but the flow id that joins the two, and the entire published surface is read back out of
/// <c>FlowX.Generated.FlowXManifest.Json</c> at run time.
/// </para>
/// <para>
/// That is the load-bearing decision. Emitting the description and the annotations here
/// would be cheaper at run time and would create a second copy of the tool surface — in
/// generated code, invisible to <c>flowx diff</c>, and free to disagree with the document
/// an agent was told to trust. The manifest constant this file hands over is the same bytes
/// the build published, so <c>tools/list</c> cannot describe a tool differently from the way
/// the manifest does.
/// </para>
/// <para>
/// Pure, like <see cref="FlowEmitter"/>: a model in, a string out, no Roslyn.
/// </para>
/// </remarks>
public static class AgentToolEmitter
{
    /// <summary>The file the emitted bindings are written to.</summary>
    public const string FileName = "FlowXAgentTools.g.cs";

    /// <summary>
    /// The manifest the emitted registration hands to the agent surface.
    /// </summary>
    /// <remarks>
    /// Emitted by <c>FlowPlanGenerator.ProduceManifest</c> into the same compilation, from
    /// the same collected flows, so the constant is always there when a binding is.
    /// </remarks>
    public const string ManifestConstant = "global::FlowX.Generated.FlowXManifest.Json";

    private const string Services = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";

    private const string JsonContext = "global::System.Text.Json.Serialization.JsonSerializerContext";

    private const string Registration = "global::FlowX.Mcp.FlowAgentToolRegistration";

    private const string Dispatcher = "global::FlowX.Runtime.IStepDispatcher";

    /// <summary>Emits the tool bindings for every agent-triggered flow.</summary>
    /// <param name="assemblyName">The compilation's assembly name, which names the class.</param>
    /// <param name="tools">The tools to bind, in the order they should be registered.</param>
    public static string Emit(string assemblyName, IReadOnlyList<AgentToolModel> tools)
    {
        if (tools is null)
        {
            throw new System.ArgumentNullException(nameof(tools));
        }

        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated");
        writer.OpenBrace();

        EmitClass(writer, ClassNameFor(assemblyName), tools);

        writer.CloseBrace();

        return writer.ToString();
    }

    /// <summary>
    /// The class name for one assembly's tool bindings, e.g. <c>EcommerceAgentTools</c>.
    /// </summary>
    /// <remarks>
    /// Named from the assembly rather than fixed, for <see cref="EndpointEmitter.ClassNameFor"/>'s
    /// reason: a solution with two flow libraries would otherwise emit two
    /// <c>FlowX.Generated.FlowXAgentTools</c>, and the application referencing both would get an
    /// ambiguous <c>services.AddFlowXAgentTools()</c> with no way to disambiguate it.
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
            return "FlowXAgentTools";
        }

        return identifier.Append("AgentTools").ToString();
    }

    private static void EmitClass(
        SourceWriter writer, string className, IReadOnlyList<AgentToolModel> tools)
    {
        writer.Line("/// <summary>The agent tools this application's flows declare.</summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// One method per <c>[AgentTrigger]</c>. Each binds a flow's plan, dispatcher,");
        writer.Line("/// projection and contract metadata to the tool the manifest publishes for it, and");
        writer.Line("/// carries none of the tool's description: the name, the description, the required");
        writer.Line("/// permissions, the declared side effects and the confirmation requirement are all");
        writer.Line("/// read out of <c>FlowXManifest.Json</c> at run time, so what an agent is told and");
        writer.Line("/// what <c>flowx.manifest.json</c> says are one document rather than two.");
        writer.Line("/// </remarks>");
        writer.Line("public static class " + className);
        writer.OpenBrace();

        EmitAddAll(writer, tools);

        foreach (var tool in tools)
        {
            writer.Line();
            EmitTool(writer, tool);
        }

        writer.CloseBrace();
    }

    /// <summary>
    /// Emits <c>AddFlowXAgentTools</c>: the manifest, then every bound flow, in one call.
    /// </summary>
    /// <remarks>
    /// The no-argument form appears only when every tool resolved a serialiser context of
    /// its own, for <see cref="EndpointEmitter"/>'s reason: registering the tools that could
    /// be resolved and silently skipping the rest would produce a host whose
    /// <c>tools/list</c> advertises a flow no call can reach — and <c>McpServer</c> refuses
    /// to start in exactly that state, so the failure would be a start-up crash rather than
    /// a compile error. Its absence is the loud version of the same fact, at the call site.
    /// </remarks>
    private static void EmitAddAll(SourceWriter writer, IReadOnlyList<AgentToolModel> tools)
    {
        if (tools.All(t => t.JsonContextTypeName != null))
        {
            writer.Line("/// <summary>Registers every flow in this application that declares an <c>[AgentTrigger]</c>.</summary>");
            writer.Line("/// <param name=\"services\">The container.</param>");
            writer.Line("public static " + Services + " AddFlowXAgentTools(");
            writer.Line("    this " + Services + " services)");
            writer.OpenBrace();
            EmitManifest(writer);

            foreach (var tool in tools)
            {
                writer.Line(tool.MethodName + "(services);");
            }

            writer.Line();
            writer.Line("return services;");
            writer.CloseBrace();
            writer.Line();
        }

        writer.Line("/// <summary>Registers every flow that declares an <c>[AgentTrigger]</c>, through one serialiser.</summary>");
        writer.Line("/// <param name=\"services\">The container.</param>");
        writer.Line("/// <param name=\"json\">The source-generated context carrying every contract an agent can reach.</param>");
        writer.Line("public static " + Services + " AddFlowXAgentTools(");
        writer.Line("    this " + Services + " services,");
        writer.Line("    " + JsonContext + " json)");
        writer.OpenBrace();
        EmitManifest(writer);

        foreach (var tool in tools)
        {
            writer.Line(tool.MethodName + "(services, json);");
        }

        writer.Line();
        writer.Line("return services;");
        writer.CloseBrace();
    }

    /// <summary>
    /// Hands the compiled-in manifest to the agent surface.
    /// </summary>
    /// <remarks>
    /// The one line that makes <c>tools/list</c> a projection. <c>FlowXManifest.Json</c> is
    /// the document this same build wrote — byte-identical to the published
    /// <c>flowx.manifest.json</c>, because it is the string the manifest was written from —
    /// so there is no second artifact for the tool surface to be derived from and none for
    /// it to drift against.
    /// </remarks>
    private static void EmitManifest(SourceWriter writer)
    {
        writer.Line(Registration + ".UseManifest(services, " + ManifestConstant + ");");
        writer.Line();
    }

    private static void EmitTool(SourceWriter writer, AgentToolModel tool)
    {
        var flow = "global::" + tool.FlowTypeName;

        if (tool.JsonContextTypeName != null)
        {
            writer.Line(
                "/// <summary>Binds <c>" + tool.FlowId + "</c> as an agent tool.</summary>");
            writer.Line("/// <param name=\"services\">The container.</param>");
            writer.Line("public static " + Services + " " + tool.MethodName + "(");
            writer.Line("    this " + Services + " services) =>");
            writer.Line(
                "    " + tool.MethodName + "(services, global::" + tool.JsonContextTypeName +
                ".Default);");
            writer.Line();
        }

        writer.Line(
            "/// <summary>Binds <c>" + tool.FlowId +
            "</c> as an agent tool, through the given serialiser.</summary>");
        writer.Line("/// <param name=\"services\">The container.</param>");
        writer.Line("/// <param name=\"json\">A context declaring both of this flow's contracts.</param>");
        writer.Line("public static " + Services + " " + tool.MethodName + "(");
        writer.Line("    this " + Services + " services,");
        writer.Line("    " + JsonContext + " json) =>");
        writer.Line(
            "    " + Registration + ".Add<global::" + tool.InputTypeName +
            ", global::" + tool.OutputTypeName + ">(");
        writer.Line("        services,");
        writer.Line("        " + Quote(tool.FlowId) + ",");
        writer.Line("        " + flow + ".Plan,");
        writer.Line("        static provider => (" + Dispatcher + ")global::Microsoft.Extensions.DependencyInjection");
        writer.Line("            .ServiceProviderServiceExtensions");
        writer.Line("            .GetRequiredService<" + flow + ".Dispatcher>(provider),");
        writer.Line("        " + flow + ".Projection,");
        writer.Line("        json);");
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
