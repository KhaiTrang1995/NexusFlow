using FlowX;

namespace Crm;

/// <summary>
/// How long work is allowed to sit before somebody is told about it.
/// </summary>
/// <remarks>
/// <strong>Constants, and unlike §7's process they are not configuration.</strong> An
/// administrator configures which transitions exist and what they do; how often an overdue task
/// nags is an operational property of the deployment, and a tenant able to set it to one second
/// would be a tenant able to make the sweep run forever. Moving either of these is a code change
/// and a deployment, which is the same line §7.3 draws.
/// </remarks>
public static class SlaPolicy
{
    /// <summary>How long between one escalation of an overdue task and the next.</summary>
    public static readonly TimeSpan EscalationWindow = TimeSpan.FromDays(1);

    /// <summary>How long an opportunity may sit in one stage before it is called stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>How many escalations a task gets before the sweep leaves it alone.</summary>
    /// <remarks>
    /// <strong>A ceiling, because an unbounded nag is a nag nobody reads.</strong> A task nobody
    /// has touched after three escalations is not going to be fixed by a fourth; what it needs
    /// is a person, and the count is what tells them how long it has been waiting.
    /// </remarks>
    public const int MaxEscalations = 3;
}

/// <summary>
/// When a task is due for its next escalation, and when an opportunity has gone quiet.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pure, so "escalates once per window" is a property that can be tested rather than
/// observed.</strong> Every one of these is a function of an instant, a stored count and a
/// constant. The sweep asks the same questions of the database in one statement; these are what
/// say what that statement means.
/// </para>
/// <para>
/// <strong>The nth escalation is due at <c>dueAt + (n − 1) × window</c>.</strong> Counting from
/// the due date rather than from the last escalation means the schedule does not drift when the
/// sweep is late, and it needs no column recording when the last one happened — the count on the
/// row is enough to place it.
/// </para>
/// </remarks>
public static class SlaRules
{
    /// <summary>Whether a task is due for another escalation.</summary>
    /// <param name="dueAt">When it was due, or null when it never was.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="escalationCount">How many times it has been escalated already.</param>
    /// <param name="window">How long between escalations.</param>
    /// <returns>True when the next escalation's moment has passed.</returns>
    public static bool NeedsEscalation(
        DateTimeOffset? dueAt,
        DateTimeOffset now,
        int escalationCount,
        TimeSpan window) =>
        dueAt is { } due &&
        escalationCount < SlaPolicy.MaxEscalations &&
        due + (window * escalationCount) <= now;

    /// <summary>How many escalations a task should have had by now.</summary>
    /// <param name="dueAt">When it was due, or null.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="window">How long between escalations.</param>
    /// <returns>The count, capped at <see cref="SlaPolicy.MaxEscalations"/>.</returns>
    /// <remarks>
    /// <strong>What a sweep that has not run for a week owes.</strong> It is deliberately not
    /// what the sweep then does: one run raises the count by one, so a backlog is worked off a
    /// window at a time rather than in a burst of notifications nobody asked for.
    /// </remarks>
    public static int EscalationsDue(DateTimeOffset? dueAt, DateTimeOffset now, TimeSpan window)
    {
        if (dueAt is not { } due || now < due || window <= TimeSpan.Zero)
        {
            return 0;
        }

        var elapsed = now - due;

        return (int)Math.Min(SlaPolicy.MaxEscalations, (elapsed.Ticks / window.Ticks) + 1);
    }

    /// <summary>Whether an opportunity has sat in one stage too long.</summary>
    /// <param name="stageEnteredAt">When it arrived where it is.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="after">How long is too long.</param>
    /// <param name="outcome">Its outcome, or null while it is still open.</param>
    /// <returns>True when an open opportunity has been still for longer than <paramref name="after"/>.</returns>
    /// <remarks>
    /// <strong>A closed opportunity is never stale.</strong> Won, lost and abandoned deals sit
    /// in their final stage forever by design, and a sweep that nagged about them would make its
    /// own output useless within a quarter.
    /// </remarks>
    public static bool IsStale(
        DateTimeOffset stageEnteredAt,
        DateTimeOffset now,
        TimeSpan after,
        OpportunityOutcome? outcome) =>
        outcome is null && stageEnteredAt + after <= now;
}

// ---------------------------------------------------------------------------------- contracts

/// <summary>Asks for a task against something.</summary>
/// <param name="Kind">What kind of work it is.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="RelatesTo">What it hangs off.</param>
/// <param name="Owner">Who owes it.</param>
/// <param name="DueAt">When, or null when nothing is promised.</param>
public sealed record CreateTask(
    ActivityKind Kind,
    string Subject,
    RelatedRef RelatesTo,
    Guid Owner,
    DateTimeOffset? DueAt);

/// <summary>The task that was written.</summary>
/// <param name="ActivityId">The activity.</param>
/// <param name="DueAt">When it is due, or null.</param>
public sealed record TaskCreated(Guid ActivityId, DateTimeOffset? DueAt);

/// <summary>What one sweep of the overdue tasks did.</summary>
/// <param name="Escalated">How many tasks were escalated.</param>
/// <param name="At">The occurrence the schedule fired for.</param>
public sealed record TasksEscalated(int Escalated, DateTimeOffset At);

/// <summary>What one sweep of the pipeline found.</summary>
/// <param name="Stale">How many open opportunities had gone quiet.</param>
/// <param name="At">The occurrence the schedule fired for.</param>
public sealed record StaleOpportunitiesSwept(int Stale, DateTimeOffset At);

/// <summary>Refusals the work flows can produce.</summary>
public static class WorkErrors
{
    /// <summary>A task was asked for against something this tenant does not have.</summary>
    /// <param name="relatesTo">What was named.</param>
    /// <remarks>
    /// <strong>The trigger of migration 0003 would refuse this too, and that is the point of
    /// checking here.</strong> A caller deserves an error naming what they got wrong rather than
    /// a constraint violation; the trigger is what makes the check unskippable.
    /// </remarks>
    public static Error TaskHasNoSubject(RelatedRef relatesTo) =>
        new Error(
            "crm.task_subject_not_found",
            $"There is no {relatesTo.Kind} in this tenant with that id.",
            ErrorCategory.NotFound)
            .With("kind", relatesTo.Kind.ToString())
            .With("id", relatesTo.Id);
}
