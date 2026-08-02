using System.Collections.Generic;
using System.Globalization;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Turns each flow's <c>[StreamTrigger]</c> into the registration a composition root would
/// otherwise write by hand — or, before this existed, could not write at all.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChangeEmitter"/>'s arrangement and its reasons. The emitted file is C# in the
/// <em>user's</em> assembly, calling <c>FlowX.Hosting.FlowStreamSubscriptionRegistration.Add</c>
/// by name; this project links against nothing.
/// </para>
/// <para>
/// <strong>It emits the window declaration too, which no other emitter does.</strong> A bus or a
/// change registration carries only an address, because everything else about how the platform
/// runs it is a <c>FlowXOptions</c> value. A window is not: <c>tumbling:1m</c> is a property of
/// the business operation — it is what the flow's output <em>means</em> — and a deployment that
/// could retune it would be changing the aggregate, not the throughput. So it travels with the
/// registration and not with the host's configuration, and the two values that <em>are</em>
/// tuning (<c>FlowXOptions.StreamChannelCapacity</c> and <c>StreamMaxResidentRecords</c>) stay
/// where the other tuning is.
/// </para>
/// </remarks>
public static class StreamEmitter
{
    /// <summary>The file the emitted registrations are written to.</summary>
    public const string FileName = "FlowXStreamSubscriptions.g.cs";

    private const string Provider = "global::System.IServiceProvider";

    private const string Dispatcher = "global::FlowX.Runtime.IStepDispatcher";

    /// <summary>Emits the registrations for every stream-triggered flow.</summary>
    /// <param name="assemblyName">The compilation's assembly name, which names the class.</param>
    /// <param name="subscriptions">The subscriptions to register, in the order they should be added.</param>
    /// <returns>The generated source.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="subscriptions"/> is null.</exception>
    public static string Emit(
        string assemblyName, IReadOnlyList<StreamSubscriptionModel> subscriptions)
    {
        if (subscriptions is null)
        {
            throw new System.ArgumentNullException(nameof(subscriptions));
        }

        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated");
        writer.OpenBrace();

        EmitClass(writer, ClassNameFor(assemblyName), subscriptions);

        writer.CloseBrace();

        return writer.ToString();
    }

    /// <summary>
    /// The class name for one assembly's stream subscriptions, e.g.
    /// <c>TelemetryStreamSubscriptions</c>.
    /// </summary>
    /// <param name="assemblyName">The compilation's assembly name.</param>
    /// <returns>The class name.</returns>
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
            return "FlowXStreamSubscriptions";
        }

        return identifier.Append("StreamSubscriptions").ToString();
    }

    private static void EmitClass(
        SourceWriter writer, string className, IReadOnlyList<StreamSubscriptionModel> subscriptions)
    {
        writer.Line("/// <summary>The stream subscriptions this application's flows declare.</summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// One method per stream trigger, generated from the same reading of the attribute");
        writer.Line("/// that produced the <c>triggers</c> block of <c>flowx.manifest.json</c>. The window,");
        writer.Line("/// the lateness, the checkpoint interval and the parallelism are not in that block —");
        writer.Line("/// they configure how the platform runs the trigger rather than what it promises a");
        writer.Line("/// caller — so they are read off the attribute and emitted here.");
        writer.Line("/// </remarks>");
        writer.Line("public static class " + className);
        writer.OpenBrace();

        EmitAddAll(writer, subscriptions);

        foreach (var subscription in subscriptions)
        {
            writer.Line();
            EmitSubscription(writer, subscription);
        }

        writer.CloseBrace();
    }

    private static void EmitAddAll(
        SourceWriter writer, IReadOnlyList<StreamSubscriptionModel> subscriptions)
    {
        writer.Line("/// <summary>Registers every flow in this application that declares a stream trigger.</summary>");
        writer.Line("/// <param name=\"services\">The built container.</param>");
        writer.Line("public static " + Provider + " AddFlowXStreamSubscriptions(");
        writer.Line("    this " + Provider + " services)");
        writer.OpenBrace();

        foreach (var subscription in subscriptions)
        {
            writer.Line(subscription.MethodName + "(services);");
        }

        writer.Line();
        writer.Line("return services;");
        writer.CloseBrace();
    }

    private static void EmitSubscription(SourceWriter writer, StreamSubscriptionModel subscription)
    {
        var flow = "global::" + subscription.FlowTypeName;

        writer.Line(
            "/// <summary>Windows <c>" + Escape(subscription.Source) + "</c> as <c>" +
            Escape(subscription.Window) + "</c> — runs <c>" + subscription.FlowId + "</c>.</summary>");
        writer.Line("/// <param name=\"services\">The built container.</param>");
        writer.Line("public static " + Provider + " " + subscription.MethodName + "(");
        writer.Line("    this " + Provider + " services) =>");
        writer.Line("    global::FlowX.Hosting.FlowStreamSubscriptionRegistration.Add(");
        writer.Line("        services,");
        writer.Line("        " + flow + ".Plan,");
        writer.Line("        static provider => (" + Dispatcher + ")global::Microsoft.Extensions.DependencyInjection");
        writer.Line("            .ServiceProviderServiceExtensions");
        writer.Line("            .GetRequiredService<" + flow + ".Dispatcher>(provider),");
        writer.Line("        " + Quote(subscription.Source) + ",");
        writer.Line("        " + Quote(subscription.Window) + ",");
        writer.Line("        " + Quote(subscription.Lateness) + ",");
        writer.Line("        " + Quote(subscription.Checkpoint) + ",");
        writer.Line(
            "        " + subscription.Parallelism.ToString(CultureInfo.InvariantCulture) + ");");
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Makes a value safe to sit inside an XML doc comment.</summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
