using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>
/// The portability note ADR-0015 and ADR-0016 both left for the second adapter, as a test.
/// </summary>
/// <remarks>
/// <para>
/// Both records say the same thing and neither could assert it: commitment 1 —
/// <c>(instance, scope, step, attempt)</c> as a key — holds in PostgreSQL <em>partly by luck
/// of dialect</em>. <see cref="StepScope.Root"/> renders as the empty string, PostgreSQL
/// treats <c>''</c> as distinct from <c>NULL</c>, and so the flow body is a legal key
/// component there without anybody having decided that it should be. A store that folds the
/// two rejects every root-scope row. <em>Any adapter after the first has to map <c>Root</c>
/// explicitly</em> — and this package is the first adapter after the first.
/// </para>
/// <para>
/// <strong>What the lease store has to do with it, stated plainly rather than implied.</strong>
/// Nothing directly: <see cref="ILeaseStore"/> has no scope in it, and the fourteen assertions
/// of <c>LeaseStoreConformance</c> never mention one. The obligation is nevertheless this
/// package's, because it attaches to the <em>key space</em> rather than to any one contract —
/// <see cref="RedisKeys"/> is where this package decides how a FlowX identifier becomes a
/// Redis key, and a decision that is not taken here is one taken by whoever writes the next
/// operation in a hurry. Deciding it once, now, with the two ADRs that asked for it in view,
/// costs a sentinel character and removes the class of bug outright.
/// </para>
/// </remarks>
public sealed class RedisKeySpaceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary><see cref="StepScope.Root"/> never renders as the empty string.</summary>
    /// <remarks>
    /// The mapping itself, at its narrowest. <c>StepScope.Text</c> is the empty string for the
    /// flow body by design — that is the contract's own rendering and it is right for a store
    /// that can hold an empty value — and this package writes a sentinel instead.
    /// </remarks>
    [Fact]
    public void RootIsMappedToASentinelRatherThanTheEmptyString()
    {
        StepScope.Root.Text.ShouldBeEmpty(
            "the contract renders the flow body as the empty string; that is the premise, not " +
            "the problem.");

        RedisKeys.Scope(StepScope.Root).ShouldBe(
            RedisKeys.RootScope,
            "and this package maps it to something Redis and its client cannot confuse with a " +
            "missing value.");

        RedisKeys.Scope(StepScope.Root).ShouldNotBeEmpty();
    }

    /// <summary>The sentinel round-trips, and so does every scope that is not root.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("7/2")]
    [InlineData("0/0/0")]
    [InlineData("499/12/3")]
    public void EveryScopeRoundTripsThroughTheKeySpace(string text)
    {
        var scope = StepScope.Parse(text);

        RedisKeys.ParseScope(RedisKeys.Scope(scope)).ShouldBe(
            scope,
            "a store persists what it was given and hands the same value back. A mapping that " +
            "is not a round trip is a mapping that loses rows on the read side instead of the " +
            "write side.");
    }

    /// <summary>The sentinel cannot collide with any iteration scope, at any depth.</summary>
    /// <remarks>
    /// A sentinel that some legitimate scope could also render as would be worse than the empty
    /// string: it would attribute one loop element's rows to the flow body. Safe here because
    /// <see cref="StepScope.Parse"/> admits only slash-separated ASCII digits, so no element
    /// index can render as a hyphen — asserted rather than assumed, because that is a property
    /// of a type in another assembly.
    /// </remarks>
    [Fact]
    public void TheRootSentinelCannotCollideWithAnIterationScope()
    {
        Should.Throw<ArgumentException>(() => StepScope.Parse(RedisKeys.RootScope));

        var scopes = new List<string>();

        for (var index = 0; index < 64; index++)
        {
            var element = StepScope.Root.Element(index);

            scopes.Add(RedisKeys.Scope(element));
            scopes.Add(RedisKeys.Scope(element.Element(index)));
        }

        scopes.ShouldNotContain(RedisKeys.RootScope);
        scopes.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            scopes.Count, "and no two scopes render the same either.");
    }

    /// <summary>An empty scope component is refused rather than silently read as root.</summary>
    /// <remarks>
    /// The read half of the same decision. Accepting the empty string would restore exactly the
    /// fold the sentinel exists to remove — a read site that lost the distinction would be
    /// indistinguishable from one that never had it, and its rows would be attributed to the
    /// flow body with nothing to notice.
    /// </remarks>
    [Fact]
    public void AnEmptyScopeComponentIsRefusedRatherThanReadAsRoot()
    {
        var refusal = Should.Throw<ArgumentException>(() => RedisKeys.ParseScope(string.Empty));

        refusal.Message.ShouldContain(
            RedisKeys.RootScope,
            Case.Sensitive,
            "the refusal has to say what this package would have written, or the reader is " +
            "left guessing which side of the round trip is wrong.");
    }

    /// <summary>Every key for one instance shares a cluster hash tag.</summary>
    /// <remarks>
    /// Redis Cluster routes by the substring inside braces. A key layout is the hardest thing
    /// in a Redis deployment to change afterwards, because changing it means migrating live
    /// data rather than shipping a new binary — so the tag is there from the first key, before
    /// anything needs it.
    /// </remarks>
    [Fact]
    public void LeaseKeysCarryAClusterHashTagAroundTheInstance()
    {
        var instance = Guid.NewGuid();

        var key = RedisKeys.Lease("flowx", instance);

        key.ShouldStartWith("flowx:");
        key.ShouldContain("{" + instance.ToString("n") + "}");
        key.ShouldEndWith(":lease");

        RedisKeys.Lease("other", instance).ShouldNotBe(
            key, "the prefix is what separates two deployments sharing one Redis.");
    }

    /// <summary>
    /// The fold this mapping exists to avoid is real, demonstrated against a live Redis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Redis is not Oracle, and the fold is not where the ADRs' example puts it.</strong>
    /// The server does distinguish a stored empty string from a missing field — the first two
    /// assertions below say so, and they are here because a mapping justified by a hazard that
    /// turned out not to exist is superstition, not engineering.
    /// </para>
    /// <para>
    /// <strong>The client is where they merge.</strong> Every read arrives as a
    /// <c>RedisValue</c>, and a missing field and a stored empty string are two values that
    /// <c>IsNullOrEmpty</c> reports identically — so the ordinary way to read one, a null-check
    /// or a <c>??</c> fallback, collapses them by construction. An adapter that stored
    /// <c>Root</c> as <c>""</c> would work until the first read site was written the ordinary
    /// way and would then lose the flow body's rows to a null check that looks correct in
    /// review. The sentinel survives every one of those idioms, which is the last assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RedisFoldsAnEmptyValueIntoAMissingOneAtTheClientBoundary()
    {
        await using var keySpace = await RedisTestKeySpace.CreateAsync(Cancellation);

        var key = keySpace.LeaseKey(Guid.NewGuid());

        await keySpace.Database.HashSetAsync(key, "empty", RedisValue.EmptyString);

        var stored = await keySpace.Database.HashGetAsync(key, "empty");
        var missing = await keySpace.Database.HashGetAsync(key, "absent");

        (await keySpace.Database.HashExistsAsync(key, "empty")).ShouldBeTrue(
            "the server itself keeps the distinction: an empty string is a value.");

        (await keySpace.Database.HashExistsAsync(key, "absent")).ShouldBeFalse();

        stored.IsNullOrEmpty.ShouldBeTrue();
        missing.IsNullOrEmpty.ShouldBeTrue(
            "and the client hands both back as a value that reads as absent. This is Redis's " +
            "version of ''-folds-to-NULL, one layer up from where ADR-0016 expected to find it.");

        ((string?)stored ?? "root").ShouldBe(
            string.Empty,
            "the ?? idiom happens to survive this one, because the conversion is null only for " +
            "a missing field…");

        stored.IsNullOrEmpty.ShouldBe(
            missing.IsNullOrEmpty,
            "…and the IsNullOrEmpty idiom does not, which is the one a reader writes when the " +
            "value is 'optional'.");

        await keySpace.Database.HashSetAsync(key, "scope", RedisKeys.Scope(StepScope.Root));

        var sentinel = await keySpace.Database.HashGetAsync(key, "scope");

        sentinel.IsNullOrEmpty.ShouldBeFalse(
            "the sentinel is a value under every idiom, which is the entire point of mapping " +
            "Root explicitly.");

        RedisKeys.ParseScope(sentinel!).ShouldBe(StepScope.Root);
    }
}
