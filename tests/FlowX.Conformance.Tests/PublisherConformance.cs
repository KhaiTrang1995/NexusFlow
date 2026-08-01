using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// What an <see cref="IEventPublisher"/> must do. Derive, supply a broker, and the whole suite
/// runs against it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the suite <see href="../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">
/// ADR-0018</see> declined to write.</strong> That record names its own revisit condition — "a
/// broker plugin exists and <c>PublisherConformance</c> can hold two implementations to this
/// contract" — and gives the reason it was deferred in one sentence: "Writing it against one
/// test double would have been a suite that encodes its only implementation." The two
/// implementations now exist, so the suite can be written against the contract rather than
/// against a recording list.
/// </para>
/// <para>
/// <strong>The broker is read per <c>partition_key</c>, and there is deliberately no way to
/// read it globally.</strong> ADR-0018 decision 3 offers ordering per key and states that
/// "global ordering is not offered, and no setting turns it on". A harness with a
/// <c>ReadEverything</c> method would invite an assertion about the order two keys arrived in,
/// which is a guarantee this contract refuses to make — and an implementation could then be
/// failed for a freedom the record explicitly grants it. <see cref="BrokerUnderTest.ReadAsync"/>
/// therefore takes the key it is asking about. The shape of the harness is the guarantee.
/// </para>
/// <para>
/// <strong>What this suite cannot hold an implementation to, and where those guarantees live
/// instead.</strong> ADR-0018 takes five decisions and only the first, third and fourth are
/// about this interface. Decision 2 (<c>staged_seq</c>) is a column in migration <c>0004</c>;
/// decision 5 (retention refuses to purge an instance with an unpublished event) is a
/// <c>NOT EXISTS</c> in <c>PostgresRetention</c> and a count on <c>RetentionSweep</c>. Neither
/// is reachable from an <see cref="IEventPublisher"/> — a publisher is handed a batch and is
/// told nothing about where it came from or what happens to the rows afterwards — and both are
/// asserted where they live, in <c>tests/FlowX.Postgres.Tests</c>. Likewise the claim query's
/// hold-back, which is what makes per-key order survive <em>two</em> publishers: that is the
/// reader's half of decision 3 and it is asserted in <c>OutboxPublisherTests</c>. What is
/// asserted here is the publisher's half — that a batch handed over in staging order is at the
/// broker in staging order, per key.
/// </para>
/// <para>
/// <strong>At-least-once is asserted from the publisher's side as a duty to redeliver.</strong>
/// The same staged event is offered twice, and both times it must be published. A publisher
/// that deduplicated on <see cref="OutboxRecord.EventId"/> would look like an improvement and
/// would break the recovery path ADR-0018 decision 4 depends on: after a crash between the
/// broker acknowledging and the mark committing, the second delivery is the <em>only</em>
/// delivery the consumer can be sure of, because the first one may have been rolled back
/// before anyone recorded it. Deduplication is the consumer's job, on
/// <see cref="OutboxRecord.EventId"/>, and every event contract says so.
/// </para>
/// </remarks>
public abstract class PublisherConformance
{
    /// <summary>A fresh publisher over an empty broker. Called once per test.</summary>
    /// <returns>
    /// The harness. The deriving class owns its lifetime, as it does for every other suite here
    /// — a harness disposed inside the assertion would close the connection an assertion after
    /// it still needs.
    /// </returns>
    protected abstract ValueTask<BrokerUnderTest> CreateBrokerAsync();

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A batch handed over is at the broker in the order it was handed over.</summary>
    /// <remarks>
    /// The base case of the prefix contract: <c>batch[0]</c>, then <c>batch[1]</c>, and a count
    /// that says how many of them got there. Everything else in this suite is a way for that
    /// sentence to be false.
    /// </remarks>
    [Fact]
    public async Task AnAcceptedBatchIsAtTheBrokerInTheOrderItWasGiven()
    {
        var broker = await CreateBrokerAsync();

        var batch = Batch("customer-1", "order.placed", "order.paid", "order.shipped");

        var published = await broker.Publisher.PublishAsync(batch, Cancellation);

        ShouldSucceed(published, "the broker is reachable and the batch is ordinary.");

        published.Value.ShouldBe(
            batch.Count,
            "a broker that took every event reports every event. A count below the batch is " +
            "legitimate and is asserted separately; here there is nothing to decline.");

        (await broker.ReadAsync("customer-1", Cancellation))
            .Select(static e => e.Type)
            .ShouldBe(
                ["order.placed", "order.paid", "order.shipped"],
                "the events are at the broker, and in the order the batch listed them.");
    }

    /// <summary>
    /// Whatever count comes back, the broker holds exactly that prefix of the batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The count is the whole of the ordering protocol. <see cref="IEventPublisher"/> says an
    /// implementation "must never report a count that includes an event it did not send", and
    /// the caller acts on it directly: <c>PostgresOutboxPublisher</c> marks exactly that many
    /// rows published inside the claim transaction and offers the rest again. A count that ran
    /// ahead of what arrived is therefore not a reporting inaccuracy — it is the mechanism by
    /// which an event is lost, silently, with the row marked as delivered.
    /// </para>
    /// <para>
    /// Written to hold for a partial acceptance as well as a full one, because a real client
    /// may accept a prefix and this suite must not require it to accept everything.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheCountIsAPrefixOfWhatReachedTheBroker()
    {
        var broker = await CreateBrokerAsync();

        var batch = Batch("customer-1", "order.placed", "order.paid", "order.shipped");

        var published = await broker.Publisher.PublishAsync(batch, Cancellation);

        ShouldSucceed(published, "the broker is reachable.");

        published.Value.ShouldBeInRange(
            0,
            batch.Count,
            "a count above the batch names events that were never offered, and a negative one " +
            "names nothing at all. The caller indexes into the batch with this number.");

        var delivered = await broker.ReadAsync("customer-1", Cancellation);

        delivered.Count.ShouldBeGreaterThanOrEqualTo(
            published.Value,
            $"the publisher reported {published.Value} event(s) as having reached the broker " +
            $"and the broker holds {delivered.Count}. The caller has already marked the " +
            "difference as published; those events are gone.");

        delivered.Take(published.Value).Select(static e => e.EventId).ShouldBe(
            batch.Take(published.Value).Select(static e => e.EventId),
            "the count is a prefix, not a tally. 'The first n arrived, in this order' is what " +
            "the caller relies on; a publisher that acknowledged events 1 and 3 of a key would " +
            "have broken per-key ordering by the time it said so.");
    }

    /// <summary>Events sharing a partition key arrive in the order they were staged.</summary>
    /// <remarks>
    /// ADR-0018 decision 3, the publisher's half. Asserted across two batches as well as within
    /// one, because the obvious implementation — hand the batch to a client that pipelines it —
    /// gets the second right and the first wrong, and the failure is invisible until a key's
    /// events are split across two passes, which is to say until the outbox is under load.
    /// </remarks>
    [Fact]
    public async Task EventsSharingAPartitionKeyArriveInStagingOrder()
    {
        var broker = await CreateBrokerAsync();

        ShouldSucceed(
            await broker.Publisher.PublishAsync(
                Batch("customer-1", "order.placed", "order.paid"), Cancellation),
            "the first pass.");

        ShouldSucceed(
            await broker.Publisher.PublishAsync(
                Batch("customer-1", "order.shipped", "order.delivered"), Cancellation),
            "the second pass, behind it.");

        (await broker.ReadAsync("customer-1", Cancellation))
            .Select(static e => e.Type)
            .ShouldBe(
                ["order.placed", "order.paid", "order.shipped", "order.delivered"],
                "one key, one order. This is the only ordering ADR-0018 offers, and it holds " +
                "across the batch boundary because a consumer that partitions by key cannot " +
                "see where one pass ended and the next began.");
    }

    /// <summary>
    /// A batch spanning two keys keeps each key's own order and promises nothing between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The assertion this test does not make is the point of it.</strong> The batch
    /// interleaves two keys, and the suite asks the broker about each key separately: <c>A</c>'s
    /// events are in order and <c>B</c>'s events are in order. Whether <c>B1</c> reached the
    /// broker before or after <c>A2</c> is not asserted anywhere in this file, because ADR-0018
    /// decision 3 declines to offer it — "two events with different keys arrive in either
    /// order", and no setting turns that into a guarantee. A publisher that fans keys out across
    /// connections is conformant; one that reorders within a key is not.
    /// </para>
    /// <para>
    /// <c>TheSuiteRejectsAPublisherThatIsWrongTests</c> holds the other end of this: a publisher
    /// that reverses across keys passes this suite, and one that reverses within a key fails it
    /// by name. Two tests are what make "per key, and only per key" an executable statement
    /// rather than a sentence in a comment.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task KeysAreOrderedIndependentlyAndNoOrderIsOfferedBetweenThem()
    {
        var broker = await CreateBrokerAsync();

        IReadOnlyList<OutboxRecord> batch =
        [
            Event("customer-1", "order.placed"),
            Event("customer-2", "order.placed"),
            Event("customer-1", "order.paid"),
            Event("customer-2", "order.paid"),
        ];

        var published = await broker.Publisher.PublishAsync(batch, Cancellation);

        ShouldSucceed(published, "an interleaved batch is an ordinary batch.");
        published.Value.ShouldBe(batch.Count);

        (await broker.ReadAsync("customer-1", Cancellation))
            .Select(static e => e.Type)
            .ShouldBe(["order.placed", "order.paid"], "customer-1's own order holds.");

        (await broker.ReadAsync("customer-2", Cancellation))
            .Select(static e => e.Type)
            .ShouldBe(
                ["order.placed", "order.paid"],
                "and so does customer-2's, independently. Nothing here asserts which of the " +
                "two keys reached the broker first: that is the global order ADR-0018 refuses.");
    }

    /// <summary>An event's identity survives, because that is what a consumer deduplicates on.</summary>
    /// <remarks>
    /// <see cref="OutboxRecord.EventId"/> is stable across every redelivery of the same staged
    /// event, and at-least-once is only survivable because of it. A publisher that minted its
    /// own message id and dropped this one would leave a consumer with no way to tell a
    /// redelivery from a new event, which converts the guarantee ADR-0018 decision 4 chose into
    /// duplicate side effects nobody can suppress.
    /// </remarks>
    [Fact]
    public async Task AnEventKeepsItsIdentityBecauseConsumersDeduplicateOnIt()
    {
        var broker = await CreateBrokerAsync();

        var batch = Batch("customer-1", "order.placed", "order.paid");

        ShouldSucceed(await broker.Publisher.PublishAsync(batch, Cancellation), "an ordinary batch.");

        (await broker.ReadAsync("customer-1", Cancellation))
            .Select(static e => e.EventId)
            .ShouldBe(
                batch.Select(static e => e.EventId),
                "the id the outbox staged is the id the consumer sees. A publisher that " +
                "assigned its own would make idempotency impossible to implement downstream.");
    }

    /// <summary>The body, the type and the schema version reach the broker unchanged.</summary>
    /// <remarks>
    /// ADR-0018 decision 1 chose <see cref="OutboxRecord"/> as the batch type because it
    /// "carries exactly what a broker client needs — <c>EventId</c> for the consumer's
    /// idempotency key, <c>Type</c> for the topic, <c>PartitionKey</c> for the key,
    /// <c>SchemaVersion</c> and <c>PayloadJson</c> for the body". A publisher that dropped one
    /// of those would make that sentence false and would do it silently: the event arrives, and
    /// the consumer cannot tell which version of the contract wrote it.
    /// </remarks>
    [Fact]
    public async Task TheTypeTheSchemaVersionAndTheBodySurvive()
    {
        var broker = await CreateBrokerAsync();

        var staged = Event("customer-1", "order.placed") with
        {
            SchemaVersion = "2.1.0",
            PayloadJson = """{"orderId":"order-7","quantity":3}""",
        };

        ShouldSucceed(await broker.Publisher.PublishAsync([staged], Cancellation), "one event.");

        var delivered = (await broker.ReadAsync("customer-1", Cancellation)).ShouldHaveSingleItem();

        delivered.Type.ShouldBe("order.placed", "the type is the topic the consumer subscribed to.");
        delivered.SchemaVersion.ShouldBe(
            "2.1.0",
            "the version is how a consumer decides whether it can read the body at all " +
            "(constraint C7's two-minor window is a promise about this number).");
        delivered.PartitionKey.ShouldBe("customer-1", "and the key it was ordered by.");
        delivered.PayloadJson.ShouldBe(staged.PayloadJson, "byte for byte: the store already redacted it.");
    }

    /// <summary>An event with no partition key is published rather than refused.</summary>
    /// <remarks>
    /// <c>OutboxWrite.PartitionKey</c> documents null as an unordered event, and ADR-0018
    /// decision 3 exempts it from the per-key guard rather than excluding it from publication.
    /// A publisher that required a key would strand every unkeyed event in the outbox for ever,
    /// and — because a refusal blocks the batch behind it — everything staged after one.
    /// </remarks>
    [Fact]
    public async Task AnUnkeyedEventIsPublishedRatherThanRefused()
    {
        var broker = await CreateBrokerAsync();

        IReadOnlyList<OutboxRecord> batch = [Event(null, "audit.first"), Event(null, "audit.second")];

        var published = await broker.Publisher.PublishAsync(batch, Cancellation);

        ShouldSucceed(published, "a null key asks for no order; it does not ask to be dropped.");
        published.Value.ShouldBe(2);

        (await broker.ReadAsync(null, Cancellation))
            .Select(static e => e.Type)
            .ShouldBe(
                ["audit.first", "audit.second"],
                "both arrived. Any order they happen to be in is an accident of the transport " +
                "rather than a guarantee — nothing may be inferred from it.");
    }

    /// <summary>The same staged event, offered twice, is published twice.</summary>
    /// <remarks>
    /// <para>
    /// The publisher's side of at-least-once. After a crash between the broker acknowledging
    /// and the mark committing, the outbox row is pending again and the same
    /// <see cref="OutboxRecord"/> — same <see cref="OutboxRecord.EventId"/> — is offered on the
    /// next pass. It must go out again.
    /// </para>
    /// <para>
    /// A publisher that remembered ids and suppressed the second delivery would be strictly
    /// worse than one that did not, and would look strictly better: it would suppress exactly
    /// the delivery the consumer needs, because the delivery it kept is the one whose
    /// acknowledgement was rolled back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEventOfferedAgainIsPublishedAgainRatherThanSuppressed()
    {
        var broker = await CreateBrokerAsync();

        var batch = Batch("customer-1", "order.placed");

        ShouldSucceed(await broker.Publisher.PublishAsync(batch, Cancellation), "the first attempt.");
        ShouldSucceed(
            await broker.Publisher.PublishAsync(batch, Cancellation),
            "and the redelivery after a crash rolled the mark back.");

        var delivered = await broker.ReadAsync("customer-1", Cancellation);

        delivered.Count.ShouldBe(
            2,
            "at-least-once means the consumer sees it twice and deduplicates. A publisher that " +
            "swallowed the second delivery would swallow the only one that was ever committed.");

        delivered.Select(static e => e.EventId).Distinct().ShouldHaveSingleItem(
            "and it is the same event both times, which is what makes deduplication possible.");
    }

    /// <summary>An empty batch is accepted as zero and publishes nothing.</summary>
    /// <remarks>
    /// A caller with nothing pending should not have to special-case the call, and a publisher
    /// that treated an empty batch as an error would turn an idle outbox into a reported
    /// failure on every poll.
    /// </remarks>
    [Fact]
    public async Task AnEmptyBatchIsAcceptedAsZero()
    {
        var broker = await CreateBrokerAsync();

        var published = await broker.Publisher.PublishAsync([], Cancellation);

        ShouldSucceed(published, "nothing to do is not a failure.");
        published.Value.ShouldBe(0, "and nothing was done.");
    }

    /// <summary>A broker that cannot be reached is an error value, not an exception.</summary>
    /// <remarks>
    /// <para>
    /// ADR-0007, restated on <see cref="IEventPublisher"/>: "a broker that refuses, is
    /// unreachable, or times out is an <see cref="Error"/> — the outbox is durable, so a
    /// refusal costs a retry and nothing else. An exception means the publisher itself is
    /// defective."
    /// </para>
    /// <para>
    /// The distinction is load-bearing rather than stylistic. <c>PostgresOutboxPublisher</c>
    /// returns an <c>OutboxPass</c> carrying the failure and lets its polling loop try again;
    /// an exception escapes that loop and takes the drain down with it, so an unreachable broker
    /// would stop the outbox rather than delay it. Nothing may be marked either way.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnUnreachableBrokerIsAnErrorValueRatherThanAnException()
    {
        var broker = await CreateBrokerAsync();

        var unreachable = await broker.UnreachableAsync(Cancellation);

        var published = await unreachable.PublishAsync(Batch("customer-1", "order.placed"), Cancellation);

        published.IsFailure.ShouldBeTrue(
            "the broker was not there. Reporting success would have the caller mark an event " +
            "published that nothing received.");

        published.Error.Code.ShouldNotBeNullOrWhiteSpace(
            "a caller branches on the code and an operator greps for it, so it is part of the " +
            "contract rather than of any one client library (ADR-0007).");

        published.Error.Category.IsRetryable().ShouldBeTrue(
            "the category carries the retry decision, and an unreachable broker is the case " +
            "that must read as retryable: the row is still pending and the outbox will offer " +
            $"the batch again. It said {published.Error.Category}, which is terminal.");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Stages a batch of events on one key, in the order given.</summary>
    protected static IReadOnlyList<OutboxRecord> Batch(string? partitionKey, params string[] types) =>
        [.. types.Select(type => Event(partitionKey, type))];

    /// <summary>One staged event, as the journal would have read it back.</summary>
    /// <remarks>
    /// <see cref="OutboxRecord.PublishedAt"/> is null on every one of them, which is what
    /// <see cref="IEventPublisher.PublishAsync"/> promises about the batch it is handed: "every
    /// one is pending".
    /// </remarks>
    protected static OutboxRecord Event(string? partitionKey, string type) => new()
    {
        EventId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Type = type,
        SchemaVersion = "1.0.0",
        PartitionKey = partitionKey,
        PayloadJson = $$"""{"type":"{{type}}"}""",
    };

    /// <summary>Asserts a publish succeeded, printing the publisher's own refusal if not.</summary>
    protected static void ShouldSucceed<T>(Result<T> result, string because) =>
        result.IsSuccess.ShouldBeTrue(
            $"{because} The publisher refused instead: {(result.IsFailure ? result.Error.ToString() : "no error")}");
}

/// <summary>
/// A publisher and the broker behind it, readable.
/// </summary>
/// <remarks>
/// <para>
/// The suite has to see what arrived, and where "what arrived" lives is the one thing that
/// differs completely between a recording double and a client with a socket. This is that
/// difference, and nothing else: an implementer supplies a publisher, a way to read one key's
/// events back in broker order, and a publisher pointed at a broker that is not there.
/// </para>
/// <para>
/// <strong>There is no method that reads the whole broker.</strong> That is a decision, not an
/// omission — see the remarks on <see cref="PublisherConformance"/>. ADR-0018 offers ordering
/// per <c>partition_key</c> and refuses to offer it globally, so the harness offers no way to
/// ask a question whose answer is not part of the contract.
/// </para>
/// </remarks>
public abstract class BrokerUnderTest : IAsyncDisposable
{
    /// <summary>The publisher under test.</summary>
    public abstract IEventPublisher Publisher { get; }

    /// <summary>
    /// Everything the broker holds for one partition key, in the order the broker took it.
    /// </summary>
    /// <param name="partitionKey">
    /// The key to read, or null for the events staged without one.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events, oldest first, including any redelivery.</returns>
    public abstract ValueTask<IReadOnlyList<DeliveredEvent>> ReadAsync(
        string? partitionKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// A publisher of the same kind, pointed at a broker that will not answer.
    /// </summary>
    /// <param name="cancellationToken">Cancels the construction.</param>
    /// <returns>The publisher. Disposed with this harness.</returns>
    /// <remarks>
    /// Supplied by the implementer because "unreachable" is the one failure this suite cannot
    /// provoke portably: it is a closed port for one client, a refused command for another and a
    /// configured refusal for a double. What the suite asserts about it is portable — an
    /// <see cref="Error"/> rather than a throw — which is the half that belongs to the contract.
    /// </remarks>
    public abstract ValueTask<IEventPublisher> UnreachableAsync(CancellationToken cancellationToken);

    /// <summary>Releases whatever the harness opened.</summary>
    /// <returns>A task that completes when it is released.</returns>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>One event as the broker holds it.</summary>
/// <remarks>
/// The five fields ADR-0018 decision 1 names as "what a broker client needs", read back off the
/// broker rather than remembered by the harness. A separate type from <see cref="OutboxRecord"/>
/// on purpose: a harness that returned the records it was handed would assert that the publisher
/// was called, not that anything arrived.
/// </remarks>
/// <param name="EventId">The consumer's idempotency key.</param>
/// <param name="Type">The event type — the topic.</param>
/// <param name="SchemaVersion">The event contract's semantic version.</param>
/// <param name="PartitionKey">The key it was ordered by, or null.</param>
/// <param name="PayloadJson">The body, exactly as the store held it.</param>
public sealed record DeliveredEvent(
    Guid EventId,
    string Type,
    string SchemaVersion,
    string? PartitionKey,
    string? PayloadJson);
