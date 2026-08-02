using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using FlowX;

namespace Healthcare;

/// <summary>Source-generated serialisation for everything this application writes down.</summary>
/// <remarks>
/// <para>
/// Every contract this flow puts on the wire, into the journal or into the outbox is declared
/// here, for the reason <c>samples/banking</c> states at length: <c>JournalPayload.Of</c> takes
/// a <c>JsonTypeInfo&lt;T&gt;</c> and has no overload that reflects over a type, which is what
/// keeps the write path trim- and NativeAOT-safe and what makes membership of a generated
/// context a compile error rather than a convention. <c>FLOWX1006</c> names the line to add.
/// </para>
/// <para>
/// The camelCase policy matters here for a reason it does not in every sample: the redaction
/// pass matches member names case-insensitively precisely so that the wire's
/// <c>"nationalId"</c> and the contract's <c>NationalId</c> are one member. A pass that matched
/// exactly would let through the spelling that actually ends up in the document.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PatientIntake))]
[JsonSerializable(typeof(IntakeResult))]
[JsonSerializable(typeof(PatientAdmitted))]
[JsonSerializable(typeof(ValidatedIntake))]
[JsonSerializable(typeof(ConsentVerified))]
[JsonSerializable(typeof(PatientIdentity))]
[JsonSerializable(typeof(StoredRecord))]
[JsonSerializable(typeof(RecordWithdrawn))]
[JsonSerializable(typeof(ErasurePetition))]
[JsonSerializable(typeof(ErasureReport))]
internal sealed partial class HealthcareJsonContext : JsonSerializerContext;

/// <summary>What a clinic's consent register recorded.</summary>
/// <param name="Reference">The register's own reference for this decision.</param>
/// <param name="Purpose">What the patient agreed to.</param>
/// <param name="GrantedAt">When they agreed.</param>
/// <param name="WithdrawnAt">When they took it back, or null while it stands.</param>
public sealed record ConsentRecord(
    string Reference,
    CarePurpose Purpose,
    DateTimeOffset GrantedAt,
    DateTimeOffset? WithdrawnAt);

/// <summary>Where a clinic keeps its patients' consent decisions.</summary>
/// <remarks>
/// A port, so <see cref="VerifyConsent"/> depends on an interface and never on the class
/// below. A real clinic's register is a system of record with its own audit trail; what the
/// flow needs from it is one lookup.
/// </remarks>
public interface IConsentRegister
{
    /// <summary>The decision recorded under a reference, or null when there is none.</summary>
    /// <param name="reference">The consent reference the intake quoted.</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<ConsentRecord?> LookupAsync(string reference, CancellationToken ct);

    /// <summary>Records that the patient has taken the consent back.</summary>
    /// <param name="reference">The consent reference.</param>
    /// <param name="at">When they withdrew it.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns><c>true</c> when a standing consent was withdrawn by this call.</returns>
    ValueTask<bool> WithdrawAsync(string reference, DateTimeOffset at, CancellationToken ct);
}

/// <summary>The clinic's map from a national identifier to its own opaque patient id.</summary>
public interface IPatientIndex
{
    /// <summary>Finds or mints this clinic's identifier for a patient.</summary>
    /// <param name="tenantId">Which clinic is asking. Two clinics never share a patient id.</param>
    /// <param name="nationalId">The patient's national identifier.</param>
    /// <param name="dateOfBirth">Disambiguates two patients who share an identifier's spelling.</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<PatientIdentity> ResolveAsync(
        string tenantId,
        string nationalId,
        DateOnly dateOfBirth,
        CancellationToken ct);
}

/// <summary>The clinic's clinical record store.</summary>
public interface IRecordStore
{
    /// <summary>Files a record, or answers null when the store refused it.</summary>
    /// <param name="tenantId">Which clinic's store.</param>
    /// <param name="patientId">The clinic's opaque patient id.</param>
    /// <param name="admissionKey">The flow's idempotency key, so a retry files one record.</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<StoredRecord?> FileAsync(
        string tenantId,
        string patientId,
        string admissionKey,
        CancellationToken ct);

    /// <summary>Withdraws a record this flow filed and could not finish.</summary>
    /// <param name="tenantId">Which clinic's store.</param>
    /// <param name="patientId">The clinic's opaque patient id.</param>
    /// <param name="admissionKey">The key the record was filed under.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns><c>true</c> when there was a record to withdraw.</returns>
    ValueTask<bool> WithdrawAsync(
        string tenantId,
        string patientId,
        string admissionKey,
        CancellationToken ct);

    /// <summary>Every record this clinic holds for a patient. For the tests and the demo.</summary>
    /// <param name="tenantId">Which clinic's store.</param>
    /// <param name="patientId">The clinic's opaque patient id.</param>
    IReadOnlyList<StoredRecord> For(string tenantId, string patientId);
}

/// <summary>
/// A consent register in memory, seeded with the decisions the README's transcripts use.
/// </summary>
/// <remarks>
/// No clinic would ship this. It is here so the sample runs from one <c>dotnet run</c>, and
/// the interface above is what a deployment replaces.
/// </remarks>
public sealed class InMemoryConsentRegister : IConsentRegister
{
    /// <summary>A standing consent to be treated. The transcript that succeeds.</summary>
    public const string TreatmentReference = "consent-treatment-01";

    /// <summary>A standing consent to be studied, and to nothing else.</summary>
    public const string ResearchReference = "consent-research-01";

    /// <summary>A consent the patient has already taken back.</summary>
    public const string WithdrawnReference = "consent-withdrawn-01";

    private readonly ConcurrentDictionary<string, ConsentRecord> _records;

    /// <summary>Creates the register.</summary>
    public InMemoryConsentRegister()
    {
        var granted = new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero);

        _records = new ConcurrentDictionary<string, ConsentRecord>(StringComparer.Ordinal)
        {
            [TreatmentReference] = new(TreatmentReference, CarePurpose.Treatment, granted, null),
            [ResearchReference] = new(ResearchReference, CarePurpose.Research, granted, null),
            [WithdrawnReference] = new(
                WithdrawnReference,
                CarePurpose.Treatment,
                granted,
                granted.AddDays(30)),
        };
    }

    /// <inheritdoc />
    public ValueTask<ConsentRecord?> LookupAsync(string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return ValueTask.FromResult(_records.TryGetValue(reference, out var record) ? record : null);
    }

    /// <inheritdoc />
    public ValueTask<bool> WithdrawAsync(string reference, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!_records.TryGetValue(reference, out var record) || record.WithdrawnAt is not null)
        {
            return ValueTask.FromResult(false);
        }

        _records[reference] = record with { WithdrawnAt = at };

        return ValueTask.FromResult(true);
    }
}

/// <summary>A patient index in memory, keyed by clinic and national identifier.</summary>
/// <remarks>
/// <strong>The key is the pair, and that is the whole of the isolation this class provides.</strong>
/// It is deliberately not the only wall: the journal's rows are isolated by PostgreSQL's own
/// row-level security, which holds whether or not this dictionary is keyed correctly. An
/// application's own stores are the application's own problem — <c>docs/16 §1</c> opens by
/// warning about the <c>WHERE TenantId = @t</c> that somebody eventually forgets — and this
/// class is what that warning looks like when it is heeded.
/// </remarks>
public sealed class InMemoryPatientIndex : IPatientIndex
{
    private readonly ConcurrentDictionary<string, string> _patients = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<PatientIdentity> ResolveAsync(
        string tenantId,
        string nationalId,
        DateOnly dateOfBirth,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(nationalId);

        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{tenantId}/{nationalId}/{dateOfBirth:O}");

        // Derived from the key rather than drawn at random, so two nodes that meet the same
        // patient at the same moment agree — and so a replay of this flow reaches the same id.
        // A Guid here would be FLOWX1008's ambient identity moved one class sideways.
        var patientId = "pat-" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key)))[..12];

        var minted = _patients.TryAdd(key, patientId);

        return ValueTask.FromResult(new PatientIdentity(_patients[key], minted));
    }
}

/// <summary>A clinical record store in memory, keyed by clinic.</summary>
public sealed class InMemoryRecordStore : IRecordStore
{
    private readonly ConcurrentDictionary<string, StoredRecord> _records = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    /// <param name="clock">Stamps the record. Injected so a test can pin it.</param>
    public InMemoryRecordStore(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public ValueTask<StoredRecord?> FileAsync(
        string tenantId,
        string patientId,
        string admissionKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(patientId);
        ArgumentNullException.ThrowIfNull(admissionKey);

        var key = Key(tenantId, patientId, admissionKey);

        // Keyed by the admission, so a retried step files one record rather than two — which
        // is what lets the capability declare Idempotent, which is what lets the step carry a
        // Retry at all (FLOWX1014).
        var record = _records.GetOrAdd(
            key,
            _ => new StoredRecord(patientId, "rec-" + admissionKey, _clock.GetUtcNow()));

        return ValueTask.FromResult<StoredRecord?>(record);
    }

    /// <inheritdoc />
    public ValueTask<bool> WithdrawAsync(
        string tenantId,
        string patientId,
        string admissionKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(patientId);
        ArgumentNullException.ThrowIfNull(admissionKey);

        return ValueTask.FromResult(_records.TryRemove(Key(tenantId, patientId, admissionKey), out _));
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredRecord> For(string tenantId, string patientId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(patientId);

        var prefix = tenantId + "/" + patientId + "/";

        return _records
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .ToList();
    }

    private static string Key(string tenantId, string patientId, string admissionKey) =>
        tenantId + "/" + patientId + "/" + admissionKey;
}

/// <summary>
/// The clinical audit trail, in memory.
/// </summary>
/// <remarks>
/// <para>
/// No clinic would ship this: a clinical audit record belongs in an append-only table, a WORM
/// bucket or a SIEM, and <c>IAuditSink</c> has no default implementation for exactly that
/// reason — where such a record is kept is a decision about a regulatory regime rather than
/// about FlowX, and a default would be a control that reads as configured and survives no
/// restart.
/// </para>
/// <para>
/// <strong>What reaches this sink is already redacted.</strong> The document is
/// <c>AuditRecord.Payload.ToJson()</c>, which is the one exit a payload has, so a national
/// identifier is <c>[redacted]</c> here for the same structural reason it is <c>[redacted]</c>
/// in the journal row beside it. This class could not write the value if it tried.
/// </para>
/// </remarks>
public sealed class InMemoryClinicalAudit : IAuditSink
{
    private readonly Lock _sync = new();
    private readonly List<AuditEntry> _entries = [];

    /// <summary>One recorded step: the record's own fields, and the document it carried.</summary>
    /// <param name="Record">What the engine described.</param>
    /// <param name="Document">
    /// <see cref="AuditRecord.Payload"/> as <see cref="JournalPayload.ToJson"/> wrote it —
    /// redacted, stamped, and the only form this sink ever sees.
    /// </param>
    public readonly record struct AuditEntry(AuditRecord Record, string? Document);

    /// <summary>Everything recorded so far, in the order the engine recorded it.</summary>
    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_sync)
        {
            _entries.Add(new AuditEntry(record, record.Payload.ToJson()));
        }

        return ValueTask.CompletedTask;
    }
}
