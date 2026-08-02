using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What the engine does with a declared <c>Cache</c>: stage 5, executed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A cache that never hits is indistinguishable from the inert version it
/// replaces.</strong> So every test here runs the same plan twice and asserts on the
/// <em>second</em> dispatch — the only shape that can tell a consulted cache from an
/// unconsulted one. <c>PolicyExecutionTests.ACacheIsNotConsultedAndARateLimitCountsNothing</c>
/// used exactly that shape to assert the opposite, and half of it is inverted here.
/// </para>
/// <para>
/// <strong>The key is where the danger is, and three tests are about it.</strong>
/// <c>docs/10 §2</c>'s table names "cache before authorisation → tenant A served tenant B's
/// cached data" as the incident stage 5 exists under. The ordering that prevents it is fixed;
/// what is not fixed by ordering is a key that fails to separate two callers, and
/// <see cref="TwoTenantsDoNotShareAnEntry"/>,
/// <see cref="TwoPermissionSetsDoNotShareAPrincipalScopedEntry"/> and
/// <see cref="AnInputThatWasRedactedIsNotKeyedOn"/> are the three ways it could.
/// </para>
/// </remarks>
public sealed partial class CachePolicyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Quote(string Symbol, decimal Price);

    private sealed record Lookup(string Symbol);

    private sealed record Card([property: Sensitive] string Pan);

    [JsonSerializable(typeof(Quote))]
    [JsonSerializable(typeof(Lookup))]
    [JsonSerializable(typeof(Card))]
    private sealed partial class CacheJson : JsonSerializerContext;

    /// <summary>The flow's <c>[Sensitive]</c> members, as the generator emits them.</summary>
    private static readonly string[] Sensitive = ["Pan"];

    /// <summary>
    /// A dispatcher that can key and hold step 0, written by hand as the generator emits it.
    /// </summary>
    /// <remarks>
    /// The entry is a one-member document under the contract's simple name, which is exactly
    /// what <c>RestoreState</c> reads — so a hit is put back by the code a resumed instance
    /// already uses and there is no second deserialiser in this runtime.
    /// </remarks>
    private static RecordingDispatcher Cacheable(string symbol = "MSFT", decimal price = 42m) =>
        new()
        {
            CacheKey = (index, _) => index == 0
                ? JournalPayload.Of(new Lookup(symbol), CacheJson.Default, Sensitive)
                : JournalPayload.Empty,

            CacheEntry = (index, _) => index == 0
                ? JournalPayload.OfState(
                    [JournalMember.Of(nameof(Quote), new Quote(symbol, price), CacheJson.Default)],
                    Sensitive)
                : JournalPayload.Empty,

            // The generator's RestoreState, by hand: a switch over the composed document's
            // members. The cache reuses it rather than carrying a deserialiser of its own,
            // which is why the entry above is composed under the contract's simple name.
            Restore = Restore,
        };

    /// <summary>Puts a composed document back into the bag, exactly as the generator does.</summary>
    private static void Restore(FlowContext ctx, string json)
    {
        using var document = JsonDocument.Parse(json);

        foreach (var member in document.RootElement.EnumerateObject())
        {
            if (string.Equals(member.Name, nameof(Quote), StringComparison.Ordinal))
            {
                ctx.Set(JournalState.Read<Quote>(member.Value, CacheJson.Default));
            }
        }
    }

    private static ExecutionPlan Plan(PolicySet? set = null) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("market.quote", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(
                    0,
                    Plans.Validate,
                    policies: PolicyChain.ForStep(set ?? OneHour, Plans.Validate)),
                StepNode.ForCapability(1, Plans.Capture),
            ]));

    private static PolicySet OneHour => PolicySet.Named("external-read").Cache(TimeSpan.FromHours(1));

    private static FlowInvocation For(string tenant, ClaimsPrincipal? principal = null) =>
        new("corr", "idem", TenantId: tenant, Principal: principal);

    // -------------------------------------------------------------------------- it bites

    /// <summary>
    /// The second execution of a cached step is served without dispatching the capability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Inverted.</strong> <c>ACacheIsNotConsultedAndARateLimitCountsNothing</c> ran the
    /// same plan twice and asserted <c>second.Executed == [0, 1, 2]</c> with the message "a
    /// one-hour cache was declared and the second run dispatched anyway". The step that is
    /// skipped now is step 0, and step 1 still runs — a cache that skipped the whole flow would
    /// be a different and much worse feature.
    /// </para>
    /// <para>
    /// The value is asserted through the context rather than only through the dispatch count,
    /// because a hit that skipped the step without putting anything in the bag would leave the
    /// next step binding to nothing — which is a cache that appears to work and breaks the flow
    /// behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSecondExecutionIsServedFromTheCache()
    {
        var cache = new RecordingCache();
        var engine = new FlowEngine(new FakeClock(T0), cache: cache);
        var plan = Plan();

        plan.HasStepPolicies.ShouldBeTrue(
            "A Cache is now something the engine applies, so the plan-level gate has to open " +
            "for it — ADR-0023's own prediction about how stage 5 would land.");

        var first = Cacheable();
        var second = Cacheable();

        second.Observe = ctx => ctx.TryGet<Quote>(out var quote) ? quote.Price : null;

        (await engine.ExecuteAsync(plan, first, For("acme"), Ct)).IsSuccess.ShouldBeTrue();
        (await engine.ExecuteAsync(plan, second, For("acme"), Ct)).IsSuccess.ShouldBeTrue();

        first.Executed.ShouldBe([0, 1], "nothing was cached yet, so both steps ran");

        cache.Writes.Count.ShouldBe(1, "the step succeeded, so its result was held");

        second.Executed.ShouldBe(
            [1],
            "Step 0 was served out of the cache and never dispatched. This is the assertion " +
            "the inert version of this file made in the opposite direction.");

        second.Observed.ShouldContain(
            42m,
            "and the value reached the state bag, so the step after it has something to bind " +
            "to. A hit that skipped the dispatch and wrote nothing would be worse than a miss.");
    }

    /// <summary>A failed step is not held, so the next execution asks the dependency again.</summary>
    /// <remarks>
    /// <c>docs/10 §8</c>: "negative caching: off — stale failures are worse than a retry". It is
    /// also what stops a cache and a circuit breaker disagreeing: a breaker exists to stop
    /// calling a dependency that is failing, and a cached failure would keep answering for it
    /// long after it recovered.
    /// </remarks>
    [Fact]
    public async Task AFailureIsNotHeld()
    {
        var cache = new RecordingCache();
        var engine = new FlowEngine(new FakeClock(T0), cache: cache);
        var plan = Plan();

        var failing = Cacheable();

        failing.FailAt(0, new Error("quote.declined", "the feed said no", ErrorCategory.Validation));

        (await engine.ExecuteAsync(plan, failing, For("acme"), Ct)).IsSuccess.ShouldBeFalse();

        cache.Writes.ShouldBeEmpty("negative caching is off");

        var second = Cacheable();

        (await engine.ExecuteAsync(plan, second, For("acme"), Ct)).IsSuccess.ShouldBeTrue();

        second.Executed.ShouldBe([0, 1], "so the second execution asks the dependency again");
    }

    /// <summary>A cache that cannot be reached dispatches, and the flow succeeds.</summary>
    /// <remarks>
    /// ADR-0025 §2.3's argument for skipping stage 5 entirely was that "an unconsulted cache
    /// means the call happens. Slower, never wrong". That is now the failure mode rather than
    /// the behaviour, and it has to keep being true or a Redis outage becomes an outage of every
    /// flow that declared a cache.
    /// </remarks>
    [Fact]
    public async Task AStoreThatIsDownDispatchesAndDoesNotFailTheFlow()
    {
        var cache = new RecordingCache { IsDown = true };
        var dispatcher = Cacheable();

        var result = await new FlowEngine(new FakeClock(T0), cache: cache)
            .ExecuteAsync(Plan(), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        dispatcher.Executed.ShouldBe([0, 1]);
        cache.Reads.Count.ShouldBe(1, "it was asked");
    }

    /// <summary>A declared cache with no store configured dispatches, silently and correctly.</summary>
    /// <remarks>
    /// The asymmetry with <c>Audit</c>, which refuses. An unconsulted cache is the honest older
    /// behaviour; an unwritten audit record is a real loss. ADR-0025 §2.3 and §2.4 argued the
    /// two skips separately for exactly this reason, and the difference survives into how the
    /// engine treats a missing seam.
    /// </remarks>
    [Fact]
    public async Task ACacheWithNoStoreConfiguredDispatches()
    {
        var dispatcher = Cacheable();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        dispatcher.Executed.ShouldBe([0, 1]);
    }

    // ---------------------------------------------------------------------------- the key

    /// <summary>Two tenants running the same flow do not share an entry.</summary>
    /// <remarks>
    /// <c>docs/10 §8</c> makes <c>Tenant</c> the default scope because "cross-tenant leakage is
    /// unacceptable by default", and <c>docs/10 §2</c>'s table names it as the incident stage 5
    /// exists under. The ordering guarantee prevents the version of it that comes from caching
    /// before authorising; this is the version that would come from a key that forgot who was
    /// asking.
    /// </remarks>
    [Fact]
    public async Task TwoTenantsDoNotShareAnEntry()
    {
        var cache = new RecordingCache();
        var engine = new FlowEngine(new FakeClock(T0), cache: cache);
        var plan = Plan();

        await engine.ExecuteAsync(plan, Cacheable(), For("acme"), Ct);

        var other = Cacheable();

        await engine.ExecuteAsync(plan, other, For("initech"), Ct);

        other.Executed.ShouldBe(
            [0, 1],
            "A second tenant asking the same question of the same capability is a miss. " +
            "Serving it acme's answer is the row docs/10 §2 calls unacceptable by default.");
    }

    /// <summary>Two different inputs do not share an entry.</summary>
    /// <remarks>
    /// The most basic property a cache key has, asserted because the key is a hash of a
    /// composed document and a hash of the wrong components would still produce a plausible
    /// key — one that hit every time and answered every question with the first one's answer.
    /// </remarks>
    [Fact]
    public async Task TwoInputsDoNotShareAnEntry()
    {
        var cache = new RecordingCache();
        var engine = new FlowEngine(new FakeClock(T0), cache: cache);
        var plan = Plan();

        await engine.ExecuteAsync(plan, Cacheable("MSFT"), For("acme"), Ct);

        var other = Cacheable("AAPL");

        await engine.ExecuteAsync(plan, other, For("acme"), Ct);

        other.Executed.ShouldBe([0, 1], "a different symbol is a different question");
    }

    /// <summary>Under <c>CacheScope.Principal</c>, two permission sets do not share an entry.</summary>
    /// <remarks>
    /// <c>docs/10 §8</c>'s key names the "principal permission set" and gives the reason:
    /// "prevents privilege-based leakage". The claims are read through exactly
    /// <c>StepAuthorization.PermissionClaimTypes</c>, so what a cache key is scoped by and what
    /// a stance is decided from are the same set of claims read the same way — a cache scoped by
    /// a permission set derived differently would be a cache keyed on a fiction.
    /// </remarks>
    [Fact]
    public async Task TwoPermissionSetsDoNotShareAPrincipalScopedEntry()
    {
        var cache = new RecordingCache();
        var engine = new FlowEngine(new FakeClock(T0), cache: cache);
        var plan = Plan(PolicySet.Named("scoped").Cache(TimeSpan.FromHours(1), CacheScope.Principal));

        await engine.ExecuteAsync(plan, Cacheable(), For("acme", Holding("quotes.read quotes.delayed")), Ct);

        var repeat = Cacheable();

        await engine.ExecuteAsync(plan, repeat, For("acme", Holding("quotes.delayed quotes.read")), Ct);

        repeat.Executed.ShouldBe(
            [1],
            "The same permissions in a different order are the same permission set. A token's " +
            "claim order is the issuer's business and must not split the key.");

        var privileged = Cacheable();

        await engine.ExecuteAsync(plan, privileged, For("acme", Holding("quotes.read quotes.realtime")), Ct);

        privileged.Executed.ShouldBe(
            [0, 1],
            "A different permission set is a different key. This is docs/10 §8's " +
            "privilege-based leakage, and it is the whole reason the scope exists.");
    }

    // ------------------------------------------------------------ redaction and the cache

    /// <summary>
    /// A step whose input carries a <c>[Sensitive]</c> member is never keyed and never held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Structural, not remembered.</strong> The dispatcher answers with a
    /// <c>JournalPayload</c> whose only exit replaces a marked member with
    /// <c>[redacted]</c> — there is no accessor for the value and the cache did not get a
    /// second one. Two different cards would therefore key identically, so the engine refuses
    /// the key outright and dispatches.
    /// </para>
    /// <para>
    /// <strong>The assertion is on the store, not on the outcome.</strong> "The flow still
    /// works" would be true of a cache that keyed on <c>[redacted]</c> and served one caller
    /// another's answer, which is the failure being prevented. What has to be true is that the
    /// store was never asked and never written.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInputThatWasRedactedIsNotKeyedOn()
    {
        var cache = new RecordingCache();

        var dispatcher = new RecordingDispatcher
        {
            CacheKey = (index, _) => index == 0
                ? JournalPayload.Of(new Card("4111111111111111"), CacheJson.Default, Sensitive)
                : JournalPayload.Empty,

            CacheEntry = (index, _) => index == 0
                ? JournalPayload.OfState(
                    [JournalMember.Of(nameof(Quote), new Quote("MSFT", 42m), CacheJson.Default)],
                    Sensitive)
                : JournalPayload.Empty,
        };

        var result = await new FlowEngine(new FakeClock(T0), cache: cache)
            .ExecuteAsync(Plan(), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        cache.Reads.ShouldBeEmpty(
            "The key document came out carrying [redacted] where the distinguishing value " +
            "should be, so two different cards would key identically. It is refused rather " +
            "than hashed.");

        cache.Writes.ShouldBeEmpty("and nothing is held under a key that was never built");
    }

    /// <summary>
    /// A step whose <em>result</em> carries a <c>[Sensitive]</c> member is keyed and not held.
    /// </summary>
    /// <remarks>
    /// The second half, enforced separately because the two failures are different. A key is
    /// refused when it was redacted because it would collide; an entry is refused when it was
    /// redacted because a hit would hand the flow <c>[redacted]</c> where a capability's answer
    /// should be, and the steps after it would bind to a value nothing produced. Between the
    /// two, a marked member cannot reach a cache and cannot come back out of one.
    /// </remarks>
    [Fact]
    public async Task AResultThatWasRedactedIsNotHeld()
    {
        var cache = new RecordingCache();

        var dispatcher = new RecordingDispatcher
        {
            CacheKey = (index, _) => index == 0
                ? JournalPayload.Of(new Lookup("MSFT"), CacheJson.Default, Sensitive)
                : JournalPayload.Empty,

            CacheEntry = (index, _) => index == 0
                ? JournalPayload.OfState(
                    [JournalMember.Of(nameof(Card), new Card("4111111111111111"), CacheJson.Default)],
                    Sensitive)
                : JournalPayload.Empty,
        };

        var result = await new FlowEngine(new FakeClock(T0), cache: cache)
            .ExecuteAsync(Plan(), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        cache.Reads.Count.ShouldBe(1, "the key was fine, so the cache was consulted");

        cache.Writes.ShouldBeEmpty(
            "and the result was not held: a hit would have restored [redacted] into the bag, " +
            "and the step after it would bind to a value no capability produced.");
    }

    /// <summary>A dispatcher that describes no key is never cached, and never fails.</summary>
    /// <remarks>
    /// <c>IStepDispatcher.DescribeCacheKey</c> defaults to <c>JournalPayload.Empty</c>, so a
    /// hand-written dispatcher — and a generated one for a flow whose contracts no context
    /// declares — caches nothing rather than caching wrongly. The plan still reports the policy
    /// and the manifest still publishes it; what does not happen is a guess.
    /// </remarks>
    [Fact]
    public async Task ADispatcherThatDescribesNoKeyIsNotCached()
    {
        var cache = new RecordingCache();
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0), cache: cache)
            .ExecuteAsync(Plan(), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        dispatcher.Executed.ShouldBe([0, 1]);
        cache.Reads.ShouldBeEmpty("not a miss — a miss is a cache that was asked, and this was not");
    }

    private static ClaimsPrincipal Holding(string permissions) => new(
        new ClaimsIdentity([new Claim("permission", permissions)], authenticationType: "Test"));
}
