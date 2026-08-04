namespace FlowX.Compiler.Emit;

/// <summary>Which generated registrations this application actually has.</summary>
/// <param name="Endpoints">HTTP routes were emitted.</param>
/// <param name="Bus">Bus subscriptions were emitted.</param>
/// <param name="Change">Change subscriptions were emitted.</param>
/// <param name="Schedules">Schedules were emitted.</param>
/// <param name="Streams">Stream subscriptions were emitted.</param>
public sealed record HostWiringModel(
    bool Endpoints,
    bool Bus,
    bool Change,
    bool Schedules,
    bool Streams)
{
    /// <summary>Whether there is anything at all to wire.</summary>
    public bool Any => Endpoints || Bus || Change || Schedules || Streams;

    /// <summary>Whether anything has to be registered on the built provider.</summary>
    public bool AnyOnProvider => Bus || Change || Schedules || Streams;
}

/// <summary>
/// Emits the one call that turns every declaration this application carries into something that
/// runs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Six calls were six chances to forget one, and forgetting one is silent.</strong> The
/// registrations are separate methods because they are emitted separately and a host may
/// legitimately want a subset — a worker that serves the subscriptions and no routes is a real
/// deployment. But the common case is "everything this assembly declared", and writing that out
/// by hand is how <c>samples/crm</c> ended up with three subscriptions nobody registered and,
/// later, two schedules nobody fired.
/// </para>
/// <para>
/// <strong>Two overloads, because the two hosts are different types.</strong> A web application
/// is an <c>IEndpointRouteBuilder</c> and reaches its provider through
/// <c>ServiceProvider</c>, so one call does everything. A worker has no route builder at all and
/// calls the provider overload. Neither mentions the other's framework.
/// </para>
/// <para>
/// Pure, like <see cref="FlowEmitter"/>: a model in, a string out, no Roslyn.
/// </para>
/// </remarks>
public static class HostWiringEmitter
{
    /// <summary>The file the emitted wiring is written to.</summary>
    public const string FileName = "FlowXHost.g.cs";

    private const string Provider = "global::System.IServiceProvider";

    private const string Endpoints = "global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder";

    /// <summary>Emits the aggregate wiring for this compilation.</summary>
    /// <param name="assemblyName">The compilation's assembly name, which names the classes.</param>
    /// <param name="model">What was emitted, and therefore what may be called.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="model"/> is null.</exception>
    public static string Emit(string assemblyName, HostWiringModel model)
    {
        if (model is null)
        {
            throw new System.ArgumentNullException(nameof(model));
        }

        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated");
        writer.OpenBrace();

        EmitClass(writer, assemblyName, model);

        writer.CloseBrace();

        return writer.ToString();
    }

    /// <summary>The class name for one assembly's wiring, e.g. <c>CrmHost</c>.</summary>
    /// <remarks>Named from the assembly for <see cref="BusEmitter.ClassNameFor"/>'s reason.</remarks>
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
            return "FlowXHost";
        }

        return identifier.Append("Host").ToString();
    }

    private static void EmitClass(SourceWriter writer, string assemblyName, HostWiringModel model)
    {
        writer.Line("/// <summary>Everything this application declared, started in one call.</summary>");
        writer.Line("public static class " + ClassNameFor(assemblyName));
        writer.OpenBrace();

        if (model.AnyOnProvider)
        {
            EmitProviderOverload(writer, assemblyName, model);
        }

        if (model.Endpoints)
        {
            if (model.AnyOnProvider)
            {
                writer.Line();
            }

            EmitEndpointOverload(writer, assemblyName, model);
        }

        writer.CloseBrace();
    }

    private static void EmitProviderOverload(
        SourceWriter writer, string assemblyName, HostWiringModel model)
    {
        writer.Line("/// <summary>Registers every subscription, schedule and stream this assembly declares.</summary>");
        writer.Line("/// <param name=\"services\">The built provider — a subscription needs a resolved dispatcher.</param>");
        writer.Line("/// <returns>The same provider, so calls chain.</returns>");
        writer.Line("public static " + Provider + " UseFlowX(");
        writer.Line("    this " + Provider + " services)");
        writer.OpenBrace();

        if (model.Bus)
        {
            writer.Line(BusEmitter.ClassNameFor(assemblyName) + ".AddFlowXSubscriptions(services);");
        }

        if (model.Change)
        {
            writer.Line(
                ChangeEmitter.ClassNameFor(assemblyName) + ".AddFlowXChangeSubscriptions(services);");
        }

        if (model.Schedules)
        {
            writer.Line(ScheduleEmitter.ClassNameFor(assemblyName) + ".AddFlowXSchedules(services);");
        }

        if (model.Streams)
        {
            writer.Line(
                StreamEmitter.ClassNameFor(assemblyName) + ".AddFlowXStreamSubscriptions(services);");
        }

        writer.Line();
        writer.Line("return services;");
        writer.CloseBrace();
    }

    private static void EmitEndpointOverload(
        SourceWriter writer, string assemblyName, HostWiringModel model)
    {
        writer.Line("/// <summary>Maps every route this assembly declares, and registers everything else.</summary>");
        writer.Line("/// <param name=\"endpoints\">The web application.</param>");
        writer.Line("/// <returns>The same builder, so calls chain.</returns>");
        writer.Line("/// <remarks>");
        writer.Line("/// The agent surface is not here: <c>MapFlowXMcp</c> takes the path it is served on,");
        writer.Line("/// which is a deployment's choice rather than a declaration's.");
        writer.Line("/// </remarks>");
        writer.Line("public static " + Endpoints + " UseFlowX(");
        writer.Line("    this " + Endpoints + " endpoints)");
        writer.OpenBrace();

        writer.Line(EndpointEmitter.ClassNameFor(assemblyName) + ".MapFlowX(endpoints);");

        if (model.AnyOnProvider)
        {
            writer.Line();
            writer.Line("UseFlowX(endpoints.ServiceProvider);");
        }

        writer.Line();
        writer.Line("return endpoints;");
        writer.CloseBrace();
    }
}
