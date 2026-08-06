using System.Globalization;
using FlowX;

namespace Crm;

/// <summary>
/// Declares the week the desk is open.
/// </summary>
/// <remarks>
/// <strong>The week is a configuration, not a constant.</strong> Every promise this build makes is
/// measured in the hours the desk is open, and a desk that works Sunday to Thursday is not an edge
/// case — it is most of one hemisphere.
/// </remarks>
[Capability("crm.service.hours", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class SetCrmBusinessHours : ICapability<ServiceBy, BusinessHoursSet>
{
    private readonly ServiceStore _service;

    /// <summary>Creates the capability.</summary>
    /// <param name="service">Writes the week.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public SetCrmBusinessHours(ServiceStore service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _service = service;
    }

    /// <inheritdoc />
    public async ValueTask<Result<BusinessHoursSet>> ExecuteAsync(
        ServiceBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Hours is not { } hours)
        {
            return Result.Fail<BusinessHoursSet>(BulkErrors.AskForOneOrTheOther());
        }

        var week = new List<OpeningHours>();

        foreach (var day in hours.Week)
        {
            if (!TimeOnly.TryParseExact(day.Opens, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var opens))
            {
                return Result.Fail<BusinessHoursSet>(ServiceErrors.TimeIsNotReadable(day.Opens));
            }

            if (!TimeOnly.TryParseExact(day.Closes, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var closes))
            {
                return Result.Fail<BusinessHoursSet>(ServiceErrors.TimeIsNotReadable(day.Closes));
            }

            if (closes <= opens)
            {
                return Result.Fail<BusinessHoursSet>(
                    ServiceErrors.DayClosesBeforeItOpens(day.Day));
            }

            // Refused rather than last-one-wins. A week where Tuesday appears twice is a week the
            // caller does not think they wrote, and silently keeping one of them makes the breach
            // report the place they find out.
            if (week.Exists(written => written.Day == day.Day))
            {
                return Result.Fail<BusinessHoursSet>(ServiceErrors.DayGivenTwice(day.Day));
            }

            week.Add(new OpeningHours(day.Day, opens, closes));
        }

        await _service.SaveWeekAsync(ctx.TenantId, week, ct).ConfigureAwait(false);

        var minutes = week.Sum(day => (day.Closes - day.Opens).TotalMinutes);

        return Result.Ok(new BusinessHoursSet(week.Count, (int)minutes));
    }
}

/// <summary>
/// Declares what the desk promises for a priority.
/// </summary>
/// <remarks>
/// <strong>One live promise per priority.</strong> Declaring a second retires the first in the
/// same call, because a desk that ended up with two promises for "urgent" would in practice have
/// neither — the one a report showed would depend on which row a query read first.
/// </remarks>
[Capability("crm.service.sla", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class DefineCrmSlaPolicy : ICapability<ServiceBy, SlaPolicyDefined>
{
    private readonly ServiceStore _service;

    /// <summary>Creates the capability.</summary>
    /// <param name="service">Writes the promise.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public DefineCrmSlaPolicy(ServiceStore service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _service = service;
    }

    /// <inheritdoc />
    public async ValueTask<Result<SlaPolicyDefined>> ExecuteAsync(
        ServiceBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Policy is not { } policy)
        {
            return Result.Fail<SlaPolicyDefined>(BulkErrors.AskForOneOrTheOther());
        }

        if (!CustomValues.IsUsableName(policy.Name))
        {
            return Result.Fail<SlaPolicyDefined>(CustomSchemaErrors.NameIsNotUsable(policy.Name));
        }

        if (policy.FirstResponseMinutes <= 0 || policy.ResolutionMinutes <= 0)
        {
            return Result.Fail<SlaPolicyDefined>(
                ServiceErrors.PromiseIsNotPositive(
                    Math.Min(policy.FirstResponseMinutes, policy.ResolutionMinutes)));
        }

        // A promise to fix something sooner than to acknowledge it is not a stricter promise.
        if (policy.ResolutionMinutes < policy.FirstResponseMinutes)
        {
            return Result.Fail<SlaPolicyDefined>(ServiceErrors.ResolutionSoonerThanResponse());
        }

        return await _service.SavePolicyAsync(ctx.TenantId, ctx.NewId(), policy, ct)
                .ConfigureAwait(false) is { } saved
            ? Result.Ok(saved)
            : Result.Fail<SlaPolicyDefined>(CustomSchemaErrors.NameIsTaken(policy.Name));
    }
}

/// <summary>
/// Raises a case and stamps the promise that governs it.
/// </summary>
/// <remarks>
/// <strong>The promise is stamped once, at the moment it is made.</strong> An administrator
/// lowering a response target on a Tuesday must not retroactively breach every case raised on
/// Monday, which is exactly what a due date recomputed from the live policy would do.
/// </remarks>
[Capability("crm.service.open", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class OpenCrmCase : ICapability<ServiceBy, CaseOpened>
{
    private readonly ServiceStore _service;

    /// <summary>Creates the capability.</summary>
    /// <param name="service">Reads the promise and writes the case.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public OpenCrmCase(ServiceStore service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _service = service;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CaseOpened>> ExecuteAsync(
        ServiceBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Open is not { } open)
        {
            return Result.Fail<CaseOpened>(BulkErrors.AskForOneOrTheOther());
        }

        if (string.IsNullOrWhiteSpace(open.Subject) || open.Subject.Length > 200)
        {
            return Result.Fail<CaseOpened>(CustomSchemaErrors.NameIsNotUsable(open.Subject));
        }

        return await _service
            .OpenCaseAsync(ctx.TenantId, ctx.NewId(), open, input.UserId, ctx.UtcNow, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Says something on a case, and stops the response clock when that is what it is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only a public reply from somebody other than the person who raised the case stops the
/// clock.</strong> An internal note does not, or every desk would report a first response time of
/// nine seconds. Nor does the raiser adding to their own description, which is the other way the
/// number is quietly made meaningless.
/// </para>
/// <para>
/// <strong>The elapsed time is measured in the same open hours the promise was.</strong> Comparing
/// a business-hours promise against a calendar-hours reply is how a desk ends up reporting a
/// breach for a case answered first thing on Monday.
/// </para>
/// </remarks>
[Capability("crm.service.comment", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class CommentOnCrmCase : ICapability<ServiceBy, CaseCommented>
{
    private readonly ServiceStore _service;

    /// <summary>Creates the capability.</summary>
    /// <param name="service">Reads the case and writes the comment.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public CommentOnCrmCase(ServiceStore service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _service = service;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CaseCommented>> ExecuteAsync(
        ServiceBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Comment is not { } comment)
        {
            return Result.Fail<CaseCommented>(BulkErrors.AskForOneOrTheOther());
        }

        if (await _service.CaseAsync(ctx.TenantId, comment.CaseId, ct).ConfigureAwait(false)
            is not { } found)
        {
            return Result.Fail<CaseCommented>(ServiceErrors.CaseNotFound(comment.CaseId));
        }

        if (found.Status == "Closed")
        {
            return Result.Fail<CaseCommented>(ServiceErrors.CaseIsClosed(found.Number));
        }

        var stopsTheClock =
            comment.IsPublic
            && found.FirstRespondedAt is null
            && !string.Equals(found.OpenedBy, input.UserId, StringComparison.Ordinal);

        var (ordinal, stopped) = await _service
            .CommentAsync(ctx.TenantId, comment.CaseId, input.UserId, comment, stopsTheClock,
                ctx.UtcNow, ct)
            .ConfigureAwait(false);

        double? minutes = null;
        var breached = false;

        if (stopped)
        {
            var week = await _service.WeekAsync(ctx.TenantId, ct).ConfigureAwait(false);

            minutes = week.Count > 0
                ? BusinessCalendar.Elapsed(found.OpenedAt, ctx.UtcNow, week)
                : (ctx.UtcNow - found.OpenedAt).TotalMinutes;

            breached = found.FirstResponseDueAt is { } due && ctx.UtcNow > due;
        }

        var status = comment.Status?.ToString() ?? found.Status;

        return Result.Ok(new CaseCommented(
            comment.CaseId, ordinal, status, stopped, minutes, breached));
    }
}

/// <summary>
/// The live queue, with where each case's promises stand as of now.
/// </summary>
/// <remarks>
/// <strong>Breach is computed as of the read, not stored.</strong> A case does not become late by
/// anybody doing anything to it — it becomes late by the clock passing a stored instant, and a
/// flag that had to be swept up by a job would be wrong for however long the job's period is.
/// </remarks>
[Capability("crm.service.queue", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmCaseWorklist : ICapability<ServiceBy, CaseWorklist>
{
    private readonly ServiceStore _service;

    /// <summary>Creates the capability.</summary>
    /// <param name="service">Reads the queue.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public ReadCrmCaseWorklist(ServiceStore service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _service = service;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CaseWorklist>> ExecuteAsync(
        ServiceBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Queue is not { } ask)
        {
            return Result.Fail<CaseWorklist>(BulkErrors.AskForOneOrTheOther());
        }

        var stored = await _service.QueueAsync(ctx.TenantId, ask, input.UserId, ct)
            .ConfigureAwait(false);

        var cases = new List<QueuedCase>();

        foreach (var row in stored)
        {
            var awaiting = row.FirstRespondedAt is null;

            // A case with no reply is late when the promise has passed; one with a reply is late
            // when the reply was after it. Two questions, because a desk that answered late and a
            // desk that has not answered at all are different problems with different remedies.
            var responseBreached = row.FirstResponseDueAt is { } responseDue
                && (row.FirstRespondedAt ?? ctx.UtcNow) > responseDue;

            var resolutionBreached = row.ResolutionDueAt is { } resolutionDue
                && ctx.UtcNow > resolutionDue;

            if (ask.BreachedOnly && !responseBreached && !resolutionBreached)
            {
                continue;
            }

            cases.Add(new QueuedCase(
                row.CaseId,
                row.Number,
                row.Subject,
                row.Status,
                row.Priority,
                row.OwnerId,
                row.OpenedAt,
                row.FirstResponseDueAt,
                row.ResolutionDueAt,
                awaiting,
                responseBreached,
                resolutionBreached,
                row.ResolutionDueAt is { } left ? (left - ctx.UtcNow).TotalMinutes : null));
        }

        return Result.Ok(new CaseWorklist(
            cases,
            cases.Count(row => row.ResponseBreached || row.ResolutionBreached),
            cases.Count(row => row.AwaitingFirstResponse)));
    }
}
