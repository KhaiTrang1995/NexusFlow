namespace FlowX.Conformance.InMemory;

/// <summary>
/// The reference <see cref="IEventPublisher"/>: it records what it was handed and gives it back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It lives here for the same reason <see cref="InMemoryFlowJournal"/> does.</strong> It
/// was a private double inside <c>tests/FlowX.Postgres.Tests</c>, written when the outbox had a
/// publisher seam and nothing behind it, and
/// <see href="../../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</see>
/// records exactly that as the reason <c>PublisherConformance</c> was not written: "the only
/// <see cref="IEventPublisher"/> in the repository is a recording test double … writing it
/// against one test double would have been a suite that encodes its only implementation". Now
/// that a second implementation exists this is one of two things the suite holds, which makes it
/// a reference implementation rather than a fixture — and a reference implementation belongs
/// beside the other three, where an author reads it when a conformance failure message is not
/// enough.
/// </para>
/// <para>
/// <strong>What it can prove, and what it cannot.</strong> Everything on the outbox side of the
/// seam: that an event reaches a publisher in staging order, that a crash before the mark
/// republishes rather than loses, and that two publishers over one table never hand the same
/// event over twice. What it cannot prove is the network — acknowledgement semantics,
/// broker-side partitioning, and what a real client does with a batch it half accepted. That is
/// <c>RedisStreamEventPublisher</c>'s half, and holding both to one suite is the only way either
/// is checked against the contract rather than against itself.
/// </para>
/// <para>
/// It records rather than asserts. A double publish has to be visible as two entries in a
/// list, because "was this delivered twice" is the question the outbox's tests are really
/// asking and a publisher that threw on the second delivery would answer it by changing the
/// outcome.
/// </para>
/// </remarks>
public sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly List<OutboxRecord> _delivered = [];
    private readonly Lock _gate = new();

    /// <summary>Runs before anything is recorded. The seam a test blocks a publisher in.</summary>
    public Func<IReadOnlyList<OutboxRecord>, CancellationToken, ValueTask>? BeforePublish { get; set; }

    /// <summary>
    /// Runs after the batch has been recorded as delivered and before the count is returned.
    /// </summary>
    /// <remarks>
    /// This is the exact instant the crash test needs: the broker has the events and the
    /// outbox row still says pending. A hook that ran before the recording would model a
    /// publisher that died on the way to the broker, which loses nothing and proves nothing.
    /// </remarks>
    public Func<IReadOnlyList<OutboxRecord>, CancellationToken, ValueTask>? AfterPublish { get; set; }

    /// <summary>
    /// How many of the batch to accept. Null accepts all of it; an <see cref="Error"/> in
    /// <see cref="Refusal"/> takes precedence and accepts none.
    /// </summary>
    public int? AcceptCount { get; set; }

    /// <summary>What the broker refuses the batch with, or null when it accepts.</summary>
    public Error? Refusal { get; set; }

    /// <summary>Every event handed to this publisher, in the order it was handed over.</summary>
    /// <remarks>
    /// Includes redeliveries. That is the point: at-least-once is a claim about this list
    /// containing an event twice rather than never.
    /// </remarks>
    public IReadOnlyList<OutboxRecord> Delivered
    {
        get
        {
            lock (_gate)
            {
                return [.. _delivered];
            }
        }
    }

    /// <summary>The types delivered, in delivery order.</summary>
    public IReadOnlyList<string> DeliveredTypes => [.. Delivered.Select(static e => e.Type)];

    /// <inheritdoc />
    public async ValueTask<Result<int>> PublishAsync(
        IReadOnlyList<OutboxRecord> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (BeforePublish is { } hook)
        {
            await hook(batch, cancellationToken).ConfigureAwait(false);
        }

        if (Refusal is { } refusal)
        {
            return Result.Fail<int>(refusal);
        }

        var accepted = Math.Min(AcceptCount ?? batch.Count, batch.Count);

        lock (_gate)
        {
            for (var i = 0; i < accepted; i++)
            {
                _delivered.Add(batch[i]);
            }
        }

        if (AfterPublish is { } after)
        {
            await after(batch, cancellationToken).ConfigureAwait(false);
        }

        return accepted;
    }
}

/// <summary>
/// The exception a test throws to stand for a process dying between the broker
/// acknowledging and <c>published_at</c> being written.
/// </summary>
/// <remarks>
/// A distinct type so the assertion that a crash happened cannot be satisfied by an
/// unrelated failure — a test that catches every exception and calls it the simulated crash
/// passes just as well when the adapter is broken.
/// </remarks>
public sealed class SimulatedCrashException : Exception
{
    /// <summary>Creates the simulated crash.</summary>
    public SimulatedCrashException()
        : base("The publisher process died between publishing and marking published.")
    {
    }
}
