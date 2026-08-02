using System.Collections.Generic;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Turns each flow's <c>[ChangeTrigger]</c> into the registration a composition root would
/// otherwise write by hand — or, before this existed, could not write at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="BusEmitter"/>'s arrangement, and its reasons.</strong> The emitted file is
/// C# in the <em>user's</em> assembly, calling
/// <c>FlowX.Hosting.FlowChangeSubscriptionRegistration.Add</c> by name; this project links against
/// nothing, and the only thing the generator knows about the host is that string, which
/// <see cref="FlowPlanGenerator"/> looks up in the user's compilation before asking for any of
/// this.
/// </para>
/// <para>
/// <strong>The address is copied off the <see cref="TriggerModel"/> the manifest published,</strong>
/// because the source and the group are two of the four values every node derives a change's
/// instance id from
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>)
/// and two of the four its durable cursor is keyed on — so two copies would not be a
/// documentation defect but a change that starts a flow twice.
/// </para>
/// <para>
/// Pure, like <see cref="BusEmitter"/>: a model in, a string out, no Roslyn.
/// </para>
/// </remarks>
public static class ChangeEmitter
{
    /// <summary>The file the emitted registrations are written to.</summary>
    public const string FileName = "FlowXChangeSubscriptions.g.cs";

    private const string Provider = "global::System.IServiceProvider";

    private const string Dispatcher = "global::FlowX.Runtime.IStepDispatcher";

    /// <summary>Emits the registrations for every change-triggered flow.</summary>
    /// <param name="assemblyName">The compilation's assembly name, which names the class.</param>
    /// <param name="subscriptions">The subscriptions to register, in the order they should be added.</param>
    /// <returns>The generated source.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="subscriptions"/> is null.</exception>
    public static string Emit(
        string assemblyName, IReadOnlyList<ChangeSubscriptionModel> subscriptions)
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
    /// The class name for one assembly's change subscriptions, e.g.
    /// <c>EcommerceChangeSubscriptions</c>.
    /// </summary>
    /// <remarks>
    /// Named from the assembly rather than fixed, for <see cref="BusEmitter.ClassNameFor"/>'s
    /// reason: a solution with two flow libraries would otherwise emit two identically named
    /// classes and the application referencing both would get an ambiguous call.
    /// </remarks>
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
            return "FlowXChangeSubscriptions";
        }

        return identifier.Append("ChangeSubscriptions").ToString();
    }

    private static void EmitClass(
        SourceWriter writer, string className, IReadOnlyList<ChangeSubscriptionModel> subscriptions)
    {
        writer.Line("/// <summary>The change subscriptions this application's flows declare.</summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// One method per change trigger, generated from the same reading of the attribute");
        writer.Line("/// that produced the <c>triggers</c> block of <c>flowx.manifest.json</c>. The source");
        writer.Line("/// and the group are therefore the manifest's own — there is no second copy of them");
        writer.Line("/// to drift, and both are terms the instance id and the durable cursor are keyed on.");
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

    /// <summary>
    /// Emits <c>AddFlowXChangeSubscriptions</c>: every change-triggered flow in this application,
    /// in one call.
    /// </summary>
    /// <remarks>
    /// Taken on the built <c>IServiceProvider</c> rather than on <c>IServiceCollection</c>, for
    /// <see cref="BusEmitter"/>'s reason: a registration needs the flow's generated dispatcher,
    /// and that is resolved from the container.
    /// </remarks>
    private static void EmitAddAll(
        SourceWriter writer, IReadOnlyList<ChangeSubscriptionModel> subscriptions)
    {
        writer.Line("/// <summary>Registers every flow in this application that declares a change trigger.</summary>");
        writer.Line("/// <param name=\"services\">The built container.</param>");
        writer.Line("public static " + Provider + " AddFlowXChangeSubscriptions(");
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

    private static void EmitSubscription(SourceWriter writer, ChangeSubscriptionModel subscription)
    {
        var flow = "global::" + subscription.FlowTypeName;

        writer.Line(
            "/// <summary>Observes <c>" + Escape(subscription.Source) + "</c> as <c>" +
            Escape(subscription.Group) + "</c> — runs <c>" + subscription.FlowId + "</c>.</summary>");
        writer.Line("/// <param name=\"services\">The built container.</param>");
        writer.Line("public static " + Provider + " " + subscription.MethodName + "(");
        writer.Line("    this " + Provider + " services) =>");
        writer.Line("    global::FlowX.Hosting.FlowChangeSubscriptionRegistration.Add(");
        writer.Line("        services,");
        writer.Line("        " + flow + ".Plan,");
        writer.Line("        static provider => (" + Dispatcher + ")global::Microsoft.Extensions.DependencyInjection");
        writer.Line("            .ServiceProviderServiceExtensions");
        writer.Line("            .GetRequiredService<" + flow + ".Dispatcher>(provider),");
        writer.Line("        " + Quote(subscription.Source) + ",");
        writer.Line("        " + Quote(subscription.Group) + ");");
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Makes a value safe to sit inside an XML doc comment.</summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
