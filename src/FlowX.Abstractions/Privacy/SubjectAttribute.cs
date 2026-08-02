namespace FlowX;

/// <summary>
/// Marks the member of a flow's input contract that names the person the record is about.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists so that erasure is possible at all.</strong> A journal that records
/// opaque documents can be searched for a subject only by reading every document it holds,
/// which is both the slowest possible query and a second disclosure of the data being
/// erased. Marking the member lets the runtime record a one-way digest of it on the
/// instance row, so "everything about this person" is an indexed lookup and never a scan.
/// </para>
/// <para>
/// <strong>The value itself is not stored by this attribute, and marking a member
/// <c>[Subject]</c> does not make it readable.</strong> The digest is computed inside
/// <see cref="JournalPayload"/>, before the redaction pass and without an accessor being
/// added to it, so a member that is both <c>[Subject]</c> and <c>[Sensitive]</c> — a
/// national identifier, which is the ordinary case — reaches the store as
/// <see cref="JournalPayload.Redacted"/> and is still erasable. That combination is the
/// point: the identifier that must never be written down is the one you have to find rows
/// by.
/// </para>
/// <para>
/// <strong>The digest is not anonymisation.</strong> It is a join key that is not the
/// value. A national identifier is drawn from a small enough space that anybody holding a
/// candidate can confirm it by hashing it, so the column is still personal data and is
/// still inside the tenant's row-level security. What it buys is that reading the column
/// does not hand out identifiers, and that erasure has something to match on.
/// See <c>docs/adr/ADR-0061</c>.
/// </para>
/// <para>
/// One member, of type <c>string</c>, on a <c>Durable</c> flow's input contract.
/// <c>FLOWX1047</c> refuses every other shape, because each of them is a declaration the
/// runtime would have to ignore.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SubjectAttribute : Attribute;
