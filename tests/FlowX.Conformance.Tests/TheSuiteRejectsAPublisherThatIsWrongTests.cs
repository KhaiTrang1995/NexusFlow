using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// Proves <see cref="PublisherConformance"/> can fail, and — just as importantly — proves what it
/// declines to fail a publisher for.
/// </summary>
/// <remarks>
/// <para>
/// The same argument as <c>TheSuiteRejectsAStoreThatIsWrongTests</c>, one contract over: a suite
/// that only ever runs against implementations written by the same person from the same reading
/// of the contract is a suite whose green is uninformative. Each publisher below is naive rather
/// than absurd — every defect is one an author reaches by reading
/// <see cref="IEventPublisher"/> and implementing the obvious thing: pipeline the batch for
/// throughput, report the batch size you were given, suppress a repeat you have already seen,
/// throw when the socket does.
/// </para>
/// <para>
/// <strong><see cref="AKeyIndependentPublisherIsAccepted"/> is the one that carries a decision
/// rather than a defect.</strong>
/// <see href="../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</see>
/// decision 3 offers ordering per <c>partition_key</c> and states that global ordering "is not
/// offered, and no setting turns it on". A suite that quietly required cross-key order would
/// reject a conformant publisher for exercising a freedom the record grants it, and nobody would
/// find out until a real client fanned keys across connections. The pair — reordering within a
/// key is rejected by name, reordering across keys is accepted — is what makes "per key, and
/// only per key" executable.
/// </para>
/// </remarks>
public sealed class TheSuiteRejectsAPublisherThatIsWrongTests
{
    /// <summary>A publisher that reorders a key's events is rejected by name.</summary>
    /// <remarks>
    /// The defect a throughput-minded first implementation has: hand the whole batch to a client
    /// that pipelines it and let the broker apply the writes in whatever order they land. Per
    /// key, that is the one thing the contract forbids — and it is invisible in any test with one
    /// event per key, which is to say in every test written before the outbox is under load.
    /// </remarks>
    [Fact]
    public async Task APublisherThatReordersOneKeyFailsAnAcceptedBatchIsAtTheBrokerInTheOrderItWasGiven()
    {
        var suite = new NaivePublisherUnderTest(Defect.ReordersWithinAKey);

        var failure = await CaughtByAsync(
            nameof(suite.AnAcceptedBatchIsAtTheBrokerInTheOrderItWasGiven),
            suite.AnAcceptedBatchIsAtTheBrokerInTheOrderItWasGiven);

        failure.Message.ShouldContain("in the order the batch listed them");
    }

    /// <summary>
    /// A publisher that reorders <em>across</em> keys is accepted, and that is the decision.
    /// </summary>
    /// <remarks>
    /// It publishes the batch back to front, so no two keys arrive in staging order relative to
    /// each other, and each key's own events still do. Every assertion in the suite passes. If
    /// one day it does not, the suite has started requiring a global order and ADR-0018 decision
    /// 3 has been reversed by accident.
    /// </remarks>
    [Fact]
    public async Task AKeyIndependentPublisherIsAccepted()
    {
        var suite = new NaivePublisherUnderTest(Defect.ReordersAcrossKeys);

        await suite.AnAcceptedBatchIsAtTheBrokerInTheOrderItWasGiven();
        await suite.EventsSharingAPartitionKeyArriveInStagingOrder();
        await suite.KeysAreOrderedIndependentlyAndNoOrderIsOfferedBetweenThem();
        await suite.AnEventKeepsItsIdentityBecauseConsumersDeduplicateOnIt();
    }

    /// <summary>A publisher that reports more than it sent is rejected by name.</summary>
    /// <remarks>
    /// The most expensive defect of the set and the least visible. The caller marks that many
    /// rows published inside the claim transaction, so an over-count is not an inaccurate log
    /// line — it is the mechanism by which an event is dropped, with the row recording that it
    /// was delivered.
    /// </remarks>
    [Fact]
    public async Task APublisherThatOverCountsFailsTheCountIsAPrefixOfWhatReachedTheBroker()
    {
        var suite = new NaivePublisherUnderTest(Defect.CountsWhatItDidNotSend);

        var failure = await CaughtByAsync(
            nameof(suite.TheCountIsAPrefixOfWhatReachedTheBroker),
            suite.TheCountIsAPrefixOfWhatReachedTheBroker);

        failure.Message.ShouldContain("those events are gone");
    }

    /// <summary>A publisher that deduplicates redeliveries is rejected by name.</summary>
    /// <remarks>
    /// The defect that looks like a feature. Suppressing a repeat of an event id it has already
    /// sent removes exactly the delivery at-least-once exists to produce: after a crash between
    /// the broker acknowledging and the mark committing, the redelivery is the only delivery
    /// anything committed a record of.
    /// </remarks>
    [Fact]
    public async Task ADeduplicatingPublisherFailsAnEventOfferedAgainIsPublishedAgain()
    {
        var suite = new NaivePublisherUnderTest(Defect.SuppressesARedelivery);

        var failure = await CaughtByAsync(
            nameof(suite.AnEventOfferedAgainIsPublishedAgainRatherThanSuppressed),
            suite.AnEventOfferedAgainIsPublishedAgainRatherThanSuppressed);

        failure.Message.ShouldContain("would swallow the only one that was ever committed");
    }

    /// <summary>A publisher that throws when the broker is down is rejected by name.</summary>
    /// <remarks>
    /// The idiomatic thing to do with a client exception is to let it out, and here that takes
    /// the outbox drain down with it instead of costing one pass. ADR-0007 draws the line where
    /// this suite does: a broker that is unreachable is a value, and only a defective publisher
    /// is an exception.
    /// </remarks>
    [Fact]
    public async Task APublisherThatThrowsOnAnUnreachableBrokerFailsAnUnreachableBrokerIsAnErrorValue()
    {
        var suite = new NaivePublisherUnderTest(Defect.ThrowsWhenUnreachable);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            suite.AnUnreachableBrokerIsAnErrorValueRatherThanAnException);

        thrown.Message.ShouldContain(
            "no broker answered",
            Case.Insensitive,
            "the suite must let a defective publisher's own exception out rather than " +
            "reporting it as a guarantee being enforced. A throw is not an Error.");
    }

    /// <summary>The control: the naive publisher passes what it gets right.</summary>
    /// <remarks>
    /// Without this, the failures above would be satisfied by a publisher that refused every
    /// call, and the suite would be shown to reject rather than to discriminate. The three
    /// assertions listed are every one this defect leaves intact — it reverses a batch, so every
    /// assertion over more than one event on a key is one it correctly fails.
    /// </remarks>
    [Fact]
    public async Task TheNaivePublisherStillPassesTheAssertionsItSatisfies()
    {
        var suite = new NaivePublisherUnderTest(Defect.ReordersWithinAKey);

        await suite.AnEmptyBatchIsAcceptedAsZero();
        await suite.TheTypeTheSchemaVersionAndTheBodySurvive();
        await suite.AnUnreachableBrokerIsAnErrorValueRatherThanAnException();
    }

    /// <summary>
    /// Runs one conformance assertion and returns the failure it produced.
    /// </summary>
    /// <remarks>
    /// Catching exactly <see cref="ShouldAssertException"/> — and nothing else — is what
    /// distinguishes "the suite caught the defect" from "the publisher threw". A publisher that
    /// crashes is a different finding and must not be reported as a guarantee being enforced.
    /// </remarks>
    private static async Task<ShouldAssertException> CaughtByAsync(string assertion, Func<Task> run)
    {
        ShouldAssertException? failure = null;

        try
        {
            await run();
        }
        catch (ShouldAssertException caught)
        {
            failure = caught;
        }

        failure.ShouldNotBeNull(
            $"{assertion} passed against a publisher that violates the guarantee it describes. " +
            "A suite that accepts a publisher it was written to reject is not a gate.");

        return failure;
    }

    /// <summary>The one thing each naive publisher gets wrong.</summary>
    private enum Defect
    {
        /// <summary>Pipelines the batch and lets the broker apply it back to front.</summary>
        ReordersWithinAKey,

        /// <summary>
        /// Publishes back to front but keeps each key's own order — which is conformant.
        /// </summary>
        ReordersAcrossKeys,

        /// <summary>Sends all but the last event and reports the whole batch.</summary>
        CountsWhatItDidNotSend,

        /// <summary>Remembers event ids and swallows a redelivery.</summary>
        SuppressesARedelivery,

        /// <summary>Lets the client's exception out instead of returning an error.</summary>
        ThrowsWhenUnreachable,
    }

    private sealed class NaivePublisherUnderTest : PublisherConformance
    {
        private readonly Defect _defect;

        public NaivePublisherUnderTest(Defect defect) => _defect = defect;

        protected override ValueTask<BrokerUnderTest> CreateBrokerAsync() =>
            new((BrokerUnderTest)new NaiveBrokerUnderTest(_defect));
    }

    /// <summary>
    /// A broker that is a list, and a publisher in front of it with exactly one thing wrong.
    /// </summary>
    /// <remarks>
    /// The broker half is deliberately the same as <c>RecordingBrokerUnderTest</c>'s: what
    /// changes between the harness that passes and the harness that is rejected is the publisher,
    /// so a failure cannot be attributed to a fixture that also differed.
    /// </remarks>
    private sealed class NaiveBrokerUnderTest : BrokerUnderTest
    {
        private readonly Defect _defect;
        private readonly List<OutboxRecord> _delivered = [];

        public NaiveBrokerUnderTest(Defect defect)
        {
            _defect = defect;
            Publisher = new NaivePublisher(defect, _delivered);
        }

        public override IEventPublisher Publisher { get; }

        public override ValueTask<IReadOnlyList<DeliveredEvent>> ReadAsync(
            string? partitionKey,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<DeliveredEvent> delivered =
            [
                .. _delivered
                    .Where(staged => string.Equals(staged.PartitionKey, partitionKey, StringComparison.Ordinal))
                    .Select(static staged => new DeliveredEvent(
                        staged.EventId,
                        staged.Type,
                        staged.SchemaVersion,
                        staged.PartitionKey,
                        staged.PayloadJson)),
            ];

            return new ValueTask<IReadOnlyList<DeliveredEvent>>(delivered);
        }

        public override ValueTask<IEventPublisher> UnreachableAsync(CancellationToken cancellationToken) =>
            new((IEventPublisher)new UnreachablePublisher(_defect));
    }

    private sealed class NaivePublisher : IEventPublisher
    {
        private readonly Defect _defect;
        private readonly List<OutboxRecord> _delivered;
        private readonly HashSet<Guid> _seen = [];

        public NaivePublisher(Defect defect, List<OutboxRecord> delivered)
        {
            _defect = defect;
            _delivered = delivered;
        }

        public ValueTask<Result<int>> PublishAsync(
            IReadOnlyList<OutboxRecord> batch,
            CancellationToken cancellationToken)
        {
            var order = _defect is Defect.ReordersWithinAKey or Defect.ReordersAcrossKeys
                ? batch.Reverse()
                : batch;

            // The "across keys" publisher is conformant: it reverses the batch and then puts
            // each key's own events back in staging order, which is the freedom decision 3
            // grants and the reason this class exists at all.
            if (_defect == Defect.ReordersAcrossKeys)
            {
                order = order
                    .GroupBy(static staged => staged.PartitionKey, StringComparer.Ordinal)
                    .SelectMany(group => group.Reverse());
            }

            var sent = 0;

            foreach (var staged in order)
            {
                if (_defect == Defect.CountsWhatItDidNotSend && sent == batch.Count - 1 && batch.Count > 1)
                {
                    break;
                }

                if (_defect == Defect.SuppressesARedelivery && !_seen.Add(staged.EventId))
                {
                    sent++;
                    continue;
                }

                _delivered.Add(staged);
                sent++;
            }

            return new ValueTask<Result<int>>(batch.Count);
        }
    }

    /// <summary>The publisher a harness hands back for a broker that will not answer.</summary>
    /// <remarks>
    /// It returns an <see cref="Error"/> for every defect but one, so
    /// <see cref="Defect.ThrowsWhenUnreachable"/> is the only thing being asserted when the
    /// unreachable case is exercised.
    /// </remarks>
    private sealed class UnreachablePublisher : IEventPublisher
    {
        private readonly Defect _defect;

        public UnreachablePublisher(Defect defect) => _defect = defect;

        public ValueTask<Result<int>> PublishAsync(
            IReadOnlyList<OutboxRecord> batch,
            CancellationToken cancellationToken)
        {
            if (_defect == Defect.ThrowsWhenUnreachable)
            {
                throw new InvalidOperationException("No broker answered.");
            }

            return new ValueTask<Result<int>>(Result.Fail<int>(new Error(
                "broker.unreachable",
                "No broker answered.",
                ErrorCategory.Unavailable)));
        }
    }
}
