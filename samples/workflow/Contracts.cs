using FlowX;

namespace Workflow;

/// <summary>How the new joiner is engaged. The value the flow's <c>Switch</c> branches on.</summary>
/// <remarks>
/// An <c>enum</c> rather than a string, so a <c>.Case(...)</c> whose value does not exist is
/// a C# error at the call site. The switch is still not exhaustive — an <c>enum</c> can hold
/// a value no member declares — which is exactly why the flow states its miss with a
/// <c>.Default(b =&gt; b.Fail(...))</c> instead of relying on the compiler.
/// </remarks>
public enum EmploymentType
{
    /// <summary>On the payroll. Opens a payroll record, which is compensable.</summary>
    Permanent = 0,

    /// <summary>Engaged through a supplier agreement. A different, equally compensable, record.</summary>
    Contractor = 1,

    /// <summary>Not onboardable by this flow. Reaches the <c>Default</c> arm and is rejected.</summary>
    Intern = 2,
}

/// <summary>What a caller asks for.</summary>
/// <param name="CandidateId">Who is joining.</param>
/// <param name="Employment">How they are engaged. Selects the switch arm.</param>
/// <param name="Site">Which office, passed to the composed workspace flow.</param>
/// <param name="Equipment">What they need on day one. The collection the <c>ForEach</c> walks.</param>
/// <param name="RequiresBackgroundCheck">Selects the arm of the trailing conditional.</param>
/// <param name="NationalId">
/// Marked <c>[Sensitive]</c>. What that buys today is narrower than it sounds and is worth
/// stating exactly: the member is named in <c>flowx.manifest.json</c> and emitted onto the
/// flow as <c>SensitiveMembers</c>, the HTTP endpoint strips it from an error response, and
/// <c>JournalPayload</c> redacts every marked member on its only exit. What it is not
/// protecting is a journal row, because there is no journal row to protect: the generated
/// dispatcher writes no payload for a step's result and none for the trigger input, so
/// <c>flow_instance.input</c> and <c>flow_step.result</c> are null for every instance this
/// application has ever written. The control is real and the surface it currently covers is
/// the wire, not the store.
/// </param>
public sealed record OnboardEmployee(
    string CandidateId,
    EmploymentType Employment,
    string Site,
    IReadOnlyList<EquipmentRequest> Equipment,
    bool RequiresBackgroundCheck,
    [property: Sensitive] string NationalId);

/// <summary>One item of kit, and whether a human has to sign for it.</summary>
/// <param name="Item">What it is.</param>
/// <param name="NeedsApproval">Selects the arm of the conditional <em>inside</em> the loop.</param>
public sealed record EquipmentRequest(string Item, bool NeedsApproval);

/// <summary>An offer that passed validation.</summary>
public sealed record ValidatedOffer(
    string CandidateId,
    EmploymentType Employment,
    string Site,
    IReadOnlyList<EquipmentRequest> Equipment,
    bool RequiresBackgroundCheck);

/// <summary>A payroll record. The <c>Permanent</c> arm's effect, and reversible.</summary>
public sealed record PayrollRecord(string CandidateId, string PayrollId);

/// <summary>A supplier agreement. The <c>Contractor</c> arm's effect, and reversible.</summary>
public sealed record SupplierAgreement(string CandidateId, string AgreementId);

/// <summary>A directory account.</summary>
public sealed record Identity(string EmployeeId, string Upn);

/// <summary>A laptop on order. One fork branch's output.</summary>
public sealed record LaptopOrder(string OrderId);

/// <summary>Access to the systems the role needs. Another fork branch's output.</summary>
public sealed record AccessGrant(string GrantId);

/// <summary>A seat at the induction session. The third fork branch's output.</summary>
/// <remarks>
/// A distinct type from its two siblings on purpose: branches of a <c>Parallel</c> must
/// write disjoint context slots, and the state bag is keyed by contract type. Two branches
/// declaring the same output would be two threads writing one key —
/// <c>FLOWX1013</c> refuses it at build time rather than letting the step after the join
/// read whichever branch happened to finish last.
/// </remarks>
public sealed record InductionBooking(string BookingId);

/// <summary>An item of kit cleared for issue, however it was cleared.</summary>
/// <remarks>
/// <para>
/// <strong>Both arms of the loop's conditional produce this, and the step after them
/// deliberately does not bind it.</strong> Producing one contract on both paths is the
/// ordinary way to let a later step bind by type without knowing which arm ran — and inside a
/// <c>ForEach</c> it is a trap, because the bag it lands in is the <em>flow's</em>, keyed by
/// contract type, not the iteration's. After the loop it holds one value: the last element's.
/// </para>
/// <para>
/// That is harmless while the loop is running — at a concurrency bound of one each element
/// writes and reads its own value before the next begins — and it is not harmless at unwind
/// time, which happens after the loop has finished. See <see cref="AssignEquipment"/> for the
/// rule that follows and <c>CompensationTests</c> for the measurement.
/// </para>
/// </remarks>
public sealed record ApprovedEquipment(string Item, string ClearedBy);

/// <summary>Kit actually handed over. Reversible, per element.</summary>
public sealed record EquipmentAssignment(string Item, string AssetTag);

/// <summary>A background check that was started.</summary>
public sealed record BackgroundCheck(string CheckId);

/// <summary>A note that no check was required. The <c>Otherwise</c> arm's output.</summary>
public sealed record CheckWaiver(string Reason);

/// <summary>The welcome pack, posted.</summary>
public sealed record WelcomePack(string TrackingId);

/// <summary>What the caller gets back.</summary>
/// <remarks>
/// <strong>There is no desk id here, and its absence is the sample's most honest line.</strong>
/// The desk is allocated by the composed workspace flow, and a sub-flow's result does not
/// flow into its parent's context — see <see cref="WorkspaceReady"/>. The projection can
/// only name what the parent's own steps produced, so this record does too. Inventing a
/// field the flow cannot fill would be the documentation-ahead-of-code failure this
/// repository's samples exist to avoid.
/// </remarks>
public sealed record OnboardingResult(string EmployeeId, int EquipmentIssued);

/// <summary>Published once the joiner is onboarded.</summary>
/// <remarks>
/// Staged in the same transaction as the step that emits it, because this flow is
/// <c>Durable</c> and therefore has a transaction to stage into. That is the condition
/// <c>FLOWX1024</c> checks, and the reason <c>samples/ecommerce</c> has to suppress the rule
/// where this sample does not.
/// </remarks>
public sealed record EmployeeOnboarded(string EmployeeId, string Site, int EquipmentIssued);

// ---------------------------------------------------------------------------------------
// The composed child's contracts
// ---------------------------------------------------------------------------------------

/// <summary>What the parent asks the workspace flow for.</summary>
public sealed record ProvisionWorkspace(string EmployeeId, string Site);

/// <summary>A desk, held.</summary>
public sealed record DeskAllocation(string DeskId);

/// <summary>A building pass, issued.</summary>
public sealed record BuildingPass(string PassId);

/// <summary>What the workspace flow returns.</summary>
/// <remarks>
/// <strong>The parent never sees this value, and that is a stated limit of the DSL rather
/// than an oversight in the sample.</strong> A sub-flow is composed for its effects: the
/// parent learns that the child succeeded or failed, and the steps after it bind to what the
/// <em>parent's</em> own steps produced. <c>docs/08-Flow-Definition.md §3.7</c> gives the two
/// ways of passing the child's answer up that were considered and refused. It is why
/// <see cref="OnboardingResult"/>'s desk id comes from a parent step and not from here.
/// </remarks>
public sealed record WorkspaceReady(string DeskId, string PassId);
