using FlowX;

namespace Crm;

/// <summary>
/// The publisher a deployment with no broker gets.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all.</strong> This sample says a broker is optional and states what
/// leaving it out costs. It was not: <c>AddFlowXPostgresOutbox</c> resolves an
/// <see cref="IEventPublisher"/> when the host starts, so a deployment without one crashed on
/// <c>Host.StartAsync</c> with a service-not-registered exception — the README's claim and
/// <c>CrmOutboxPump</c>'s own remarks were both wrong, and nothing tested the composition either
/// way. This is the missing half.
/// </para>
/// <para>
/// <strong>It acknowledges nothing, and that is the whole design.</strong> Reporting a prefix of
/// zero leaves every claimed row pending, so the sweep marks nothing and the transaction rolls
/// back. The rows stay in the outbox exactly as staged — which is what lets the change feed keep
/// offering them to <c>crm.process.transition</c>, and what makes the events arrive for real the
/// day somebody configures a broker and restarts. A publisher that lied and said "sent" would
/// throw away every event ever staged without a broker, silently and permanently.
/// </para>
/// <para>
/// <strong>Not a failure, either.</strong> Returning an <see cref="Error"/> would be reported as a
/// broker that refused, and a deployment that deliberately has no broker is not a deployment with
/// a broken one. Nought published and no error is the honest reading: there is nowhere to send it.
/// </para>
/// <para>
/// <strong>And the loop does not spin.</strong> A prefix of zero leaves the sweep unsaturated, so
/// <c>PostgresOutboxPublisher.RunAsync</c> waits its poll interval rather than coming straight
/// back — the pump costs one small query per interval, not a hot loop against PostgreSQL.
/// </para>
/// </remarks>
public sealed class UnpublishedOutbox : IEventPublisher
{
    /// <inheritdoc />
    public ValueTask<Result<int>> PublishAsync(
        IReadOnlyList<OutboxRecord> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Result.Ok(0));
    }
}
