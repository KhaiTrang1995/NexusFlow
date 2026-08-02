namespace FlowX;

/// <summary>
/// What a deployment bounds each tenant to, so that one cannot starve another.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Row isolation and fairness are different guarantees and fail apart.</strong>
/// <c>TenantIsolation.Row</c> stops tenant <em>A</em> reading tenant <em>B</em>'s rows and does
/// nothing whatever about <em>A</em> consuming every sweep slot, every limiter permit and every
/// journal connection while <em>B</em> waits. <c>docs/16 §4</c> names six mechanisms for the
/// second problem; this is where a deployment turns them on.
/// </para>
/// <para>
/// <strong>Every bound is off by default, and off costs one boolean.</strong>
/// <see cref="IsEnabled"/> is read once per admission and once per sweep. A single-tenant
/// deployment never reaches it at all — <c>FlowHost</c> branches on
/// <see cref="TenantIsolation.None"/> first — so budget <strong>B2</strong> is untouched by
/// construction rather than by care, which is the bargain
/// <c>ExecutionPlan.HasAuthorizedSteps</c> struck one layer down.
/// </para>
/// <para>
/// <strong>Where each bound is applied.</strong> The first three are admission control and are
/// applied at stage 1, before a lease is taken and before a journal row exists — <c>docs/16 §4</c>
/// requires it and the requirement is not stylistic, because "<em>rejecting expensively is how
/// rate limiting becomes the DoS</em>". The fourth is scheduling and is applied where a noisy
/// tenant's work actually queues: the timer and recovery sweeps, which pull a bounded page of
/// due work and have a bounded number of slots to spend on it.
/// </para>
/// </remarks>
public sealed class TenantFairness
{
    /// <summary>
    /// Permits one tenant may spend per <see cref="Window"/>, or zero for no rate limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The same mechanism as a step's <c>RateLimit</c>, under a different key, and
    /// deliberately not a second one.</strong> Both are token buckets over
    /// <see cref="IRateLimiterStore"/>, and the reason that store exists —
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>'s
    /// "n nodes admit n × the declared rate" — applies with more force here, not less: a
    /// per-tenant bound that each node enforced for itself would be a plan limit multiplied by
    /// the replica count, which is a commercial promise the deployment cannot keep. What differs
    /// is the key and the place. A step's limit is keyed by capability and is spent inside the
    /// step loop; this is keyed by tenant alone and is spent at admission, so a refused tenant
    /// costs one round trip rather than a lease, a row and a step.
    /// </para>
    /// <para>
    /// A deployment that sets this and registers no <see cref="IRateLimiterStore"/> is refused
    /// at startup rather than admitted unbounded — the same stance a step's <c>RateLimit</c>
    /// takes for the same reason.
    /// </para>
    /// </remarks>
    public int PermitsPerWindow { get; set; }

    /// <summary>The period <see cref="PermitsPerWindow"/> is granted over.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Invocations one tenant may make per <see cref="QuotaWindow"/>, or zero for no quota.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The same mechanism again, and the honest answer is that it is a rate limit with a
    /// long window — with one place the two genuinely differ.</strong> A quota's purpose is
    /// commercial rather than protective: it enforces a plan limit rather than smoothing a
    /// burst, and it is the reason a tenant sees <c>tenant.quota_exhausted</c> and calls its
    /// account manager instead of retrying. That difference is worth a separate bucket, a
    /// separate window and a separate refusal, and it is not worth a separate store: the read,
    /// the refill and the decrement are the same indivisible operation, and a second contract
    /// asking the same server the same question would be two implementations to hold to one
    /// conformance suite.
    /// </para>
    /// <para>
    /// <strong>Where it stops being the same, and this is the limitation rather than the
    /// design.</strong> <see cref="IRateLimiterStore"/> is a token bucket, so it refills
    /// <em>continuously</em>: a tenant that exhausts a monthly quota on the first day is
    /// admitted again a few seconds later at one thirty-millionth of the budget per second,
    /// rather than being refused until the first of the next month. A quota that resets on a
    /// calendar boundary is a fixed-window counter and is not this contract. So this bounds a
    /// tenant's <em>long-run average</em> to the plan it bought, which is what protects the
    /// platform, and a deployment billing against a calendar month reads its counters out of
    /// the billing export <c>docs/16 §8</c> names rather than out of this.
    /// </para>
    /// </remarks>
    public int QuotaPerWindow { get; set; }

    /// <summary>The period <see cref="QuotaPerWindow"/> is granted over.</summary>
    public TimeSpan QuotaWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Flows one tenant may have executing at once, or zero for no bulkhead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A pool per tenant rather than a share of one pool, which is what makes it a
    /// bulkhead.</strong> A shared pool with a per-tenant cap still lets a tenant hold a slot
    /// another tenant is waiting for; a pool each means there are no slots to consume. That is
    /// <c>docs/16 §4</c>'s "one tenant cannot consume all slots" read as an architecture rather
    /// than as an arithmetic.
    /// </para>
    /// <para>
    /// <strong>It sheds and does not queue</strong> — §4's "429 · shed early, do not queue" —
    /// unlike a step's <c>Bulkhead</c>, which has a queue depth. The difference is what is
    /// behind each: a step's caller is already inside a flow that holds a lease and a journal
    /// row, so waiting is cheaper than unwinding, and an admission's caller holds nothing at
    /// all, so waiting is a thread and a socket held open for work the platform has already
    /// decided it will not do.
    /// </para>
    /// <para>
    /// <strong>This one is per node, and saying so is the point.</strong> Concurrency is a
    /// property of a process's threads and connections, so a shared counter would bound
    /// something no single node owns and would cost a round trip on the acquire <em>and</em> the
    /// release. n nodes therefore admit n × this, which is the conservative direction — the
    /// bound protects each node's own resources, which is what the mechanism is for — and it is
    /// the opposite direction from a per-process rate limit, which is why that one is refused
    /// and this one is not.
    /// </para>
    /// </remarks>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// How many of a sweep's page one tenant may occupy, or zero for the page a sweep always
    /// took.
    /// </summary>
    /// <remarks>
    /// The store-side half of fair queueing, passed through to
    /// <see cref="DueInstanceQuery.PerTenantLimit"/> and
    /// <see cref="AbandonedInstanceQuery.PerTenantLimit"/>. Without it the page is the oldest
    /// work in the table and a tenant with a long enough backlog owns all of it, so the sweep
    /// cannot schedule fairly for the simple reason that it has never seen the other tenant's
    /// work. Set it and the sweep's scheduler has something to be fair between.
    /// </remarks>
    public int PerTenantScanShare { get; set; }

    /// <summary>
    /// What each tenant's share of a sweep is worth, for tenants that are not worth one share.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>weighted</em> in <c>docs/16 §4</c>'s weighted fair queueing. A tenant absent from
    /// this map has weight one, so an empty map — the default — is plain round robin, and a
    /// tenant given three is served three candidates per round to everyone else's one. This is
    /// how an enterprise plan and a free plan share a node without either starving: the free
    /// tenant's share is small and it is never zero.
    /// </para>
    /// <para>
    /// A weight of zero is refused at startup rather than honoured. It would express "never
    /// schedule this tenant", which is a suspension — <c>docs/16 §7</c>'s lifecycle state, whose
    /// contract is that in-flight durable flows still complete — and expressing it here would
    /// suspend a tenant by silently never waking its parked instances, which is the starvation
    /// this whole option exists to prevent, arriving through the option meant to prevent it.
    /// </para>
    /// </remarks>
    public IDictionary<string, int> Weights { get; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Whether any bound is set at all.</summary>
    /// <remarks>
    /// Read once per admission and once per sweep, so a deployment that isolates by tenant and
    /// bounds nothing pays one comparison rather than a store round trip it did not ask for.
    /// </remarks>
    public bool IsEnabled =>
        PermitsPerWindow > 0 || QuotaPerWindow > 0 || MaxConcurrency > 0 || PerTenantScanShare > 0;

    /// <summary>Whether admission has anything to decide.</summary>
    /// <remarks>
    /// The three stage-1 mechanisms, separately from <see cref="PerTenantScanShare"/>, which is
    /// a sweep's concern and reaches no invocation.
    /// </remarks>
    public bool BoundsAdmission =>
        PermitsPerWindow > 0 || QuotaPerWindow > 0 || MaxConcurrency > 0;

    /// <summary>What one tenant's share of a round is worth.</summary>
    /// <param name="tenantId">The tenant, or null for untenanted work.</param>
    /// <returns>The declared weight, or one.</returns>
    public int WeightOf(string? tenantId) =>
        tenantId is not null && Weights.TryGetValue(tenantId, out var weight) ? weight : 1;
}
