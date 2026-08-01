using System.Text.Json.Serialization;

namespace Workflow;

/// <summary>Source-generated serialisation for the contracts that cross a boundary.</summary>
/// <remarks>
/// <para>
/// <strong><see cref="EmployeeOnboarded"/> is in here for a reason the other two are not.</strong>
/// An <c>Emit</c> step's body is written through a source-generated
/// <c>JsonSerializerContext</c> and never by reflection, so membership here is a build
/// requirement rather than a convention: an event contract that no generated context declares
/// is <c>FLOWX1024</c>, which is exactly how a durable flow's outbox stays trim- and
/// NativeAOT-safe.
/// </para>
/// <para>
/// <strong>The contracts below <see cref="EmployeeOnboarded"/> are here for a third reason:
/// <c>FLOWX1006</c>.</strong> Both flows in this sample are <c>Durable</c>, so each step's
/// result and the state bag after it are journaled, and every one of those needs the same
/// generated metadata for the same reason the event body does. Before WP-59 the journal
/// recorded no payloads at all — truthful about which steps had run, silent about what they
/// produced — so a resumed instance re-entered with an empty bag and re-ran everything after
/// the frontier. The list is not maintained by hand: the compiler names the missing contract.
/// </para>
/// <para>
/// The camelCase policy is not decoration. Without it the wire names are the C# ones, and a
/// client sending the conventional <c>"candidateId"</c> gets a <c>CandidateId</c> of null —
/// a missing member deserialises to <c>default</c>. Here <c>offer.validate</c> rejects it,
/// which is the point of validating at the first step.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OnboardEmployee))]
[JsonSerializable(typeof(OnboardingResult))]
[JsonSerializable(typeof(EmployeeOnboarded))]
[JsonSerializable(typeof(ValidatedOffer))]
[JsonSerializable(typeof(PayrollRecord))]
[JsonSerializable(typeof(SupplierAgreement))]
[JsonSerializable(typeof(Identity))]
[JsonSerializable(typeof(LaptopOrder))]
[JsonSerializable(typeof(AccessGrant))]
[JsonSerializable(typeof(ApprovedEquipment))]
[JsonSerializable(typeof(EquipmentAssignment))]
[JsonSerializable(typeof(BackgroundCheck))]
[JsonSerializable(typeof(CheckWaiver))]
[JsonSerializable(typeof(WelcomePack))]
[JsonSerializable(typeof(InductionBooking))]
[JsonSerializable(typeof(ProvisionWorkspace))]
[JsonSerializable(typeof(DeskAllocation))]
[JsonSerializable(typeof(BuildingPass))]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext;

/// <summary>People records, in memory.</summary>
/// <remarks>
/// The capabilities depend on <see cref="IPeopleDirectory"/>, not on this. Every write is
/// keyed on the idempotency key, which is what the capabilities' <c>Idempotent = true</c>
/// promises and what makes a replayed step safe.
/// </remarks>
internal sealed class InMemoryPeopleDirectory : IPeopleDirectory
{
    private readonly Dictionary<string, string> _payroll = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _agreements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _accounts = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>Accounts that exist right now. Read by the tests to check an unwind landed.</summary>
    public int OpenAccounts
    {
        get
        {
            lock (_sync)
            {
                return _accounts.Count;
            }
        }
    }

    /// <summary>Payroll records that exist right now.</summary>
    public int OpenPayrollRecords
    {
        get
        {
            lock (_sync)
            {
                return _payroll.Count;
            }
        }
    }

    public ValueTask<string> OpenPayrollAsync(string candidateId, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_payroll.TryGetValue(idempotencyKey, out var id))
            {
                id = "pay-" + candidateId;
                _payroll[idempotencyKey] = id;
            }

            return ValueTask.FromResult(id);
        }
    }

    public ValueTask ClosePayrollAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _payroll.Remove(idempotencyKey);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string> SignAgreementAsync(string candidateId, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_agreements.TryGetValue(idempotencyKey, out var id))
            {
                id = "agr-" + candidateId;
                _agreements[idempotencyKey] = id;
            }

            return ValueTask.FromResult(id);
        }
    }

    public ValueTask VoidAgreementAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _agreements.Remove(idempotencyKey);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> CreateAccountAsync(string candidateId, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_accounts.TryGetValue(idempotencyKey, out var upn))
            {
                upn = candidateId + "@example.test";
                _accounts[idempotencyKey] = upn;
            }

            return ValueTask.FromResult<string?>(upn);
        }
    }

    public ValueTask DisableAccountAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _accounts.Remove(idempotencyKey);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Assets, in memory.</summary>
internal sealed class InMemoryAssetRegistry : IAssetRegistry
{
    private readonly HashSet<string> _orders = new(StringComparer.Ordinal);
    private readonly List<string> _assigned = [];
    private readonly List<string> _approved = [];
    private readonly Lock _sync = new();

    /// <summary>Items still in someone's hands, in the order they were assigned.</summary>
    public IReadOnlyList<string> Assigned
    {
        get
        {
            lock (_sync)
            {
                return [.. _assigned];
            }
        }
    }

    /// <summary>Items a human signed for.</summary>
    public IReadOnlyList<string> Approved
    {
        get
        {
            lock (_sync)
            {
                return [.. _approved];
            }
        }
    }

    /// <summary>Orders still outstanding.</summary>
    public int OutstandingOrders
    {
        get
        {
            lock (_sync)
            {
                return _orders.Count;
            }
        }
    }

    public ValueTask<string> OrderAsync(string item, string employeeId, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _orders.Add(item + ":" + idempotencyKey);
        }

        return ValueTask.FromResult(item + "-order");
    }

    public ValueTask CancelAsync(string item, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _orders.Remove(item + ":" + idempotencyKey);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordApprovalAsync(string item, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _approved.Add(item);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string> AssignAsync(string item, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _assigned.Add(item);
        }

        return ValueTask.FromResult("tag-" + item);
    }

    public ValueTask ReturnAsync(string item, CancellationToken ct)
    {
        lock (_sync)
        {
            _assigned.Remove(item);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Access, calendar and screening, in memory.</summary>
internal sealed class InMemoryAccessControl : IAccessControl
{
    private readonly HashSet<string> _grants = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>Grants still standing.</summary>
    public int OutstandingGrants
    {
        get
        {
            lock (_sync)
            {
                return _grants.Count;
            }
        }
    }

    public ValueTask<string> GrantAsync(string upn, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _grants.Add(idempotencyKey);
        }

        return ValueTask.FromResult("grant-" + upn);
    }

    public ValueTask RevokeAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _grants.Remove(idempotencyKey);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string> BookInductionAsync(string employeeId, string idempotencyKey, CancellationToken ct)
        => ValueTask.FromResult("induction-" + employeeId);

    public ValueTask<string> StartScreeningAsync(string employeeId, string idempotencyKey, CancellationToken ct)
        => ValueTask.FromResult("check-" + employeeId);

    public ValueTask WaiveScreeningAsync(string employeeId, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask<string> PostWelcomePackAsync(string employeeId, string idempotencyKey, CancellationToken ct)
        => ValueTask.FromResult("post-" + employeeId);
}

/// <summary>Desks and passes, in memory.</summary>
internal sealed class InMemoryFacilities : IFacilities
{
    private readonly Dictionary<string, string> _desks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _passes = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    private int _next;

    /// <summary>Desks still held.</summary>
    public int HeldDesks
    {
        get
        {
            lock (_sync)
            {
                return _desks.Count;
            }
        }
    }

    public ValueTask<string?> AllocateDeskAsync(string site, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_desks.TryGetValue(idempotencyKey, out var desk))
            {
                desk = string.Concat(site, "-desk-", (++_next).ToString(System.Globalization.CultureInfo.InvariantCulture));
                _desks[idempotencyKey] = desk;
            }

            return ValueTask.FromResult<string?>(desk);
        }
    }

    public ValueTask ReleaseDeskAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _desks.Remove(idempotencyKey);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> IssuePassAsync(string deskId, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_passes.TryGetValue(idempotencyKey, out var pass))
            {
                pass = "pass-" + deskId;
                _passes[idempotencyKey] = pass;
            }

            return ValueTask.FromResult<string?>(pass);
        }
    }

    public ValueTask CancelPassAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            _passes.Remove(idempotencyKey);
        }

        return ValueTask.CompletedTask;
    }
}
