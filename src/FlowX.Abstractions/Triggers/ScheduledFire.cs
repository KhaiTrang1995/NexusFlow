namespace FlowX;

/// <summary>
/// What a schedule hands the flow it starts: the occurrence, and the declaration that
/// produced it.
/// </summary>
/// <param name="OccurrenceAt">
/// The instant the cron expression named — <strong>not</strong> the instant the sweep noticed
/// it. A firing that happens forty minutes late still carries the instant it was due, because
/// that is the value the work is about: a nightly reconciliation run at 02:41 is still
/// reconciling the day that ended at 02:00.
/// </param>
/// <param name="Cron">The five-field expression, as the author wrote it and the manifest published it.</param>
/// <param name="TimeZone">The IANA zone the expression was evaluated in.</param>
/// <remarks>
/// <para>
/// <strong>This type exists because a flow may not read a clock.</strong> <c>FLOWX1007</c>
/// makes an ambient <c>DateTime.UtcNow</c> an error inside a <c>Durable</c> flow and
/// <c>FLOWX1011</c> makes it one inside every builder lambda, for the reason
/// <c>06 §5</c> gives: a value taken ambiently is in none of the fields a replay
/// reconstructs, so a resumed instance would compute a different answer from the one it
/// committed. A scheduled flow therefore cannot ask what time it is — and "what time is it"
/// is the only thing a cron fire has to say. So the occurrence arrives as input, is journalled
/// on <c>flow_instance.input</c> like any other trigger's body, and is read back verbatim on a
/// resume (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0033-a-scheduled-flows-input-is-its-occurrence.md">ADR-0028</a>).
/// </para>
/// <para>
/// <strong>It is also the instance's identity, one derivation away.</strong> The id every node
/// derives for a firing is derived from these three values and the flow's own id and version
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>),
/// which is why the expression and the zone are carried rather than only the instant: an
/// operator reading a journal row can reconstruct why that row has that primary key.
/// </para>
/// <para>
/// <strong>A flow declaring <c>[CronTrigger]</c> must take this as its input</strong>, and
/// <c>FLOWX1038</c> reports one that does not. The alternative — letting a scheduled flow
/// declare any input and starting it with a default — would journal an instance whose recorded
/// request is a value nobody sent.
/// </para>
/// </remarks>
public sealed record ScheduledFire(DateTimeOffset OccurrenceAt, string Cron, string TimeZone);
