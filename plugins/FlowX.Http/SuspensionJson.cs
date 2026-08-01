using System.Text.Json;
using FlowX.Runtime;
using Microsoft.AspNetCore.Http;

namespace FlowX.Http;

/// <summary>
/// Writes the body a suspended flow answers with, and the one a delivered signal
/// acknowledges with.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hand-written with <see cref="Utf8JsonWriter"/>, for the reason
/// <see cref="ProblemDetailsJson"/> is.</strong> The shape is small and closed, so writing it
/// directly means this plugin ships no <c>JsonSerializerContext</c> of its own and adds
/// nothing to a consumer's trim graph — which is constraint C2, and which the trim analyzer
/// checks on every build of this project.
/// </para>
/// <para>
/// <strong>Everything in it comes from the plan or from the request, and nothing from the
/// flow's data.</strong> The identities are <c>StepNode.SignalType</c>, read off the compiled
/// plan; the instance id is minted by <c>FlowHost</c>; the delivery path is composed from the
/// request's own path, so it survives a path base, a reverse-proxy prefix and a parameterised
/// route without the endpoint being told about any of them. A caller resolves it against the
/// URL they just posted to, which is why it is a path rather than an absolute URL — a service
/// behind a load balancer cannot know its own scheme and host reliably.
/// </para>
/// </remarks>
internal static class SuspensionJson
{
    /// <summary>The media type these bodies carry.</summary>
    public const string ContentType = "application/json";

    /// <summary>
    /// Serialises one outcome: the instance, what it did, and — when it is waiting — every
    /// signal that would continue it.
    /// </summary>
    /// <param name="instanceId">The journaled instance, or <c>null</c> for an ephemeral flow.</param>
    /// <param name="status"><c>suspended</c> or <c>completed</c>.</param>
    /// <param name="awaiting">The identities the plan declares, empty when the flow has ended.</param>
    /// <param name="basePath">The request's own path, which the delivery paths are built from.</param>
    public static byte[] ToUtf8(
        Guid? instanceId, string status, IReadOnlyList<string> awaiting, string basePath)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (instanceId is { } id)
            {
                writer.WriteString("instanceId", id);
            }

            writer.WriteString("status", status);

            if (awaiting.Count > 0 && instanceId is { } waiting)
            {
                writer.WriteStartArray("awaiting");

                foreach (var signal in awaiting)
                {
                    writer.WriteStartObject();
                    writer.WriteString("signal", signal);
                    writer.WriteString("deliverTo", DeliveryPath(basePath, waiting, signal));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The route the generator registered for one signal on one instance.
    /// </summary>
    /// <remarks>
    /// It matches <c>EndpointEmitter</c>'s template by construction rather than by agreement:
    /// both are <c>{route}/{instanceId}/signals/{identity}</c>, and the run endpoint's own path
    /// is the <c>{route}</c> half. There is no second place the shape is written down.
    /// </remarks>
    public static string DeliveryPath(string basePath, Guid instanceId, string signal) =>
        basePath.TrimEnd('/') + "/" + instanceId.ToString() + "/signals/" + signal;

    /// <summary>Every signal the compiled plan can stop at, in step order.</summary>
    /// <remarks>
    /// Read from the plan rather than passed in, so the endpoint and the manifest publish the
    /// same set from the same declaration. Deduplicated, because a flow that waits twice for
    /// one identity offers a caller one address — the journal's frontier decides which wait a
    /// delivery satisfies, and it is always the first one with no committed row.
    /// </remarks>
    public static IReadOnlyList<string> AwaitedBy(ExecutionPlan plan)
    {
        List<string>? signals = null;

        foreach (var step in plan.Graph.Steps)
        {
            if (step.Kind == StepKind.AwaitSignal && step.SignalType is { Length: > 0 } signal)
            {
                signals ??= [];

                if (!signals.Contains(signal, StringComparer.Ordinal))
                {
                    signals.Add(signal);
                }
            }
        }

        return signals ?? (IReadOnlyList<string>)[];
    }

    /// <summary>The path this request arrived on, prefix included.</summary>
    public static string PathOf(HttpContext context) =>
        context.Request.PathBase.Add(context.Request.Path).ToString();
}
