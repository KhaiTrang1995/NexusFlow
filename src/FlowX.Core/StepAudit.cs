using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// A step's <see cref="PolicyStage.Consistency"/> <c>Audit</c>, resolved out of its declared
/// <see cref="PolicyChain"/> into the two values the record needs: a category and a list of
/// members to strip.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stage 7, and only the audit half of it.</strong> The other stage-7 kind,
/// <c>CompensationRetry</c>, wraps the step's <em>undo</em> and is resolved by
/// <see cref="CompensationPolicy.From"/> off a different chain. Two kinds share a stage and
/// neither reads the other's parameters, which is exactly why
/// the deleted <c>FLOWX1032</c> insisted the cut was never a range of stages.
/// </para>
/// <para>
/// <strong>Resolved once, when the plan is built</strong>, and hung off
/// <see cref="StepNode.StepAudit"/> — the shape
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>
/// fixed and <see cref="StepAuthorization"/> struck a second time. A step declaring no audit
/// holds the shared <see cref="None"/> and answers one comparison. The plan-level gate is
/// <see cref="ExecutionPlan.HasAuditedSteps"/>, so a flow that audits nothing never reads a
/// principal, never asks the dispatcher for a payload and never touches a sink.
/// </para>
/// <para>
/// <strong>Why <em>not</em> a field on <see cref="StepPolicy"/>, when <c>Cache</c> is.</strong>
/// Stage 5 runs inside stage 4's nesting — around the dispatch and under the timeout
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
/// §2.5) — so it is reached from the same call the four resilience kinds are reached from and
/// belongs on the same resolved value. Stage 7 runs <em>after</em> the step and its commit,
/// outside every one of them, so folding it into <see cref="StepPolicy.IsActive"/> would make
/// that flag mean "the engine wraps this step" for three kinds and "the engine does something
/// somewhere" for a fourth.
/// </para>
/// </remarks>
public sealed class StepAudit
{
    private StepAudit(string? category, ImmutableArray<string> redact)
    {
        Category = category;
        Redact = redact;
    }

    /// <summary>Nothing to record: what a step that declares no <c>Audit</c> gets.</summary>
    public static StepAudit None { get; } = new(null, ImmutableArray<string>.Empty);

    /// <summary>The declared category, or <c>null</c> when no audit was declared.</summary>
    public string? Category { get; }

    /// <summary>
    /// Members the record must not carry, on top of the flow's <c>[Sensitive]</c> ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what <c>redact</c> means, and it is the answer <c>PLAN §6a</c> asked
    /// for.</strong> The names go to <c>JournalPayload.OfState</c> as additional sensitive
    /// members, alongside the flow's own, and are matched by the one redaction pass that
    /// already exists — case-insensitively, at every depth, replaced with
    /// <c>JournalPayload.Redacted</c>. There is no second implementation of redaction and no
    /// second exit from a value; <c>redact</c> is a longer list handed to the same pass.
    /// </para>
    /// <para>
    /// <strong>It can only ever remove.</strong> An audit record is therefore never more
    /// revealing than the journal row for the same step, whatever an author writes here — the
    /// property that makes this list safe to accept from the DSL without a second review of
    /// what it is allowed to name.
    /// </para>
    /// </remarks>
    public ImmutableArray<string> Redact { get; }

    /// <summary>True when this step has a record to write.</summary>
    /// <remarks>
    /// The single question the step loop asks, and what makes
    /// <see cref="ExecutionPlan.HasAuditedSteps"/> mean "some step of this plan writes a
    /// record" rather than "some step declared something" — the bargain
    /// <see cref="StepPolicy.IsActive"/> and <see cref="StepAuthorization.CanRefuse"/> both
    /// strike.
    /// </remarks>
    public bool IsAudited => Category is not null;

    /// <summary>The descriptor kind <see cref="PolicySet.Audit"/> emits.</summary>
    /// <remarks>
    /// Published as a constant for <see cref="StepPolicy.TimeoutKind"/>'s reason:
    /// <c>PolicyStageFitnessTests</c> pins <c>DeclaredPolicyAnalyzer.ExecutedKinds</c> against
    /// the constants the resolvers actually read, and a kind implemented without one would slip
    /// past that gate unnoticed, and would leave a declared kind reaching no resolver at all.
    /// </remarks>
    public const string AuditKind = "Audit";

    /// <summary>The parameter <see cref="PolicySet.Audit"/> stores the category under.</summary>
    public const string CategoryParameter = "category";

    /// <summary>The parameter <see cref="PolicySet.Audit"/> stores the redact list under.</summary>
    public const string RedactParameter = "redact";

    /// <summary>
    /// Reads the <c>Audit</c> out of a chain, or <see cref="None"/> when it declares none.
    /// </summary>
    /// <param name="policies">The step's own chain, already ordered by stage.</param>
    /// <remarks>
    /// Tolerant of a chain carrying other kinds, for <see cref="StepPolicy.From"/>'s reason: a
    /// set may legitimately declare a timeout, a cache and an audit together, and this type is
    /// one stage's view of the author's whole declaration.
    /// <para>
    /// A blank category resolves to <see cref="None"/>. <c>PolicySet.Audit</c> does not refuse
    /// one — no builder method validates its arguments — and a record whose category is the
    /// empty string is one no compliance query can select, so writing it would be worse than
    /// not auditing while looking like auditing.
    /// </para>
    /// </remarks>
    public static StepAudit From(PolicyChain policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        string? category = null;
        var redact = ImmutableArray<string>.Empty;

        foreach (var policy in policies.Ordered)
        {
            if (!string.Equals(policy.Kind, AuditKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (policy.Parameters.TryGetValue(CategoryParameter, out var declared) &&
                declared is string { Length: > 0 } named &&
                !string.IsNullOrWhiteSpace(named))
            {
                category = named;
            }

            if (policy.Parameters.TryGetValue(RedactParameter, out var members) &&
                members is string[] names)
            {
                redact = [.. names.Where(static name => !string.IsNullOrWhiteSpace(name))];
            }
        }

        return category is null ? None : new StepAudit(category, redact);
    }
}
