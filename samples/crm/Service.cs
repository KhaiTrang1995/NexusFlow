using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- the vocabulary

/// <summary>How urgent a case is, and therefore which promise governs it.</summary>
public enum CasePriority
{
    /// <summary>A nuisance.</summary>
    Low,

    /// <summary>The ordinary case.</summary>
    Normal,

    /// <summary>Something is broken for somebody.</summary>
    High,

    /// <summary>Something is broken for everybody.</summary>
    Urgent,
}

/// <summary>How the case arrived.</summary>
/// <remarks>
/// Kept because it is the one field a desk manager reads before deciding where to put a person,
/// and it cannot be recovered later from anything else on the row.
/// </remarks>
public enum CaseOrigin
{
    /// <summary>An inbox.</summary>
    Email,

    /// <summary>A telephone.</summary>
    Phone,

    /// <summary>A form.</summary>
    Web,

    /// <summary>A conversation.</summary>
    Chat,
}

/// <summary>Where a case stands.</summary>
public enum CaseStatus
{
    /// <summary>Nobody has touched it.</summary>
    New,

    /// <summary>Somebody is on it.</summary>
    Working,

    /// <summary>Waiting on the customer.</summary>
    Waiting,

    /// <summary>Somebody senior has it.</summary>
    Escalated,

    /// <summary>Finished.</summary>
    Closed,
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>One day of the week the desk is open.</summary>
/// <param name="Day">Which day.</param>
/// <param name="Opens">When it opens, as <c>HH:mm</c>.</param>
/// <param name="Closes">When it closes, as <c>HH:mm</c>.</param>
public sealed record OpeningHoursOfDay(DayOfWeek Day, string Opens, string Closes);

/// <summary>Declares the week the desk is open.</summary>
/// <param name="Week">
/// The open days. A day left out is a day the clock does not run — which is how a weekend, or a
/// four-day week, is said without a flag for either.
/// </param>
public sealed record SetBusinessHours(IReadOnlyList<OpeningHoursOfDay> Week);

/// <summary>The week was declared.</summary>
/// <param name="Days">How many days the desk is open.</param>
/// <param name="MinutesPerWeek">
/// How many minutes that is. <strong>Said back deliberately</strong> — a week that was meant to be
/// forty hours and reads as four is a typo somebody will otherwise find in a breach report.
/// </param>
public sealed record BusinessHoursSet(int Days, int MinutesPerWeek);

/// <summary>Declares what the desk promises for a priority.</summary>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Priority">Which cases it governs.</param>
/// <param name="FirstResponseMinutes">How long until somebody must have replied.</param>
/// <param name="ResolutionMinutes">How long until it must be finished.</param>
/// <param name="BusinessHoursOnly">Whether the clock stops when the desk shuts.</param>
public sealed record DefineSlaPolicy(
    string Name,
    string Label,
    CasePriority Priority,
    int FirstResponseMinutes,
    int ResolutionMinutes,
    bool BusinessHoursOnly);

/// <summary>The promise was declared.</summary>
/// <param name="PolicyId">Its id.</param>
/// <param name="Replaced">
/// Whether it took over from one already governing the priority. A desk that silently ended up
/// with two promises for "urgent" would have neither.
/// </param>
public sealed record SlaPolicyDefined(Guid PolicyId, bool Replaced);

/// <summary>Raises a case.</summary>
/// <param name="AccountId">Whose.</param>
/// <param name="ContactId">Who asked, when it is somebody in particular.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="Description">What they said.</param>
/// <param name="Priority">How urgent.</param>
/// <param name="Origin">How it arrived.</param>
public sealed record OpenCase(
    Guid AccountId,
    Guid? ContactId,
    string Subject,
    string Description,
    CasePriority Priority,
    CaseOrigin Origin);

/// <summary>The case was raised.</summary>
/// <param name="CaseId">Its id.</param>
/// <param name="Number">What to say on the telephone.</param>
/// <param name="Policy">Which promise governs it, or null when the desk promises nothing here.</param>
/// <param name="FirstResponseDueAt">When somebody must have replied, or null.</param>
/// <param name="ResolutionDueAt">When it must be finished, or null.</param>
public sealed record CaseOpened(
    Guid CaseId,
    long Number,
    string? Policy,
    DateTimeOffset? FirstResponseDueAt,
    DateTimeOffset? ResolutionDueAt);

/// <summary>Says something on a case.</summary>
/// <param name="CaseId">Which case.</param>
/// <param name="Body">What to say.</param>
/// <param name="IsPublic">Whether the customer sees it.</param>
/// <param name="Status">Where the case now stands, when it moves.</param>
public sealed record CommentOnCase(Guid CaseId, string Body, bool IsPublic, CaseStatus? Status);

/// <summary>The comment was written.</summary>
/// <param name="CaseId">Which case.</param>
/// <param name="Ordinal">Where it sits in the thread.</param>
/// <param name="Status">Where the case now stands.</param>
/// <param name="StoppedTheResponseClock">
/// Whether this was the first reply the customer could see, and so the one the first-response
/// promise is measured against.
/// </param>
/// <param name="FirstResponseMinutes">
/// How many open minutes it took, counted only when the clock stopped here.
/// </param>
/// <param name="BreachedFirstResponse">Whether that was later than promised.</param>
public sealed record CaseCommented(
    Guid CaseId,
    int Ordinal,
    string Status,
    bool StoppedTheResponseClock,
    double? FirstResponseMinutes,
    bool BreachedFirstResponse);

/// <summary>What the service capabilities are given, once the flow has read the caller.</summary>
/// <param name="Hours">The week, when that is what was asked.</param>
/// <param name="Policy">The promise, when that is what was asked.</param>
/// <param name="Open">The case to raise, when that is what was asked.</param>
/// <param name="Comment">The comment, when that is what was asked.</param>
/// <param name="Queue">The queue to read, when that is what was asked.</param>
/// <param name="UserId">Who is asking, from their claims and never from the body.</param>
public sealed record ServiceBy(
    SetBusinessHours? Hours,
    DefineSlaPolicy? Policy,
    OpenCase? Open,
    CommentOnCase? Comment,
    ReadCaseWorklist? Queue,
    string UserId);

/// <summary>Asks for the live queue.</summary>
/// <param name="MineOnly">Only the caller's, which is what a console opens on.</param>
/// <param name="Priority">Only one priority, when a desk is working the urgent queue down.</param>
/// <param name="BreachedOnly">Only what is already late.</param>
public sealed record ReadCaseWorklist(bool MineOnly, CasePriority? Priority, bool BreachedOnly);

// ------------------------------------------------------------------------------- what comes back

/// <summary>One case in the queue, with where its promises stand.</summary>
/// <param name="CaseId">Its id.</param>
/// <param name="Number">What to say on the telephone.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Priority">How urgent.</param>
/// <param name="OwnerId">Whose it is.</param>
/// <param name="OpenedAt">When it arrived.</param>
/// <param name="FirstResponseDueAt">When somebody must have replied, or null.</param>
/// <param name="ResolutionDueAt">When it must be finished, or null.</param>
/// <param name="AwaitingFirstResponse">Whether nobody has replied yet.</param>
/// <param name="ResponseBreached">
/// Whether the reply is late — or, when one was written, whether it was.
/// </param>
/// <param name="ResolutionBreached">Whether the fix is late.</param>
/// <param name="MinutesToResolutionDue">
/// How long is left, negative when it has gone. <strong>Signed rather than clamped</strong>,
/// because "four hours late" and "due now" are the same number to a desk that clamps.
/// </param>
public sealed record QueuedCase(
    Guid CaseId,
    long Number,
    string Subject,
    string Status,
    string Priority,
    string OwnerId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? FirstResponseDueAt,
    DateTimeOffset? ResolutionDueAt,
    bool AwaitingFirstResponse,
    bool ResponseBreached,
    bool ResolutionBreached,
    double? MinutesToResolutionDue);

/// <summary>The live queue.</summary>
/// <param name="Cases">The cases, the tightest promise first.</param>
/// <param name="Breached">How many are already late on something.</param>
/// <param name="AwaitingFirstResponse">How many nobody has replied to.</param>
public sealed record CaseWorklist(
    IReadOnlyList<QueuedCase> Cases,
    int Breached,
    int AwaitingFirstResponse);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the service surface can produce.</summary>
public static class ServiceErrors
{
    /// <summary>The time is not one this build can read.</summary>
    /// <param name="value">What was given.</param>
    /// <returns>The refusal.</returns>
    public static Error TimeIsNotReadable(string value) =>
        new(
            "crm.service_time_unreadable",
            $"'{value}' is not a time of day. Write it as HH:mm, in twenty-four hours.",
            ErrorCategory.Validation);

    /// <summary>The desk shuts before it opens.</summary>
    /// <param name="day">Which day.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// A window that closes before it opens never elapses, so a minute budget walked through it
    /// would find no time on any day for ever. Refused rather than defended against.
    /// </remarks>
    public static Error DayClosesBeforeItOpens(DayOfWeek day) =>
        new(
            "crm.service_day_inverted",
            $"The desk cannot shut on {day} before it opens.",
            ErrorCategory.Validation);

    /// <summary>The same day was given twice.</summary>
    /// <param name="day">Which day.</param>
    /// <returns>The refusal.</returns>
    public static Error DayGivenTwice(DayOfWeek day) =>
        new(
            "crm.service_day_repeated",
            $"{day} was given twice. A day the desk opens and shuts twice is two windows, and " +
            "this build keeps one.",
            ErrorCategory.Validation);

    /// <summary>The promise is not a positive number of minutes.</summary>
    /// <param name="minutes">What was given.</param>
    /// <returns>The refusal.</returns>
    public static Error PromiseIsNotPositive(int minutes) =>
        new(
            "crm.service_promise_not_positive",
            $"'{minutes}' is not a number of minutes a desk can promise. A target of zero is met " +
            "by nothing and breached by everything.",
            ErrorCategory.Validation);

    /// <summary>The promise is inside out.</summary>
    /// <returns>The refusal.</returns>
    public static Error ResolutionSoonerThanResponse() =>
        new(
            "crm.service_resolution_before_response",
            "A promise to fix something sooner than to acknowledge it is not a stricter promise, " +
            "it is an unmeetable one.",
            ErrorCategory.Validation);

    /// <summary>The case is not one this tenant has.</summary>
    /// <param name="id">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error CaseNotFound(Guid id) =>
        new(
            "crm.service_case_not_found",
            $"'{id}' is not a case of this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The account is not one this tenant has.</summary>
    /// <param name="id">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error AccountNotFound(Guid id) =>
        new(
            "crm.service_account_not_found",
            $"'{id}' is not an account of this tenant.",
            ErrorCategory.NotFound);

    /// <summary>Somebody said something on a case that is finished.</summary>
    /// <param name="number">Which case.</param>
    /// <returns>The refusal.</returns>
    public static Error CaseIsClosed(long number) =>
        new(
            "crm.service_case_closed",
            $"Case {number} is closed. Reopen it before adding to the thread, so the promise it " +
            "is measured against is one somebody chose.",
            ErrorCategory.Conflict);

    /// <summary>Too many cases were raised at once for the number to settle.</summary>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Retryable, and said so rather than swallowed. A number allocated per tenant is settled by
    /// the unique constraint, and a caller that lost <c>NumberAttempts</c> times in a row is
    /// against a burst rather than a bug.
    /// </remarks>
    public static Error NumberContended() =>
        new(
            "crm.service_number_contended",
            "Too many cases were raised at once to allocate a number. Try again.",
            ErrorCategory.Conflict);

    /// <summary>The week the promise is measured against is empty.</summary>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Refused when the case is raised rather than defaulted to around the clock. A desk that
    /// configured no week and got a twenty-four-hour promise would breach everything by Tuesday
    /// and blame the report.
    /// </remarks>
    public static Error NoBusinessHours() =>
        new(
            "crm.service_no_business_hours",
            "This promise is measured in the hours the desk is open, and the desk has no hours. " +
            "Declare the week first, or make the policy run around the clock.",
            ErrorCategory.Validation);
}

/// <summary>What the service surface accepts.</summary>
public static class ServiceLimits
{
    /// <summary>The most cases one queue answers with.</summary>
    public const int MaxQueue = 200;

    /// <summary>How many times a case number is retried when two arrive at once.</summary>
    /// <remarks>
    /// The number is allocated per tenant rather than from a shared sequence, because a sequence
    /// every tenant draws from leaks how busy the others are. The unique constraint settles the
    /// race and this is how many times the loser tries again.
    /// </remarks>
    public const int NumberAttempts = 5;
}
