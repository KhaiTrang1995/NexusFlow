using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>
/// What the conformance suite cannot see: the shape of what this adapter left in Redis.
/// </summary>
/// <remarks>
/// <para>
/// <c>LeaseStoreConformance</c> asserts behaviour through <see cref="ILeaseStore"/>, which is
/// exactly right and is why it travels between stores unchanged. It follows that it cannot
/// assert the one thing that makes this adapter different from the textbook Redis lease —
/// that the key holding the fencing-token counter carries no Redis expiry. The suite catches
/// the <em>consequence</em> of getting that wrong, and only after waiting out a TTL; these
/// tests catch the cause, immediately, and say what to look at.
/// </para>
/// <para>
/// Both are needed. Dropping the suite for these would test the adapter against its own idea
/// of itself; dropping these for the suite would leave the reason the adapter is written the
/// way it is asserted nowhere.
/// </para>
/// </remarks>
public sealed class RedisLeaseStoreTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// The key holding the counter is never given a Redis expiry, on any path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the whole design, asserted directly.</strong> The idiomatic Redis lease
    /// is <c>SET key owner NX PX ttl</c> and lets the server delete the key; the deletion takes
    /// the fencing-token counter with it, and the next acquisition hands a returning zombie a
    /// token equal to the one its successor is using. Checked on all four paths, because an
    /// implementation that got <c>Acquire</c> right could still put a <c>PEXPIRE</c> in
    /// <c>Renew</c> and reintroduce it wherever a lease is renewed at all.
    /// </para>
    /// <para>
    /// The lapse at the end is the important one: after the lease has expired the key must
    /// still be there, holding the token that was issued. That is the difference between an
    /// expiry that ends a lease and an expiry that ends the sequence.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheLeaseKeyNeverCarriesARedisExpiry()
    {
        await using var keySpace = await RedisTestKeySpace.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();

        var held = await keySpace.Leases.AcquireAsync(
            instance, "node-1", TimeSpan.FromMilliseconds(200), Cancellation);

        held.IsSuccess.ShouldBeTrue();

        (await keySpace.TimeToLiveAsync(instance)).ShouldBeNull(
            "an acquisition that sets a Redis TTL is an acquisition whose counter Redis will " +
            "delete. Expiry is a value in the hash, not a property of the key.");

        var renewed = await keySpace.Leases.RenewAsync(held.Value, TimeSpan.FromMilliseconds(200), Cancellation);

        renewed.IsSuccess.ShouldBeTrue();

        (await keySpace.TimeToLiveAsync(instance)).ShouldBeNull(
            "and a renewal does not add one either.");

        var released = await keySpace.Leases.ReleaseAsync(renewed.Value, Cancellation);

        released.IsSuccess.ShouldBeTrue();

        (await keySpace.KeyExistsAsync(instance)).ShouldBeTrue(
            "releasing expires the lease in place. Deleting the key here is the quiet way to " +
            "lose exclusivity two acquisitions later.");

        (await keySpace.TimeToLiveAsync(instance)).ShouldBeNull(
            "and it does not leave a TTL behind to finish the job later.");

        var lapsed = await keySpace.Leases.AcquireAsync(
            instance, "node-2", TimeSpan.FromMilliseconds(150), Cancellation);

        lapsed.IsSuccess.ShouldBeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(400), Cancellation);

        (await keySpace.KeyExistsAsync(instance)).ShouldBeTrue(
            "a lease that lapsed because nobody renewed it must leave its key — and its token " +
            "— behind. This is the assertion the whole adapter is shaped around.");

        (await keySpace.TimeToLiveAsync(instance)).ShouldBeNull();
    }

    /// <summary>
    /// The token counter is a field that outlives every lease written over it.
    /// </summary>
    /// <remarks>
    /// The conformance suite proves the tokens increase. This proves <em>why</em> they can:
    /// the value is read out of Redis directly, so an implementation that kept a counter in
    /// process memory — which would pass the suite on one node and fail on two — is refused
    /// here.
    /// </remarks>
    [Fact]
    public async Task TheCounterLivesInRedisRatherThanInTheProcess()
    {
        await using var keySpace = await RedisTestKeySpace.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();

        var first = await keySpace.Leases.AcquireAsync(instance, "node-1", TimeSpan.FromMinutes(5), Cancellation);

        first.IsSuccess.ShouldBeTrue();

        (await keySpace.Database.HashGetAsync(keySpace.LeaseKey(instance), "token")).ShouldBe(
            (RedisValue)first.Value.Token.Value,
            "the token a caller was handed is the one Redis is holding, not one this process " +
            "remembered.");

        await keySpace.Leases.ReleaseAsync(first.Value, Cancellation);

        (await keySpace.Database.HashGetAsync(keySpace.LeaseKey(instance), "token")).ShouldBe(
            (RedisValue)first.Value.Token.Value,
            "a release moves the expiry and leaves the counter exactly where it is.");

        // A second store object, as a second node would be: no shared memory, same Redis.
        var otherNode = new RedisLeaseStore(keySpace.Database.Multiplexer, keySpace.Options);

        var second = await otherNode.AcquireAsync(instance, "node-2", TimeSpan.FromMinutes(5), Cancellation);

        second.IsSuccess.ShouldBeTrue();

        second.Value.Token.ShouldBeGreaterThan(
            first.Value.Token,
            "a different node continues the sequence rather than starting one, which is only " +
            "possible because the counter was never in either node's memory.");
    }

    /// <summary>
    /// A node that wakes up after its lease lapsed is refused by the token, not by its clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WP-54's exit criterion asks for stale-token rejection proven against Redis, and this is
    /// that proof at the adapter rather than through the suite's abstraction. The zombie is
    /// given a copy of its lease with a future expiry — its own view of the world, which is
    /// precisely the thing that cannot be trusted — and every operation it attempts is refused
    /// with <c>lease.lost</c>.
    /// </para>
    /// <para>
    /// The last assertion is the one that matters to the journal: the successor's token is
    /// strictly greater, so a write the zombie manages to get out is below the fence and is
    /// rejected there too. The lease refuses it twice and the journal refuses it a third time,
    /// which is the arrangement ADR-0006 asks for — correctness that does not depend on any
    /// clock being right.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AZombieIsRefusedByItsTokenAfterTheLeaseLapsed()
    {
        await using var keySpace = await RedisTestKeySpace.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();

        var zombie = await keySpace.Leases.AcquireAsync(
            instance, "paused-node", TimeSpan.FromMilliseconds(150), Cancellation);

        zombie.IsSuccess.ShouldBeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(300), Cancellation);

        var successor = await keySpace.Leases.AcquireAsync(
            instance, "live-node", TimeSpan.FromMinutes(5), Cancellation);

        successor.IsSuccess.ShouldBeTrue(
            successor.IsFailure ? successor.Error.ToString() : string.Empty);

        var zombiesView = zombie.Value with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };

        var renew = await keySpace.Leases.RenewAsync(zombiesView, TimeSpan.FromMinutes(5), Cancellation);

        renew.IsFailure.ShouldBeTrue("the zombie's token is not the current one.");
        renew.Error.Code.ShouldBe(DurabilityErrors.LeaseLostCode);
        renew.Error.Category.ShouldBe(ErrorCategory.Forbidden);

        var release = await keySpace.Leases.ReleaseAsync(zombiesView, Cancellation);

        release.IsFailure.ShouldBeTrue(
            "and a tidy shutdown must not evict the node that took over. This is the quietest " +
            "way to lose exclusivity: nothing errors anywhere and a third node acquires while " +
            "the second is mid-step.");
        release.Error.Code.ShouldBe(DurabilityErrors.LeaseLostCode);

        var read = await keySpace.Leases.ReadAsync(instance, Cancellation);

        read.IsSuccess.ShouldBeTrue();
        read.Value.OwnerNode.ShouldBe("live-node", "the successor still holds it.");
        read.Value.Token.ShouldBeGreaterThan(
            zombie.Value.Token,
            "and with a token the journal's fence will accept over the zombie's.");
    }

    /// <summary>An acquisition without an owner is refused before it reaches Redis.</summary>
    /// <remarks>
    /// The owner is what an operator reads to find out which node is holding an instance, and
    /// a blank one is a lease nobody can attribute. Refused as an argument fault rather than as
    /// a <c>Result</c> because it is a caller defect, not a store outcome (ADR-0007 draws the
    /// line there).
    /// </remarks>
    [Fact]
    public async Task AcquiringWithoutAnOwnerIsACallerDefect()
    {
        await using var keySpace = await RedisTestKeySpace.CreateAsync(Cancellation);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await keySpace.Leases.AcquireAsync(
                Guid.NewGuid(), string.Empty, TimeSpan.FromMinutes(5), Cancellation));
    }
}
