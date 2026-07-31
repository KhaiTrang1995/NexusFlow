using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// What an <see cref="ILeaseStore"/> must do. Derive, supply a store, and the whole suite
/// runs against it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0006's claim is that exactly-one-writer is a property of two primitives rather than of
/// any particular database. One implementation cannot demonstrate that, and the
/// fencing-token argument is the part most likely to be got wrong differently by a different
/// store — which is why this file exists before Redis and Postgres do, rather than after.
/// A suite written once two stores exist is a suite shaped like those two stores.
/// </para>
/// <para>
/// <strong>Expiry is tested in real time, deliberately.</strong> The suite waits out a short
/// TTL rather than advancing an injected clock. A fake clock would make these assertions
/// cheaper and would stop them being about expiry: a store whose TTL is enforced by Redis or
/// by <c>now()</c> in Postgres has no clock to inject, and testing the one store that does
/// would prove nothing about the two that matter. A store with coarser granularity overrides
/// <see cref="LeaseTtl"/>.
/// </para>
/// </remarks>
public abstract class LeaseStoreConformance
{
    /// <summary>A fresh, empty lease store. Called once per test.</summary>
    protected abstract ValueTask<ILeaseStore> CreateStoreAsync();

    /// <summary>The TTL the expiry assertions use. Short, so the suite stays quick.</summary>
    protected virtual TimeSpan LeaseTtl => TimeSpan.FromMilliseconds(200);

    /// <summary>How long a lease is held in the assertions that are not about expiry.</summary>
    protected virtual TimeSpan LongTtl => TimeSpan.FromMinutes(5);

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Acquiring an unheld instance grants a lease with a real token and an expiry.</summary>
    [Fact]
    public async Task AcquiringAnUnheldInstanceGrantsIt()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var acquired = await store.AcquireAsync(instance, "node-1", LongTtl, Cancellation);

        ShouldSucceed(acquired, "nobody held it.");

        acquired.Value.InstanceId.ShouldBe(instance);
        acquired.Value.OwnerNode.ShouldBe("node-1", "the owner is recorded, so an operator can see who has it.");
        acquired.Value.Token.IsNone.ShouldBeFalse(
            "an issued token is never the none token; the journal's fence starts at zero and " +
            "a token that never beats it fences nobody out.");
        acquired.Value.ExpiresAt.ShouldBeGreaterThan(
            DateTimeOffset.UtcNow,
            "a lease that is already expired grants nothing.");
    }

    /// <summary>A live lease is exclusive, including against the node that holds it.</summary>
    /// <remarks>
    /// Refusing the current holder is a decision this suite makes rather than an accident. A
    /// holder that could re-acquire would be issued a second token for work it is already
    /// doing, and its own in-flight writes would then be fenced out by itself. A holder
    /// renews.
    /// </remarks>
    [Fact]
    public async Task ALiveLeaseCannotBeAcquiredAgain()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        ShouldSucceed(
            await store.AcquireAsync(instance, "node-1", LongTtl, Cancellation),
            "node-1 takes it first.");

        ShouldFailWith(
            await store.AcquireAsync(instance, "node-2", LongTtl, Cancellation),
            DurabilityErrors.LeaseHeldCode,
            ErrorCategory.Conflict,
            "exactly one writer is the whole point.");

        ShouldFailWith(
            await store.AcquireAsync(instance, "node-1", LongTtl, Cancellation),
            DurabilityErrors.LeaseHeldCode,
            ErrorCategory.Conflict,
            "the holder renews rather than acquiring again; a second token would fence out " +
            "its own in-flight writes.");
    }

    /// <summary>Exclusivity is per instance, not global.</summary>
    /// <remarks>
    /// The obvious over-correction, and the one that would quietly serialise a whole
    /// deployment onto one instance at a time.
    /// </remarks>
    [Fact]
    public async Task LeasesOnDifferentInstancesDoNotExclude()
    {
        var store = await CreateStoreAsync();

        ShouldSucceed(
            await store.AcquireAsync(Guid.NewGuid(), "node-1", LongTtl, Cancellation),
            "one instance is taken.");

        ShouldSucceed(
            await store.AcquireAsync(Guid.NewGuid(), "node-1", LongTtl, Cancellation),
            "a different instance is a different lease. Nodes are interchangeable and run many.");
    }

    /// <summary>Every acquisition issues a strictly greater token than the last.</summary>
    /// <remarks>
    /// <para>
    /// This is the assertion the whole of ADR-0006 depends on. The counter is per instance and
    /// must never restart — not on release, not on expiry, not when the holder went away
    /// cleanly. A store that reset it would hand a returning zombie a token equal to the one
    /// its successor is using, and every fence check downstream would pass.
    /// </para>
    /// <para>
    /// Checked across both ways a lease ends, because a plausible implementation gets one
    /// right and the other wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryAcquisitionIssuesAStrictlyGreaterToken()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var first = await store.AcquireAsync(instance, "node-1", LeaseTtl, Cancellation);
        ShouldSucceed(first, "the first acquisition.");

        await WaitForExpiryAsync(first.Value);

        var second = await AcquireAfterExpiryAsync(store, instance, "node-2");

        second.Token.ShouldBeGreaterThan(
            first.Value.Token,
            "the token after an expiry must beat the one that expired, or the zombie holding " +
            "the old one is not fenced out by anything.");

        ShouldSucceed(await store.ReleaseAsync(second, Cancellation), "node-2 shuts down cleanly.");

        var third = await store.AcquireAsync(instance, "node-3", LongTtl, Cancellation);
        ShouldSucceed(third, "the lease was released, so it is available.");

        third.Value.Token.ShouldBeGreaterThan(
            second.Token,
            "a clean release does not reset the counter either. The sequence is per instance " +
            "and never restarts.");
    }

    /// <summary>An expired lease is taken by another node.</summary>
    /// <remarks>
    /// Recovery latency is bounded by the TTL, which is a tuning trade-off. Correctness is
    /// bounded by the token, which is not.
    /// </remarks>
    [Fact]
    public async Task AnExpiredLeaseIsAcquirableByAnotherNode()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var held = await store.AcquireAsync(instance, "node-1", LeaseTtl, Cancellation);
        ShouldSucceed(held, "node-1 takes it and then dies.");

        await WaitForExpiryAsync(held.Value);

        var recovered = await AcquireAfterExpiryAsync(store, instance, "node-2");

        recovered.OwnerNode.ShouldBe("node-2", "the instance is picked up rather than stranded.");
    }

    /// <summary>Expiry is observable through a read, not only inferred from an acquisition.</summary>
    /// <remarks>
    /// A recovery scan asks the store what is expired. A store that reported a lapsed lease as
    /// live would make that scan blind and would leave instances stuck until something else
    /// noticed.
    /// </remarks>
    [Fact]
    public async Task AnExpiredLeaseReadsAsUnheld()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var held = await store.AcquireAsync(instance, "node-1", LeaseTtl, Cancellation);
        ShouldSucceed(held, "node-1 takes it.");

        ShouldSucceed(await store.ReadAsync(instance, Cancellation), "and while it is live, it reads as held.");

        await WaitForExpiryAsync(held.Value);

        ShouldFailWith(
            await store.ReadAsync(instance, Cancellation),
            DurabilityErrors.LeaseNotHeldCode,
            ErrorCategory.NotFound,
            "an expired lease is not a lease.");
    }

    /// <summary>A read reports who holds the lease and with which token.</summary>
    [Fact]
    public async Task ReadReportsTheCurrentOwnerAndToken()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var held = await store.AcquireAsync(instance, "node-1", LongTtl, Cancellation);
        ShouldSucceed(held, "node-1 takes it.");

        var read = await store.ReadAsync(instance, Cancellation);
        ShouldSucceed(read, "and the store can say so.");

        read.Value.OwnerNode.ShouldBe("node-1");
        read.Value.Token.ShouldBe(
            held.Value.Token,
            "the token a reader sees is the one the holder was issued — a recovering node " +
            "needs it to know what it has to beat.");
    }

    /// <summary>Renewal extends the expiry and keeps the token.</summary>
    /// <remarks>
    /// Keeping the token is the point. A renewal that issued a new one would fence out the
    /// holder's own in-flight writes, which is the failure mode that looks like data loss and
    /// reads like a race.
    /// </remarks>
    [Fact]
    public async Task RenewalExtendsTheExpiryAndKeepsTheToken()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var held = await store.AcquireAsync(instance, "node-1", LeaseTtl, Cancellation);
        ShouldSucceed(held, "node-1 takes it.");

        var renewed = await store.RenewAsync(held.Value, LongTtl, Cancellation);
        ShouldSucceed(renewed, "the holder renews while it is still live.");

        renewed.Value.Token.ShouldBe(held.Value.Token, "a renewal is the same ownership, extended.");
        renewed.Value.ExpiresAt.ShouldBeGreaterThan(held.Value.ExpiresAt, "and it is extended.");
    }

    /// <summary>An expired lease cannot be renewed back to life.</summary>
    /// <remarks>
    /// The classic pause: a node stops for longer than its TTL, wakes up, and renews as though
    /// nothing happened. If that succeeded, two nodes would believe they own the instance and
    /// the earlier one would still hold a token the journal accepts.
    /// </remarks>
    [Fact]
    public async Task AnExpiredLeaseCannotBeRenewed()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var held = await store.AcquireAsync(instance, "node-1", LeaseTtl, Cancellation);
        ShouldSucceed(held, "node-1 takes it and then pauses.");

        await WaitForExpiryAsync(held.Value);

        ShouldFailWith(
            await store.RenewAsync(held.Value, LongTtl, Cancellation),
            DurabilityErrors.LeaseLostCode,
            ErrorCategory.Forbidden,
            "the lease lapsed while the node was paused; it does not come back.");
    }

    /// <summary>A superseded token cannot renew, even when its holder believes it is live.</summary>
    /// <remarks>
    /// The zombie's own view of its lease is the thing that cannot be trusted: it holds a
    /// record with a future expiry that stopped being true while it was not looking. The store
    /// answers from the token, not from the caller's copy of the expiry.
    /// </remarks>
    [Fact]
    public async Task ASupersededTokenCannotRenew()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var first = await store.AcquireAsync(instance, "node-1", LeaseTtl, Cancellation);
        ShouldSucceed(first, "node-1 takes it.");

        await WaitForExpiryAsync(first.Value);
        await AcquireAfterExpiryAsync(store, instance, "node-2");

        var zombiesView = first.Value with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };

        ShouldFailWith(
            await store.RenewAsync(zombiesView, LongTtl, Cancellation),
            DurabilityErrors.LeaseLostCode,
            ErrorCategory.Forbidden,
            "node-1 still thinks its lease is live. The token says otherwise, and the token wins.");
    }

    /// <summary>Releasing makes the lease available immediately, without waiting for the TTL.</summary>
    /// <remarks>
    /// This is what makes a rolling update a non-event: a pod releases its leases on SIGTERM
    /// and the new pod picks them up at once, instead of every instance pausing for a TTL
    /// (<c>docs/11-Distributed-Runtime.md §7</c>).
    /// </remarks>
    [Fact]
    public async Task ReleasingMakesTheLeaseImmediatelyAvailable()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var held = await store.AcquireAsync(instance, "node-1", LongTtl, Cancellation);
        ShouldSucceed(held, "node-1 takes it with a long TTL.");

        var released = await store.ReleaseAsync(held.Value, Cancellation);
        ShouldSucceed(released, "node-1 is shutting down and gives it up.");
        released.Value.ShouldBeTrue("the call reports that it did the releasing.");

        ShouldSucceed(
            await store.AcquireAsync(instance, "node-2", LongTtl, Cancellation),
            "the new pod does not wait out a TTL for a lease nobody holds.");
    }

    /// <summary>A superseded token cannot release its successor's lease.</summary>
    /// <remarks>
    /// The quietest way to lose exclusivity. A zombie shutting down tidily releases what it
    /// thinks is its lease, the live owner is silently evicted, and a third node acquires
    /// while the second is mid-step.
    /// </remarks>
    [Fact]
    public async Task ASupersededTokenCannotRelease()
    {
        var store = await CreateStoreAsync();
        var instance = Guid.NewGuid();

        var first = await store.AcquireAsync(instance, "node-1", LeaseTtl, Cancellation);
        ShouldSucceed(first, "node-1 takes it.");

        await WaitForExpiryAsync(first.Value);
        var second = await AcquireAfterExpiryAsync(store, instance, "node-2");

        ShouldFailWith(
            await store.ReleaseAsync(first.Value, Cancellation),
            DurabilityErrors.LeaseLostCode,
            ErrorCategory.Forbidden,
            "node-1 no longer owns anything to release.");

        var read = await store.ReadAsync(instance, Cancellation);
        ShouldSucceed(read, "node-2 still holds it.");
        read.Value.Token.ShouldBe(second.Token, "and with the token it was issued.");
    }

    /// <summary>Reading a lease nobody holds is a value, not an exception.</summary>
    [Fact]
    public async Task ReadingAnUnheldLeaseIsAnErrorValue()
    {
        var store = await CreateStoreAsync();

        ShouldFailWith(
            await store.ReadAsync(Guid.NewGuid(), Cancellation),
            DurabilityErrors.LeaseNotHeldCode,
            ErrorCategory.NotFound,
            "a recovery scan asks about instances nobody holds; that is its job, not an error " +
            "condition (ADR-0007).");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Waits until a lease has certainly lapsed.</summary>
    /// <remarks>
    /// Anchored on the lease's own <see cref="FlowLease.ExpiresAt"/> rather than on the TTL,
    /// so a store whose clock is the database's is waited out correctly. The margin absorbs
    /// timer granularity; the retry in <see cref="AcquireAfterExpiryAsync"/> absorbs skew.
    /// </remarks>
    private static async Task WaitForExpiryAsync(FlowLease lease)
    {
        var deadline = lease.ExpiresAt + TimeSpan.FromMilliseconds(50);
        var remaining = deadline - DateTimeOffset.UtcNow;

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, Cancellation);
        }
    }

    /// <summary>
    /// Acquires a lease that has expired, tolerating clock skew between this process and the
    /// store but never tolerating a lease that does not expire at all.
    /// </summary>
    private async Task<FlowLease> AcquireAfterExpiryAsync(ILeaseStore store, Guid instanceId, string ownerNode)
    {
        var giveUpAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);

        while (true)
        {
            var acquired = await store.AcquireAsync(instanceId, ownerNode, LongTtl, Cancellation);

            if (acquired.IsSuccess)
            {
                return acquired.Value;
            }

            if (DateTimeOffset.UtcNow > giveUpAt)
            {
                acquired.IsSuccess.ShouldBeTrue(
                    "the lease's own expiry passed five seconds ago and the store still reports " +
                    $"it as held: {acquired.Error}. A TTL that is never enforced strands every " +
                    "instance whose node died.");

                return acquired.Value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Cancellation);
        }
    }

    /// <summary>Asserts a store call succeeded, printing the store's own refusal if not.</summary>
    protected static void ShouldSucceed<T>(Result<T> result, string because) =>
        result.IsSuccess.ShouldBeTrue(
            $"{because} The store refused instead: {(result.IsFailure ? result.Error.ToString() : "no error")}");

    /// <summary>Asserts a store call was refused with the documented code and category.</summary>
    protected static Error ShouldFailWith<T>(
        Result<T> result,
        string code,
        ErrorCategory category,
        string because)
    {
        result.IsFailure.ShouldBeTrue($"{because} The store accepted the call instead.");

        result.Error.Code.ShouldBe(
            code,
            $"{because} A caller branches on the code, so it is part of the contract and not " +
            "of any one store.");

        result.Error.Category.ShouldBe(
            category,
            $"{because} The category carries the retry decision, which is the half that " +
            "changes what a caller does next.");

        return result.Error;
    }
}
