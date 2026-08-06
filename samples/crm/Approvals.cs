using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- the vocabulary

/// <summary>What can be submitted for approval.</summary>
/// <remarks>
/// Closed, because each subject's attributes are a fixed list a criterion is checked against when
/// the process is saved. A criterion naming an attribute the subject does not have matches nothing
/// for ever, and the symptom is a process that silently never applies.
/// </remarks>
public enum ApprovalSubject
{
    /// <summary>A quote, usually for its discount.</summary>
    Quote,

    /// <summary>A deal, usually for its size.</summary>
    Opportunity,

    /// <summary>A commitment, usually for its number.</summary>
    Plan,
}

/// <summary>How the person who has to say yes is found.</summary>
public enum ApproverKind
{
    /// <summary>One named person.</summary>
    /// <remarks>
    /// The simplest and the most brittle: a step that names somebody breaks when they leave, and
    /// the request it breaks is one already waiting.
    /// </remarks>
    Named,

    /// <summary>
    /// Whoever the submitter reports to, read from the organisation's line.
    /// </summary>
    /// <remarks>
    /// <strong>What makes one process work for a whole company.</strong> The reporting line already
    /// exists, so this keeps working through a reorganisation that a named step would not survive.
    /// </remarks>
    SubmittersManager,

    /// <summary>Anybody the tenant has placed in a given role.</summary>
    RoleHolder,
}

/// <summary>What somebody said.</summary>
public enum ApprovalDecision
{
    /// <summary>Yes. The next step is asked, or the request completes.</summary>
    Approved,

    /// <summary>No. The request ends here and no later step is asked.</summary>
    Rejected,
}

/// <summary>Which attributes an approval criterion may be written against.</summary>
public static class ApprovalAttributes
{
    /// <summary>What a subject may be tested on.</summary>
    /// <param name="subject">Which subject.</param>
    /// <returns>The attribute names.</returns>
    public static IReadOnlyList<string> Of(ApprovalSubject subject) => subject switch
    {
        ApprovalSubject.Quote => ["discount", "subtotal", "total", "status"],
        ApprovalSubject.Opportunity => ["amount", "probability", "currency"],
        _ => ["target_amount", "kind"],
    };
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>One thing that must hold for a process to apply.</summary>
/// <param name="Attribute">Which attribute of the subject.</param>
/// <param name="Operator">The same five operators as everywhere else in this sample.</param>
/// <param name="Value">What to compare against.</param>
public sealed record ApprovalCriterion(string Attribute, GuardOperator Operator, string Value);

/// <summary>One person who has to say yes, and how they are found.</summary>
/// <param name="Label">What to show on the request.</param>
/// <param name="Kind">How to find them.</param>
/// <param name="Approver">A subject, a role, or null for the submitter's manager.</param>
public sealed record ApprovalStepDefinition(string Label, ApproverKind Kind, string? Approver);

/// <summary>Declares an approval process.</summary>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Subject">What it governs.</param>
/// <param name="Priority">Lower runs first; the first matching process governs the request.</param>
/// <param name="Criteria">What must hold for it to apply. All of them.</param>
/// <param name="Steps">Who has to say yes, in the order they are asked.</param>
public sealed record DefineApprovalProcess(
    string Name,
    string Label,
    ApprovalSubject Subject,
    int Priority,
    IReadOnlyList<ApprovalCriterion> Criteria,
    IReadOnlyList<ApprovalStepDefinition> Steps);

/// <summary>The process was declared.</summary>
/// <param name="ProcessId">Its id.</param>
/// <param name="Steps">How many approvals it asks for.</param>
public sealed record ApprovalProcessDefined(Guid ProcessId, int Steps);

/// <summary>Submits something for approval.</summary>
/// <param name="Subject">What sort of thing.</param>
/// <param name="Id">Which one.</param>
public sealed record SubmitForApproval(ApprovalSubject Subject, Guid Id);

/// <summary>What the approval capabilities are given, once the flow has read the caller.</summary>
/// <param name="Submit">The submission, when that is what was asked.</param>
/// <param name="Decide">The decision, when that is what was asked.</param>
/// <param name="UserId">Who is asking, from their claims and never from the body.</param>
public sealed record ApprovalBy(SubmitForApproval? Submit, DecideApproval? Decide, string UserId);

/// <summary>What came of submitting it.</summary>
/// <param name="RequestId">The request, or null when no process applied.</param>
/// <param name="Process">Which process governs it, or null.</param>
/// <param name="Required">
/// Whether approval is needed at all. <strong>False is a real and common answer</strong> — most
/// quotes are under every threshold — and it is said explicitly rather than by returning nothing,
/// so a client can tell "no approval needed" from "the call failed".
/// </param>
/// <param name="AwaitingStep">Which step is being waited on.</param>
/// <param name="AwaitingLabel">What that step is called.</param>
public sealed record ApprovalSubmitted(
    Guid? RequestId,
    string? Process,
    bool Required,
    int AwaitingStep,
    string? AwaitingLabel);

/// <summary>Records what somebody said about a request.</summary>
/// <param name="RequestId">Which request.</param>
/// <param name="Decision">Yes or no.</param>
/// <param name="Note">Why. Kept on the register, because that is what an audit reads.</param>
public sealed record DecideApproval(Guid RequestId, ApprovalDecision Decision, string Note);

/// <summary>What came of the decision.</summary>
/// <param name="RequestId">Which request.</param>
/// <param name="Status">Where the request now stands.</param>
/// <param name="AwaitingStep">Which step is next, or the count when it is finished.</param>
/// <param name="AwaitingLabel">What that step is called, or null when nothing is waiting.</param>
public sealed record ApprovalDecided(
    Guid RequestId,
    string Status,
    int AwaitingStep,
    string? AwaitingLabel);

/// <summary>Asks what is waiting on the caller.</summary>
public sealed record ReadApprovalInbox;

// ------------------------------------------------------------------------------- what comes back

/// <summary>One request waiting on somebody.</summary>
/// <param name="RequestId">Which request.</param>
/// <param name="Process">Which process governs it.</param>
/// <param name="Subject">What sort of thing is being approved.</param>
/// <param name="SubjectId">Which one.</param>
/// <param name="SubmittedBy">Who asked.</param>
/// <param name="SubmittedAt">When.</param>
/// <param name="Step">Which step is being waited on.</param>
/// <param name="StepLabel">What that step is called.</param>
public sealed record WaitingApproval(
    Guid RequestId,
    string Process,
    string Subject,
    Guid SubjectId,
    string SubmittedBy,
    DateTimeOffset SubmittedAt,
    int Step,
    string StepLabel);

/// <summary>What is waiting on the caller.</summary>
/// <param name="Waiting">The requests, oldest first — which is the order a queue is worked.</param>
public sealed record ApprovalInbox(IReadOnlyList<WaitingApproval> Waiting);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the approval surface can produce.</summary>
public static class ApprovalErrors
{
    /// <summary>The attribute is not one the subject has.</summary>
    /// <param name="subject">Which subject.</param>
    /// <param name="attribute">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error AttributeIsNotOfSubject(ApprovalSubject subject, string attribute) =>
        new(
            "crm.approval_attribute_unknown",
            $"'{attribute}' is not an attribute of {subject} a criterion can test. It has: " +
            string.Join(", ", ApprovalAttributes.Of(subject)) + ".",
            ErrorCategory.Validation);

    /// <summary>The process asked for no approvals.</summary>
    public static Error ProcessHasNoSteps() =>
        new(
            "crm.approval_process_without_steps",
            "An approval process with no steps approves everything the moment it is submitted, " +
            "which is a slower way of having no process.",
            ErrorCategory.Validation);

    /// <summary>The thing being submitted is not one this tenant has.</summary>
    /// <param name="id">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error SubjectNotFound(Guid id) =>
        new(
            "crm.approval_subject_not_found",
            $"'{id}' is not a thing of this tenant that can be submitted.",
            ErrorCategory.NotFound);

    /// <summary>Something is already waiting on this.</summary>
    /// <param name="id">Which thing.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Two pending requests for one quote is a quote whose fate depends on which one somebody
    /// happens to open.
    /// </remarks>
    public static Error AlreadyPending(Guid id) =>
        new(
            "crm.approval_already_pending",
            $"'{id}' already has an approval waiting.",
            ErrorCategory.Conflict);

    /// <summary>The request is not one this tenant has.</summary>
    /// <param name="id">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error RequestNotFound(Guid id) =>
        new(
            "crm.approval_request_not_found",
            $"'{id}' is not an approval request of this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The request has already been decided.</summary>
    /// <param name="status">What it was decided as.</param>
    /// <returns>The refusal.</returns>
    public static Error AlreadyDecided(string status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new Error(
            "crm.approval_already_decided",
            $"This request was already {status.ToLowerInvariant()}.",
            ErrorCategory.Conflict);
    }

    /// <summary>The caller is not who this step is waiting on.</summary>
    /// <returns>The refusal.</returns>
    public static Error NotTheApprover() =>
        new(
            "crm.approval_not_the_approver",
            "This step is waiting on somebody else.",
            ErrorCategory.Forbidden);

    /// <summary>The caller submitted the thing they are trying to approve.</summary>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// <strong>The control the whole mechanism exists for.</strong> It is the first thing an
    /// auditor asks about and the last thing a home-grown approval system implements, because the
    /// happy path works perfectly without it and nothing fails until somebody notices they can
    /// approve their own discount.
    /// </remarks>
    public static Error CannotApproveOwnRequest() =>
        new(
            "crm.approval_self",
            "The person who submitted a request cannot be the one who approves it.",
            ErrorCategory.Forbidden);

    /// <summary>The step's approver cannot be resolved.</summary>
    /// <param name="reason">What is missing.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Refused when the request is submitted rather than when somebody tries to decide it. A
    /// request waiting on nobody is one that sits in a queue for a fortnight before anybody works
    /// out why it never moved.
    /// </remarks>
    public static Error ApproverCannotBeResolved(string reason) =>
        new(
            "crm.approval_approver_unresolved",
            $"This process cannot say who should approve the request: {reason}",
            ErrorCategory.Validation);
}

/// <summary>What the approval surface accepts.</summary>
public static class ApprovalLimits
{
    /// <summary>The most criteria one process carries.</summary>
    public const int MaxCriteria = 10;

    /// <summary>The most steps one process asks for.</summary>
    /// <remarks>
    /// Bounded because each step is a person who has to be found and asked. A process with twenty
    /// is not a control, it is a queue with a name.
    /// </remarks>
    public const int MaxSteps = 10;
}
