using FlowX;

namespace Workflow;

/// <summary>Errors this application can produce.</summary>
/// <remarks>
/// Declared in one place so the codes are greppable and so two capabilities cannot invent
/// two spellings of the same condition. Each one reaches the manifest, the generated OpenAPI
/// responses and the RFC 7807 <c>type</c> URI.
/// </remarks>
public static class OnboardingErrors
{
    /// <summary>The offer names nobody.</summary>
    public static Error MissingCandidate() =>
        new("onboarding.missing_candidate", "The offer names no candidate.", ErrorCategory.Validation);

    /// <summary>
    /// The engagement type has no arm. The value the flow's <c>Default</c> block fails with.
    /// </summary>
    /// <remarks>
    /// A <c>static readonly</c> field rather than a factory, because <c>.Fail(error)</c> takes
    /// a value and the generator lifts the expression into a <c>static readonly Error</c> on
    /// the flow's partial class. Rejecting a request therefore allocates nothing at the moment
    /// the flow is already about to unwind.
    /// </remarks>
    public static readonly Error UnsupportedEmployment = new(
        "onboarding.unsupported_employment",
        "This flow onboards permanent employees and contractors only.",
        ErrorCategory.Validation);

    /// <summary>The directory would not create the account.</summary>
    public static Error DirectoryUnavailable() =>
        new("identity.directory_unavailable", "The directory service did not answer.", ErrorCategory.Unavailable);

    /// <summary>There is no desk left at the site.</summary>
    public static Error NoDeskAvailable(string site) =>
        new Error("workspace.no_desk", $"No desk is free at '{site}'.", ErrorCategory.Conflict)
            .With("site", site);

    /// <summary>Security would not print a pass.</summary>
    public static Error PassRefused(string reason) =>
        new Error("workspace.pass_refused", $"The building pass was refused: {reason}.", ErrorCategory.Conflict)
            .With("reason", reason);
}

// ---------------------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------------------

/// <summary>Checks the offer is well formed.</summary>
/// <remarks>
/// A read. No side effects, safe to retry, and — because the class references no transport
/// type and calls no other capability — testable by constructing it and calling the method.
/// </remarks>
[Capability("offer.validate", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class ValidateOffer : ICapability<OnboardEmployee, ValidatedOffer>
{
    /// <inheritdoc />
    public ValueTask<Result<ValidatedOffer>> ExecuteAsync(
        OnboardEmployee input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.CandidateId))
        {
            return ValueTask.FromResult(Result.Fail<ValidatedOffer>(OnboardingErrors.MissingCandidate()));
        }

        return ValueTask.FromResult(Result.Ok(new ValidatedOffer(
            input.CandidateId,
            input.Employment,
            input.Site,
            input.Equipment,
            input.RequiresBackgroundCheck)));
    }
}

// ---------------------------------------------------------------------------------------
// The switch's two arms, each with its own inverse
// ---------------------------------------------------------------------------------------

/// <summary>Opens a payroll record. The <c>Permanent</c> arm.</summary>
[Capability("payroll.open", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "payroll.write",
    Idempotent = true,
    SideEffects = ["payroll-ledger"])]
public sealed class OpenPayrollRecord : ICapability<ValidatedOffer, PayrollRecord>
{
    private readonly IPeopleDirectory _people;

    /// <summary>Creates the capability.</summary>
    public OpenPayrollRecord(IPeopleDirectory people)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PayrollRecord>> ExecuteAsync(
        ValidatedOffer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var id = await _people.OpenPayrollAsync(input.CandidateId, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new PayrollRecord(input.CandidateId, id);
    }
}

/// <summary>Closes a payroll record. The business inverse of <see cref="OpenPayrollRecord"/>.</summary>
[Capability("payroll.close", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["payroll-ledger"])]
public sealed class ClosePayrollRecord : ICapability<ValidatedOffer, PayrollRecord>
{
    private readonly IPeopleDirectory _people;

    /// <summary>Creates the capability.</summary>
    public ClosePayrollRecord(IPeopleDirectory people)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PayrollRecord>> ExecuteAsync(
        ValidatedOffer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _people.ClosePayrollAsync(ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new PayrollRecord(input.CandidateId, ctx.IdempotencyKey);
    }
}

/// <summary>Signs a supplier agreement. The <c>Contractor</c> arm.</summary>
[Capability("supplier.sign", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "supplier.write",
    Idempotent = true,
    SideEffects = ["supplier-register"])]
public sealed class SignSupplierAgreement : ICapability<ValidatedOffer, SupplierAgreement>
{
    private readonly IPeopleDirectory _people;

    /// <summary>Creates the capability.</summary>
    public SignSupplierAgreement(IPeopleDirectory people)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people;
    }

    /// <inheritdoc />
    public async ValueTask<Result<SupplierAgreement>> ExecuteAsync(
        ValidatedOffer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var id = await _people.SignAgreementAsync(input.CandidateId, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new SupplierAgreement(input.CandidateId, id);
    }
}

/// <summary>Voids a supplier agreement. The inverse of <see cref="SignSupplierAgreement"/>.</summary>
[Capability("supplier.void", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["supplier-register"])]
public sealed class VoidSupplierAgreement : ICapability<ValidatedOffer, SupplierAgreement>
{
    private readonly IPeopleDirectory _people;

    /// <summary>Creates the capability.</summary>
    public VoidSupplierAgreement(IPeopleDirectory people)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people;
    }

    /// <inheritdoc />
    public async ValueTask<Result<SupplierAgreement>> ExecuteAsync(
        ValidatedOffer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _people.VoidAgreementAsync(ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new SupplierAgreement(input.CandidateId, ctx.IdempotencyKey);
    }
}

// ---------------------------------------------------------------------------------------
// Identity — the step that carries a policy set
// ---------------------------------------------------------------------------------------

/// <summary>Creates the directory account.</summary>
/// <remarks>
/// Declares <c>Idempotent = true</c>, which is what makes the retry in
/// <see cref="Policies.DirectoryService"/> legal. Declaring it <c>false</c> and attaching the
/// same set is <c>FLOWX1014</c> at build time.
/// </remarks>
[Capability("identity.create", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "identity.write",
    Idempotent = true,
    SideEffects = ["directory"])]
public sealed class CreateIdentity : ICapability<ValidatedOffer, Identity>
{
    private readonly IPeopleDirectory _people;

    /// <summary>Creates the capability.</summary>
    public CreateIdentity(IPeopleDirectory people)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Identity>> ExecuteAsync(
        ValidatedOffer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var upn = await _people
            .CreateAccountAsync(input.CandidateId, ctx.IdempotencyKey, ct)
            .ConfigureAwait(false);

        return upn is null
            ? OnboardingErrors.DirectoryUnavailable()
            : new Identity(input.CandidateId, upn);
    }
}

/// <summary>Disables the directory account. The inverse of <see cref="CreateIdentity"/>.</summary>
[Capability("identity.disable", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["directory"])]
public sealed class DisableIdentity : ICapability<ValidatedOffer, Identity>
{
    private readonly IPeopleDirectory _people;

    /// <summary>Creates the capability.</summary>
    public DisableIdentity(IPeopleDirectory people)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Identity>> ExecuteAsync(
        ValidatedOffer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _people.DisableAccountAsync(ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new Identity(input.CandidateId, ctx.IdempotencyKey);
    }
}

// ---------------------------------------------------------------------------------------
// The fork's three branches. Two carry their own inverse.
// ---------------------------------------------------------------------------------------

/// <summary>Orders a laptop.</summary>
[Capability("hardware.order", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["asset-register"])]
public sealed class OrderLaptop : ICapability<Identity, LaptopOrder>
{
    private readonly IAssetRegistry _assets;

    /// <summary>Creates the capability.</summary>
    public OrderLaptop(IAssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LaptopOrder>> ExecuteAsync(
        Identity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var id = await _assets
            .OrderAsync("laptop", input.EmployeeId, ctx.IdempotencyKey, ct)
            .ConfigureAwait(false);

        return new LaptopOrder(id);
    }
}

/// <summary>Cancels the laptop order. This branch's own inverse.</summary>
[Capability("hardware.cancel", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["asset-register"])]
public sealed class CancelLaptopOrder : ICapability<Identity, LaptopOrder>
{
    private readonly IAssetRegistry _assets;

    /// <summary>Creates the capability.</summary>
    public CancelLaptopOrder(IAssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LaptopOrder>> ExecuteAsync(
        Identity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _assets.CancelAsync("laptop", ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new LaptopOrder(ctx.IdempotencyKey);
    }
}

/// <summary>Grants access to the systems the role needs.</summary>
[Capability("access.grant", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "access.write",
    Idempotent = true,
    SideEffects = ["access-control"])]
public sealed class GrantSystemAccess : ICapability<Identity, AccessGrant>
{
    private readonly IAccessControl _access;

    /// <summary>Creates the capability.</summary>
    public GrantSystemAccess(IAccessControl access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AccessGrant>> ExecuteAsync(
        Identity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var id = await _access.GrantAsync(input.Upn, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new AccessGrant(id);
    }
}

/// <summary>Revokes system access. This branch's own inverse.</summary>
[Capability("access.revoke", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["access-control"])]
public sealed class RevokeSystemAccess : ICapability<Identity, AccessGrant>
{
    private readonly IAccessControl _access;

    /// <summary>Creates the capability.</summary>
    public RevokeSystemAccess(IAccessControl access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AccessGrant>> ExecuteAsync(
        Identity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _access.RevokeAsync(ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new AccessGrant(ctx.IdempotencyKey);
    }
}

/// <summary>Books a seat at the next induction. The branch with no inverse — a booking is not an effect worth undoing.</summary>
[Capability("induction.book", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["calendar"])]
public sealed class ScheduleInduction : ICapability<Identity, InductionBooking>
{
    private readonly IAccessControl _access;

    /// <summary>Creates the capability.</summary>
    public ScheduleInduction(IAccessControl access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;
    }

    /// <inheritdoc />
    public async ValueTask<Result<InductionBooking>> ExecuteAsync(
        Identity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var id = await _access.BookInductionAsync(input.EmployeeId, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new InductionBooking(id);
    }
}

// ---------------------------------------------------------------------------------------
// The loop body: a conditional whose two arms meet, then a compensable step
// ---------------------------------------------------------------------------------------

/// <summary>Records a human's approval for an item that needs one. The loop's <c>then</c> arm.</summary>
/// <remarks>
/// <strong>This is not a wait.</strong> It records a decision that has already been made and
/// arrived with the request. A step that suspended until a human answered would need
/// <c>AwaitSignal</c>, which is why the README says what this sample does not do.
/// </remarks>
[Capability("equipment.approve", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "equipment.approve",
    Idempotent = true,
    SideEffects = ["approval-log"])]
public sealed class RecordEquipmentApproval : ICapability<EquipmentRequest, ApprovedEquipment>
{
    private readonly IAssetRegistry _assets;

    /// <summary>Creates the capability.</summary>
    public RecordEquipmentApproval(IAssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ApprovedEquipment>> ExecuteAsync(
        EquipmentRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _assets.RecordApprovalAsync(input.Item, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new ApprovedEquipment(input.Item, "line-manager");
    }
}

/// <summary>Clears an item that needs no approval. The loop's <c>Otherwise</c> arm.</summary>
/// <remarks>
/// Produces the same contract as <see cref="RecordEquipmentApproval"/>, which is what lets
/// the step after the conditional bind by type without knowing which arm ran.
/// </remarks>
[Capability("equipment.auto_clear", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class AutoClearEquipment : ICapability<EquipmentRequest, ApprovedEquipment>
{
    /// <inheritdoc />
    public ValueTask<Result<ApprovedEquipment>> ExecuteAsync(
        EquipmentRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        return ValueTask.FromResult(Result.Ok(new ApprovedEquipment(input.Item, "policy")));
    }
}

/// <summary>Hands the item over. Compensable, per element.</summary>
/// <remarks>
/// <para>
/// <strong>It binds <see cref="EquipmentRequest"/> — the loop's element — rather than the
/// <see cref="ApprovedEquipment"/> the arm above it produced, and that is forced.</strong>
/// A compensation is invoked with <em>the input of the step it undoes</em>: the generated
/// <c>CompensateAsync</c> emits the same input expression the forward case does, so
/// <see cref="ReturnEquipment"/> cannot bind a different contract from this one. Binding
/// <c>ApprovedEquipment</c> here would therefore make the undo bind it too, and the undo runs
/// after the loop has finished, when the shared bag holds only the last element's copy.
/// </para>
/// <para>
/// So the rule this sample had to learn is: <strong>a step inside a <c>ForEach</c> that
/// declares a compensation must bind the element type.</strong> Only the element is scoped to
/// the iteration; what a body step returns goes into the flow's shared, type-keyed bag.
/// </para>
/// </remarks>
[Capability("equipment.assign", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["asset-register"])]
public sealed class AssignEquipment : ICapability<EquipmentRequest, EquipmentAssignment>
{
    private readonly IAssetRegistry _assets;

    /// <summary>Creates the capability.</summary>
    public AssignEquipment(IAssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
    }

    /// <inheritdoc />
    public async ValueTask<Result<EquipmentAssignment>> ExecuteAsync(
        EquipmentRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var tag = await _assets.AssignAsync(input.Item, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new EquipmentAssignment(input.Item, tag);
    }
}

/// <summary>Takes the item back. The per-element inverse of <see cref="AssignEquipment"/>.</summary>
/// <remarks>
/// <para>
/// <strong>Its input is <see cref="EquipmentRequest"/> because
/// <see cref="AssignEquipment"/>'s is, and this sample was written the other way round
/// first.</strong> Each completed step is recorded with the scope it completed in, so this
/// does run under its own iteration's view of the context — but only the <em>element</em> is
/// scoped to that iteration. What a body step returns goes into the flow's shared, type-keyed
/// bag, so every element's <c>ApprovedEquipment</c> lands in one slot and the last write wins.
/// </para>
/// <para>
/// With the loop's effectful step bound to <c>ApprovedEquipment</c>, both undos read the item
/// the loop ended on and returned it twice, leaving the first item outstanding — with a green
/// trace, because both undos ran and both reported success.
/// <c>CompensationTests.EachElementsUndoReversesItsOwnElement</c> is the test that found it,
/// and <c>CompensationTests.AnUndoSeesItsOwnElementAndTheLoopsLastSharedValue</c> pins the
/// difference so nobody re-introduces it.
/// </para>
/// </remarks>
[Capability("equipment.return", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["asset-register"])]
public sealed class ReturnEquipment : ICapability<EquipmentRequest, EquipmentAssignment>
{
    private readonly IAssetRegistry _assets;

    /// <summary>Creates the capability.</summary>
    public ReturnEquipment(IAssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
    }

    /// <inheritdoc />
    public async ValueTask<Result<EquipmentAssignment>> ExecuteAsync(
        EquipmentRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _assets.ReturnAsync(input.Item, ct).ConfigureAwait(false);

        return new EquipmentAssignment(input.Item, "returned");
    }
}

// ---------------------------------------------------------------------------------------
// The trailing conditional, and the close
// ---------------------------------------------------------------------------------------

/// <summary>Starts a background check. The trailing conditional's <c>then</c> arm.</summary>
[Capability("screening.start", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "screening.write",
    Idempotent = true,
    SideEffects = ["screening-vendor"])]
public sealed class StartBackgroundCheck : ICapability<Identity, BackgroundCheck>
{
    private readonly IAccessControl _access;

    /// <summary>Creates the capability.</summary>
    public StartBackgroundCheck(IAccessControl access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;
    }

    /// <inheritdoc />
    public async ValueTask<Result<BackgroundCheck>> ExecuteAsync(
        Identity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var id = await _access.StartScreeningAsync(input.EmployeeId, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new BackgroundCheck(id);
    }
}

/// <summary>Records that no check was required. The trailing conditional's <c>Otherwise</c> arm.</summary>
/// <remarks>
/// A named capability rather than an empty <c>Otherwise</c>, because a branch that does
/// nothing is invisible in a trace and a reader cannot tell it from a branch that was never
/// taken. This one leaves a row.
/// </remarks>
[Capability("screening.waive", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["screening-log"])]
public sealed class WaiveBackgroundCheck : ICapability<Identity, CheckWaiver>
{
    private readonly IAccessControl _access;

    /// <summary>Creates the capability.</summary>
    public WaiveBackgroundCheck(IAccessControl access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CheckWaiver>> ExecuteAsync(
        Identity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _access.WaiveScreeningAsync(input.EmployeeId, ct).ConfigureAwait(false);

        return new CheckWaiver("the role does not require screening");
    }
}

/// <summary>Posts the welcome pack. The last capability before the event.</summary>
[Capability("welcome.send", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["post"])]
public sealed class SendWelcomePack : ICapability<Identity, WelcomePack>
{
    private readonly IAccessControl _access;

    /// <summary>Creates the capability.</summary>
    public SendWelcomePack(IAccessControl access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;
    }

    /// <inheritdoc />
    public async ValueTask<Result<WelcomePack>> ExecuteAsync(
        Identity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var id = await _access.PostWelcomePackAsync(input.EmployeeId, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new WelcomePack(id);
    }
}

// ---------------------------------------------------------------------------------------
// The composed child's capabilities
// ---------------------------------------------------------------------------------------

/// <summary>Holds a desk at the site. Compensable, inside the child.</summary>
[Capability("workspace.allocate_desk", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["facilities"])]
public sealed class AllocateDesk : ICapability<ProvisionWorkspace, DeskAllocation>
{
    private readonly IFacilities _facilities;

    /// <summary>Creates the capability.</summary>
    public AllocateDesk(IFacilities facilities)
    {
        ArgumentNullException.ThrowIfNull(facilities);
        _facilities = facilities;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DeskAllocation>> ExecuteAsync(
        ProvisionWorkspace input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var desk = await _facilities
            .AllocateDeskAsync(input.Site, ctx.IdempotencyKey, ct)
            .ConfigureAwait(false);

        return desk is null
            ? OnboardingErrors.NoDeskAvailable(input.Site)
            : new DeskAllocation(desk);
    }
}

/// <summary>Releases the desk. The inverse of <see cref="AllocateDesk"/>, and the one the parent's failure has to reach.</summary>
[Capability("workspace.release_desk", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["facilities"])]
public sealed class ReleaseDesk : ICapability<ProvisionWorkspace, DeskAllocation>
{
    private readonly IFacilities _facilities;

    /// <summary>Creates the capability.</summary>
    public ReleaseDesk(IFacilities facilities)
    {
        ArgumentNullException.ThrowIfNull(facilities);
        _facilities = facilities;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DeskAllocation>> ExecuteAsync(
        ProvisionWorkspace input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _facilities.ReleaseDeskAsync(ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new DeskAllocation(ctx.IdempotencyKey);
    }
}

/// <summary>Issues a building pass.</summary>
[Capability("workspace.issue_pass", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["security"])]
public sealed class IssueBuildingPass : ICapability<DeskAllocation, BuildingPass>
{
    private readonly IFacilities _facilities;

    /// <summary>Creates the capability.</summary>
    public IssueBuildingPass(IFacilities facilities)
    {
        ArgumentNullException.ThrowIfNull(facilities);
        _facilities = facilities;
    }

    /// <inheritdoc />
    public async ValueTask<Result<BuildingPass>> ExecuteAsync(
        DeskAllocation input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var pass = await _facilities
            .IssuePassAsync(input.DeskId, ctx.IdempotencyKey, ct)
            .ConfigureAwait(false);

        return pass is null
            ? OnboardingErrors.PassRefused("security hold")
            : new BuildingPass(pass);
    }
}

/// <summary>Cancels a building pass. The inverse of <see cref="IssueBuildingPass"/>.</summary>
[Capability("workspace.cancel_pass", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["security"])]
public sealed class CancelBuildingPass : ICapability<DeskAllocation, BuildingPass>
{
    private readonly IFacilities _facilities;

    /// <summary>Creates the capability.</summary>
    public CancelBuildingPass(IFacilities facilities)
    {
        ArgumentNullException.ThrowIfNull(facilities);
        _facilities = facilities;
    }

    /// <inheritdoc />
    public async ValueTask<Result<BuildingPass>> ExecuteAsync(
        DeskAllocation input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _facilities.CancelPassAsync(ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new BuildingPass(ctx.IdempotencyKey);
    }
}

// ---------------------------------------------------------------------------------------
// The ports. Infrastructure implements these; no capability above names a driver.
// ---------------------------------------------------------------------------------------

/// <summary>People records the application reads and writes.</summary>
public interface IPeopleDirectory
{
    /// <summary>Opens a payroll record, returning its id.</summary>
    ValueTask<string> OpenPayrollAsync(string candidateId, string idempotencyKey, CancellationToken ct);

    /// <summary>Closes a payroll record opened under an idempotency key.</summary>
    ValueTask ClosePayrollAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>Signs a supplier agreement, returning its id.</summary>
    ValueTask<string> SignAgreementAsync(string candidateId, string idempotencyKey, CancellationToken ct);

    /// <summary>Voids an agreement signed under an idempotency key.</summary>
    ValueTask VoidAgreementAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>Creates a directory account, returning its UPN or <c>null</c> when the directory is down.</summary>
    ValueTask<string?> CreateAccountAsync(string candidateId, string idempotencyKey, CancellationToken ct);

    /// <summary>Disables an account created under an idempotency key.</summary>
    ValueTask DisableAccountAsync(string idempotencyKey, CancellationToken ct);
}

/// <summary>Assets the application orders, approves, assigns and takes back.</summary>
public interface IAssetRegistry
{
    /// <summary>Orders an item, returning the order id.</summary>
    ValueTask<string> OrderAsync(string item, string employeeId, string idempotencyKey, CancellationToken ct);

    /// <summary>Cancels an order placed under an idempotency key.</summary>
    ValueTask CancelAsync(string item, string idempotencyKey, CancellationToken ct);

    /// <summary>Records a human approval for an item.</summary>
    ValueTask RecordApprovalAsync(string item, string idempotencyKey, CancellationToken ct);

    /// <summary>Assigns an item, returning its asset tag.</summary>
    ValueTask<string> AssignAsync(string item, string idempotencyKey, CancellationToken ct);

    /// <summary>Takes an item back.</summary>
    ValueTask ReturnAsync(string item, CancellationToken ct);
}

/// <summary>Access, calendar and screening.</summary>
public interface IAccessControl
{
    /// <summary>Grants access, returning the grant id.</summary>
    ValueTask<string> GrantAsync(string upn, string idempotencyKey, CancellationToken ct);

    /// <summary>Revokes a grant made under an idempotency key.</summary>
    ValueTask RevokeAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>Books an induction seat, returning the booking id.</summary>
    ValueTask<string> BookInductionAsync(string employeeId, string idempotencyKey, CancellationToken ct);

    /// <summary>Starts a screening, returning its id.</summary>
    ValueTask<string> StartScreeningAsync(string employeeId, string idempotencyKey, CancellationToken ct);

    /// <summary>Records that screening was not required.</summary>
    ValueTask WaiveScreeningAsync(string employeeId, CancellationToken ct);

    /// <summary>Posts the welcome pack, returning a tracking id.</summary>
    ValueTask<string> PostWelcomePackAsync(string employeeId, string idempotencyKey, CancellationToken ct);
}

/// <summary>Desks and building passes.</summary>
public interface IFacilities
{
    /// <summary>Holds a desk, or returns <c>null</c> when the site is full.</summary>
    ValueTask<string?> AllocateDeskAsync(string site, string idempotencyKey, CancellationToken ct);

    /// <summary>Releases a desk held under an idempotency key.</summary>
    ValueTask ReleaseDeskAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>Issues a pass, or returns <c>null</c> when security refuses.</summary>
    ValueTask<string?> IssuePassAsync(string deskId, string idempotencyKey, CancellationToken ct);

    /// <summary>Cancels a pass issued under an idempotency key.</summary>
    ValueTask CancelPassAsync(string idempotencyKey, CancellationToken ct);
}
