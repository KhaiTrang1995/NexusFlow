using System.Collections.Generic;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Turns each flow's <c>[CronTrigger]</c> into the schedule registration a composition root
/// would otherwise write by hand — or, before this existed, could not write at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same arrangement <see cref="EndpointEmitter"/> has, and for a sharper
/// reason.</strong> The emitted file is C# in the <em>user's</em> assembly, calling
/// <c>FlowX.Hosting.FlowScheduleRegistration.Add</c> by name; this project links against
/// nothing, and the only thing the generator knows about the host is that string, which
/// <see cref="FlowPlanGenerator"/> looks up in the compilation before asking for any of this.
/// The expression and the zone are copied off the <see cref="TriggerModel"/> the manifest
/// published, so there is no second copy of the schedule to drift — and every node derives the
/// instance id from that expression
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>),
/// so two copies would not be a documentation defect but two schedules that never see each
/// other's firings.
/// </para>
/// <para>
/// <strong>Why this is generated when service registration is not.</strong>
/// <see cref="EndpointEmitter"/> declines to emit <c>AddSingleton</c> because a service
/// lifetime is declared nowhere in the flow model and a generator that picked one would be
/// inventing a fact. A schedule is the opposite case: <c>[CronTrigger]</c> declares the whole
/// of it — the expression, the zone and the missed-fire behaviour — and hand-writing the
/// registration is the one place a project can silently disagree with its own flow.
/// </para>
/// <para>
/// Pure, like <see cref="FlowEmitter"/>: a model in, a string out, no Roslyn.
/// </para>
/// </remarks>
public static class ScheduleEmitter
{
    /// <summary>The file the emitted registrations are written to.</summary>
    public const string FileName = "FlowXSchedules.g.cs";

    private const string Provider = "global::System.IServiceProvider";

    private const string Dispatcher = "global::FlowX.Runtime.IStepDispatcher";

    /// <summary>Emits the schedule registrations for every cron-triggered flow.</summary>
    /// <param name="assemblyName">The compilation's assembly name, which names the class.</param>
    /// <param name="schedules">The schedules to register, in the order they should be added.</param>
    public static string Emit(string assemblyName, IReadOnlyList<ScheduleModel> schedules)
    {
        if (schedules is null)
        {
            throw new System.ArgumentNullException(nameof(schedules));
        }

        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated");
        writer.OpenBrace();

        EmitClass(writer, ClassNameFor(assemblyName), schedules);

        writer.CloseBrace();

        return writer.ToString();
    }

    /// <summary>
    /// The class name for one assembly's schedules, e.g. <c>WorkflowSchedules</c>.
    /// </summary>
    /// <remarks>
    /// Named from the assembly rather than fixed, for
    /// <see cref="EndpointEmitter.ClassNameFor"/>'s reason: a solution with two flow libraries
    /// would otherwise emit two <c>FlowX.Generated.FlowXSchedules</c>, and the application
    /// referencing both would get an ambiguous <c>services.AddFlowXSchedules()</c> with no way to
    /// disambiguate it.
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
            return "FlowXSchedules";
        }

        return identifier.Append("Schedules").ToString();
    }

    private static void EmitClass(
        SourceWriter writer, string className, IReadOnlyList<ScheduleModel> schedules)
    {
        writer.Line("/// <summary>The schedules this application's flows declare.</summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// One method per <c>[CronTrigger]</c>, generated from the same reading of the");
        writer.Line("/// attribute that produced the <c>triggers</c> block of <c>flowx.manifest.json</c>.");
        writer.Line("/// The expression and the time zone are therefore the manifest's own — there is no");
        writer.Line("/// second copy of them to drift, and every node in the fleet derives the instance id");
        writer.Line("/// each firing runs under from exactly that expression.");
        writer.Line("/// </remarks>");
        writer.Line("public static class " + className);
        writer.OpenBrace();

        EmitAddAll(writer, schedules);

        foreach (var schedule in schedules)
        {
            writer.Line();
            EmitSchedule(writer, schedule);
        }

        writer.CloseBrace();
    }

    /// <summary>
    /// Emits <c>AddFlowXSchedules</c>: every cron-triggered flow in this application, in one
    /// call.
    /// </summary>
    /// <remarks>
    /// Taken on the built <c>IServiceProvider</c> rather than on <c>IServiceCollection</c>,
    /// because a registration needs the flow's generated dispatcher and that is resolved from
    /// the container. It is the same shape and the same point in <c>Program.cs</c> as the
    /// <c>FlowCatalog.Add</c> a durable application already writes.
    /// </remarks>
    private static void EmitAddAll(SourceWriter writer, IReadOnlyList<ScheduleModel> schedules)
    {
        writer.Line("/// <summary>Registers every flow in this application that declares a <c>[CronTrigger]</c>.</summary>");
        writer.Line("/// <param name=\"services\">The built container.</param>");
        writer.Line("public static " + Provider + " AddFlowXSchedules(");
        writer.Line("    this " + Provider + " services)");
        writer.OpenBrace();

        foreach (var schedule in schedules)
        {
            writer.Line(schedule.MethodName + "(services);");
        }

        writer.Line();
        writer.Line("return services;");
        writer.CloseBrace();
    }

    private static void EmitSchedule(SourceWriter writer, ScheduleModel schedule)
    {
        var flow = "global::" + schedule.FlowTypeName;

        writer.Line(
            "/// <summary><c>" + Escape(schedule.Cron) + "</c> (" + Escape(schedule.TimeZone) +
            ") — runs <c>" + schedule.FlowId + "</c>.</summary>");
        writer.Line("/// <param name=\"services\">The built container.</param>");
        writer.Line("public static " + Provider + " " + schedule.MethodName + "(");
        writer.Line("    this " + Provider + " services) =>");
        writer.Line("    global::FlowX.Hosting.FlowScheduleRegistration.Add(");
        writer.Line("        services,");
        writer.Line("        " + flow + ".Plan,");
        writer.Line("        static provider => (" + Dispatcher + ")global::Microsoft.Extensions.DependencyInjection");
        writer.Line("            .ServiceProviderServiceExtensions");
        writer.Line("            .GetRequiredService<" + flow + ".Dispatcher>(provider),");
        writer.Line("        " + Quote(schedule.Cron) + ",");
        writer.Line("        " + Quote(schedule.TimeZone) + ",");
        writer.Line("        global::FlowX.MissedFirePolicy." + schedule.MissedFire + ",");
        writer.Line("        " + (schedule.PerTenant ? "true" : "false") + ");");
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Makes a value safe to sit inside an XML doc comment.</summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
