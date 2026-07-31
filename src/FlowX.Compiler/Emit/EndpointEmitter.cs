using System.Collections.Generic;
using System.Linq;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Turns each flow's <c>[HttpTrigger]</c> into the endpoint registration a composition
/// root would otherwise write by hand.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Where this code lives, and why it can.</strong> The emitted file is C# in the
/// <em>user's</em> assembly, calling <c>FlowX.Http.FlowEndpointExtensions.MapFlow</c> by
/// name. This project links against nothing, and least of all against a plugin: the only
/// thing the generator knows about the HTTP transport is the string
/// <c>"FlowX.Http.FlowEndpointExtensions"</c>, which <see cref="FlowPlanGenerator"/>
/// looks up in the compilation before asking for any of this. So
/// <c>RuntimeDoesNotReferenceAnyPlugin</c> is not merely still green — there is nothing
/// for it to look at, because the reference the generated code needs is one the user's
/// project already declared. An application that does not reference
/// <c>FlowX.Http</c> gets no file, no type and no IL, which is the same "pay for what
/// you use" rule the plugin model exists to state.
/// </para>
/// <para>
/// Pure, like <see cref="FlowEmitter"/>: a model in, a string out, no Roslyn, so every
/// emitted shape is testable by comparing text.
/// </para>
/// <para>
/// <strong>What is deliberately not generated.</strong> Service registration. The
/// dispatcher's constructor names every capability it needs, so emitting
/// <c>AddSingleton</c> for each would be mechanical — but a service <em>lifetime</em> is
/// declared nowhere in the flow model, and a generator that picked one would be
/// inventing a fact rather than publishing one. A missing registration is already a
/// start-up failure that names the type; a wrong lifetime is a captive dependency in a
/// file nobody wrote. The endpoint is generated because <c>[HttpTrigger]</c>
/// <em>declares</em> it and hand-writing it is the one place a project can silently
/// disagree with its own flow.
/// </para>
/// </remarks>
public static class EndpointEmitter
{
    /// <summary>The file the emitted endpoints are written to.</summary>
    public const string FileName = "FlowXEndpoints.g.cs";

    private const string RouteBuilder = "global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder";
    private const string ConventionBuilder = "global::Microsoft.AspNetCore.Builder.IEndpointConventionBuilder";
    private const string JsonContext = "global::System.Text.Json.Serialization.JsonSerializerContext";

    /// <summary>Emits the endpoint registrations for every HTTP-triggered flow.</summary>
    /// <param name="assemblyName">The compilation's assembly name, which names the class.</param>
    /// <param name="endpoints">The flows to map, in the order they should be registered.</param>
    public static string Emit(string assemblyName, IReadOnlyList<HttpEndpointModel> endpoints)
    {
        if (endpoints is null)
        {
            throw new System.ArgumentNullException(nameof(endpoints));
        }

        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated");
        writer.OpenBrace();

        EmitClass(writer, ClassNameFor(assemblyName), endpoints);

        writer.CloseBrace();

        return writer.ToString();
    }

    /// <summary>
    /// The class name for one assembly's endpoints, e.g. <c>EcommerceEndpoints</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named from the assembly rather than fixed, because a solution with two flow
    /// libraries would otherwise emit two <c>FlowX.Generated.FlowXEndpoints</c> — and
    /// while the CLR tolerates that, the application referencing both gets an ambiguous
    /// <c>app.MapFlowX()</c> with <em>no way to disambiguate it</em>: qualifying the call
    /// requires naming the class, and both classes have the same name. Distinct names cost
    /// nothing to a single-assembly project — the extension method is still
    /// <c>app.MapFlowX()</c>, resolved by namespace — and turn a dead end into
    /// <c>OrderingEndpoints.MapFlowX(app)</c>.
    /// </para>
    /// <para>
    /// Non-identifier characters are dropped rather than replaced, so <c>Acme.Ordering</c>
    /// becomes <c>AcmeOrderingEndpoints</c>. An assembly name that reduces to nothing —
    /// possible, if unlikely — falls back to <c>FlowXEndpoints</c>.
    /// </para>
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
            return "FlowXEndpoints";
        }

        return identifier.Append("Endpoints").ToString();
    }

    private static void EmitClass(
        SourceWriter writer, string className, IReadOnlyList<HttpEndpointModel> endpoints)
    {
        writer.Line("/// <summary>The endpoints this application's flows declare.</summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// One method per <c>[HttpTrigger]</c>, generated from the same reading of the");
        writer.Line("/// attribute that produced the <c>triggers</c> block of <c>flowx.manifest.json</c>.");
        writer.Line("/// The method, the route and the idempotency rule are therefore the manifest's own");
        writer.Line("/// — there is no second copy of them to drift.");
        writer.Line("/// </remarks>");
        writer.Line("public static class " + className);
        writer.OpenBrace();

        EmitMapAll(writer, endpoints);

        foreach (var endpoint in endpoints)
        {
            writer.Line();
            EmitEndpoint(writer, endpoint);
        }

        writer.CloseBrace();
    }

    /// <summary>
    /// Emits <c>MapFlowX</c>: every HTTP-triggered flow in this application, in one call.
    /// </summary>
    /// <remarks>
    /// The no-argument form appears only when every endpoint resolved a serialiser
    /// context of its own. Emitting it otherwise would mean silently mapping the flows
    /// that could be resolved and skipping the rest — an application that answers on
    /// three of its four declared routes and says nothing. Its absence is a compile
    /// error at the call site instead, which is the loud version of the same fact.
    /// </remarks>
    private static void EmitMapAll(SourceWriter writer, IReadOnlyList<HttpEndpointModel> endpoints)
    {
        if (endpoints.All(e => e.JsonContextTypeName != null))
        {
            writer.Line("/// <summary>Maps every flow in this application that declares an <c>[HttpTrigger]</c>.</summary>");
            writer.Line("/// <param name=\"endpoints\">The route builder.</param>");
            writer.Line("public static " + RouteBuilder + " MapFlowX(");
            writer.Line("    this " + RouteBuilder + " endpoints)");
            writer.OpenBrace();

            foreach (var endpoint in endpoints)
            {
                writer.Line(endpoint.MethodName + "(endpoints);");
            }

            writer.Line();
            writer.Line("return endpoints;");
            writer.CloseBrace();
            writer.Line();
        }

        writer.Line("/// <summary>Maps every flow that declares an <c>[HttpTrigger]</c>, through one serialiser.</summary>");
        writer.Line("/// <param name=\"endpoints\">The route builder.</param>");
        writer.Line("/// <param name=\"json\">The source-generated context carrying every contract on the wire.</param>");
        writer.Line("public static " + RouteBuilder + " MapFlowX(");
        writer.Line("    this " + RouteBuilder + " endpoints,");
        writer.Line("    " + JsonContext + " json)");
        writer.OpenBrace();

        foreach (var endpoint in endpoints)
        {
            writer.Line(endpoint.MethodName + "(endpoints, json);");
        }

        writer.Line();
        writer.Line("return endpoints;");
        writer.CloseBrace();
    }

    private static void EmitEndpoint(SourceWriter writer, HttpEndpointModel endpoint)
    {
        var flow = "global::" + endpoint.FlowTypeName;

        if (endpoint.JsonContextTypeName != null)
        {
            writer.Line(
                "/// <summary><c>" + endpoint.Method + " " + endpoint.Route + "</c> — runs <c>" +
                endpoint.FlowId + "</c>.</summary>");
            writer.Line("/// <param name=\"endpoints\">The route builder.</param>");
            writer.Line("public static " + ConventionBuilder + " " + endpoint.MethodName + "(");
            writer.Line("    this " + RouteBuilder + " endpoints) =>");
            writer.Line(
                "    " + endpoint.MethodName + "(endpoints, global::" + endpoint.JsonContextTypeName +
                ".Default);");
            writer.Line();
        }

        writer.Line(
            "/// <summary><c>" + endpoint.Method + " " + endpoint.Route + "</c> — runs <c>" +
            endpoint.FlowId + "</c>, through the given serialiser.</summary>");
        writer.Line("/// <param name=\"endpoints\">The route builder.</param>");
        writer.Line("/// <param name=\"json\">A context declaring both of this flow's contracts.</param>");
        writer.Line("public static " + ConventionBuilder + " " + endpoint.MethodName + "(");
        writer.Line("    this " + RouteBuilder + " endpoints,");
        writer.Line("    " + JsonContext + " json) =>");
        writer.Line(
            "    global::FlowX.Http.FlowEndpointExtensions.MapFlow<global::" + endpoint.InputTypeName +
            ", global::" + endpoint.OutputTypeName + ">(");
        writer.Line("        endpoints,");
        writer.Line("        " + Quote(endpoint.Method) + ",");
        writer.Line("        " + Quote(endpoint.Route) + ",");
        writer.Line("        " + flow + ".Plan,");
        writer.Line("        static services => global::Microsoft.Extensions.DependencyInjection");
        writer.Line("            .ServiceProviderServiceExtensions");
        writer.Line("            .GetRequiredService<" + flow + ".Dispatcher>(services),");
        writer.Line("        " + flow + ".Projection,");
        writer.Line("        json,");
        writer.Line(
            "        requireIdempotencyKey: " + (endpoint.RequiresIdempotencyKey ? "true" : "false") + ",");
        writer.Line("        sensitiveMembers: " + flow + ".SensitiveMembers);");
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
