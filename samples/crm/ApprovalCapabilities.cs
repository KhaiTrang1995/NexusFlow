using FlowX;

namespace Crm;

/// <summary>
/// Declares an approval process an administrator can change without a deployment.
/// </summary>
/// <remarks>
/// <strong>This does not replace <c>crm.discount.approve</c>.</strong> That grant answers "may
/// this person approve a discount at all", which is authorisation and belongs on the capability.
/// This answers the orthogonal question a large organisation also asks: which particular people,
/// in what order, for this particular quote — which is configuration.
/// </remarks>
[Capability("crm.approval.process", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class DefineCrmApprovalProcess
    : ICapability<DefineApprovalProcess, ApprovalProcessDefined>
{
    private readonly ApprovalStore _approvals;

    /// <summary>Creates the capability.</summary>
    /// <param name="approvals">Writes the process.</param>
    /// <exception cref="ArgumentNullException"><paramref name="approvals"/> is null.</exception>
    public DefineCrmApprovalProcess(ApprovalStore approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        _approvals = approvals;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ApprovalProcessDefined>> ExecuteAsync(
        DefineApprovalProcess input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<ApprovalProcessDefined>(
                CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        // A process with no steps approves everything the moment it is submitted, which is a
        // slower way of having no process at all.
        if (input.Steps.Count == 0)
        {
            return Result.Fail<ApprovalProcessDefined>(ApprovalErrors.ProcessHasNoSteps());
        }

        if (input.Steps.Count > ApprovalLimits.MaxSteps
            || input.Criteria.Count > ApprovalLimits.MaxCriteria)
        {
            return Result.Fail<ApprovalProcessDefined>(
                QueryErrors.TooManyCriteria(input.Criteria.Count));
        }

        // Checked when the process is saved. A criterion naming an attribute the subject does not
        // have matches nothing for ever, and the symptom is a process that silently never applies
        // — which reads as "approvals are switched off" rather than as a typo.
        foreach (var criterion in input.Criteria)
        {
            if (!ApprovalAttributes.Of(input.Subject)
                    .Contains(criterion.Attribute, StringComparer.Ordinal))
            {
                return Result.Fail<ApprovalProcessDefined>(
                    ApprovalErrors.AttributeIsNotOfSubject(input.Subject, criterion.Attribute));
            }
        }

        var id = await _approvals
            .SaveProcessAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is { } saved
            ? Result.Ok(new ApprovalProcessDefined(saved, input.Steps.Count))
            : Result.Fail<ApprovalProcessDefined>(CustomSchemaErrors.NameIsTaken(input.Name));
    }
}

/// <summary>
/// Submits something for approval, if any process says it needs one.
/// </summary>
/// <remarks>
/// <strong>"No approval needed" is a real answer and is said out loud.</strong> Most quotes are
/// under every threshold anybody configured, so returning nothing would make the common case
/// indistinguishable from a failed call — and a client that could not tell them apart would either
/// block every quote or none.
/// </remarks>
[Capability("crm.approval.submit", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class SubmitCrmApproval : ICapability<ApprovalBy, ApprovalSubmitted>
{
    private readonly ApprovalStore _approvals;
    private readonly ApproverResolver _approvers;

    /// <summary>Creates the capability.</summary>
    /// <param name="approvals">Reads the processes and writes the request.</param>
    /// <param name="approvers">Works out who each step is waiting on.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SubmitCrmApproval(ApprovalStore approvals, ApproverResolver approvers)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(approvers);

        _approvals = approvals;
        _approvers = approvers;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ApprovalSubmitted>> ExecuteAsync(
        ApprovalBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Submit is not { } submit)
        {
            return Result.Fail<ApprovalSubmitted>(BulkErrors.AskForOneOrTheOther());
        }

        var governing = await _approvals
            .GoverningAsync(ctx.TenantId, submit.Subject, submit.Id, ct)
            .ConfigureAwait(false);

        if (governing is null)
        {
            // Either the thing does not exist or nothing governs it. Telling them apart needs a
            // second read, and the answer a caller acts on is the same: do not wait for anybody.
            return Result.Ok(new ApprovalSubmitted(null, null, false, 0, null));
        }

        // Every step's approver is resolved now, not when somebody tries to decide. A request
        // waiting on nobody sits in a queue for a fortnight before anybody works out why it never
        // moved, and by then the deal it was holding up has gone.
        foreach (var step in governing.Steps)
        {
            if (await _approvers
                    .ResolveAsync(ctx.TenantId, step, input.UserId, ct)
                    .ConfigureAwait(false) is not { Count: > 0 })
            {
                return Result.Fail<ApprovalSubmitted>(
                    ApprovalErrors.ApproverCannotBeResolved(
                        $"step {step.Ordinal} ('{step.Label}') resolves to nobody."));
            }
        }

        var id = ctx.NewId();

        if (!await _approvals
                .SubmitAsync(
                    ctx.TenantId, id, governing.ProcessId, submit.Subject, submit.Id,
                    input.UserId, ctx.UtcNow, ct)
                .ConfigureAwait(false))
        {
            return Result.Fail<ApprovalSubmitted>(ApprovalErrors.AlreadyPending(submit.Id));
        }

        var first = governing.Steps[0];

        return Result.Ok(new ApprovalSubmitted(id, governing.Name, true, first.Ordinal, first.Label));
    }
}

/// <summary>
/// Records what somebody said about a request.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A submitter cannot approve their own request.</strong> It is the whole point of the
/// mechanism and the first thing an auditor asks about — and it is almost never implemented,
/// because the happy path works perfectly without it and nothing fails until somebody notices they
/// can sign off their own discount.
/// </para>
/// <para>
/// <strong>A step cannot be decided twice, and a rejection ends the request.</strong> The first is
/// held by the register's primary key rather than by a read-then-write, so two clicks resolve to
/// one decision and one refusal. The second is what makes a rejection mean something: a later
/// approver being asked anyway would turn "no" into "not yet".
/// </para>
/// </remarks>
[Capability("crm.approval.decide", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class DecideCrmApproval : ICapability<ApprovalBy, ApprovalDecided>
{
    private readonly ApprovalStore _approvals;
    private readonly ApproverResolver _approvers;

    /// <summary>Creates the capability.</summary>
    /// <param name="approvals">Reads the request and writes the decision.</param>
    /// <param name="approvers">Works out whether the caller is who the step is waiting on.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DecideCrmApproval(ApprovalStore approvals, ApproverResolver approvers)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(approvers);

        _approvals = approvals;
        _approvers = approvers;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ApprovalDecided>> ExecuteAsync(
        ApprovalBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Decide is not { } decide)
        {
            return Result.Fail<ApprovalDecided>(BulkErrors.AskForOneOrTheOther());
        }

        if (await _approvals.RequestAsync(ctx.TenantId, decide.RequestId, ct).ConfigureAwait(false)
            is not { } request)
        {
            return Result.Fail<ApprovalDecided>(
                ApprovalErrors.RequestNotFound(decide.RequestId));
        }

        if (request.Status != "Pending")
        {
            return Result.Fail<ApprovalDecided>(ApprovalErrors.AlreadyDecided(request.Status));
        }

        // The control this whole mechanism exists for.
        if (string.Equals(request.SubmittedBy, input.UserId, StringComparison.Ordinal))
        {
            return Result.Fail<ApprovalDecided>(ApprovalErrors.CannotApproveOwnRequest());
        }

        var step = request.Steps.FirstOrDefault(row => row.Ordinal == request.CurrentStep);

        if (step is null)
        {
            return Result.Fail<ApprovalDecided>(
                ApprovalErrors.ApproverCannotBeResolved("the step it waits on no longer exists."));
        }

        var approvers = await _approvers
            .ResolveAsync(ctx.TenantId, step, request.SubmittedBy, ct)
            .ConfigureAwait(false);

        if (!approvers.Contains(input.UserId, StringComparer.Ordinal))
        {
            return Result.Fail<ApprovalDecided>(ApprovalErrors.NotTheApprover());
        }

        var rejected = decide.Decision == ApprovalDecision.Rejected;
        var last = request.CurrentStep + 1 >= request.Steps.Count;

        var next = rejected ? request.CurrentStep : Math.Min(request.CurrentStep + 1, request.Steps.Count);
        var status = rejected ? "Rejected" : last ? "Approved" : "Pending";

        if (!await _approvals
                .DecideAsync(
                    ctx.TenantId, request.RequestId, request.CurrentStep, input.UserId,
                    decide.Decision, decide.Note, next, status, ctx.UtcNow, ct)
                .ConfigureAwait(false))
        {
            return Result.Fail<ApprovalDecided>(ApprovalErrors.AlreadyDecided("decided"));
        }

        var awaiting = status == "Pending"
            ? request.Steps.FirstOrDefault(row => row.Ordinal == next)
            : null;

        return Result.Ok(new ApprovalDecided(
            request.RequestId, status, next, awaiting?.Label));
    }
}

/// <summary>
/// What is waiting on the caller.
/// </summary>
/// <remarks>
/// <strong>Filtered by who is asking, not by a query the client writes.</strong> An inbox that
/// took a user id in the body would be an inbox anybody could read as anybody — and the request
/// waiting in it is exactly the one somebody would like to see before they are asked about it.
/// </remarks>
[Capability("crm.approval.inbox", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmApprovalInbox : ICapability<ApprovalBy, ApprovalInbox>
{
    private readonly ApprovalStore _approvals;
    private readonly ApproverResolver _approvers;

    /// <summary>Creates the capability.</summary>
    /// <param name="approvals">Reads what is pending.</param>
    /// <param name="approvers">Works out which of them are the caller's.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReadCrmApprovalInbox(ApprovalStore approvals, ApproverResolver approvers)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(approvers);

        _approvals = approvals;
        _approvers = approvers;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ApprovalInbox>> ExecuteAsync(
        ApprovalBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var pending = await _approvals.PendingAsync(ctx.TenantId, ct).ConfigureAwait(false);
        var waiting = new List<WaitingApproval>();

        foreach (var request in pending)
        {
            // A submitter never sees their own request in their inbox, because they could not act
            // on it anyway — and an inbox full of things you are forbidden to decide is an inbox
            // people stop opening.
            if (string.Equals(request.SubmittedBy, input.UserId, StringComparison.Ordinal))
            {
                continue;
            }

            var approvers = await _approvers
                .ResolveAsync(
                    ctx.TenantId,
                    new StoredStep(request.Step, request.StepLabel, request.Kind, request.Approver),
                    request.SubmittedBy,
                    ct)
                .ConfigureAwait(false);

            if (approvers.Contains(input.UserId, StringComparer.Ordinal))
            {
                waiting.Add(new WaitingApproval(
                    request.RequestId, request.Process, request.Subject, request.SubjectId,
                    request.SubmittedBy, request.SubmittedAt, request.Step, request.StepLabel));
            }
        }

        return Result.Ok(new ApprovalInbox(waiting));
    }
}
