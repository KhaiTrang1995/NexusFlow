using System.Security.Cryptography;
using System.Text;

namespace FlowX;

/// <summary>
/// Turns the value of a <c>[Subject]</c> member into the handle a journal row is found by.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One function, deterministic, unkeyed — and each of those three is a decision.</strong>
/// </para>
/// <para>
/// <em>Deterministic</em>, because erasure arrives months later holding only the identifier
/// and has to reach the same rows the intake wrote. Anything drawn at write time would have
/// to be stored to be reproduced, and a stored salt beside the digest it salts protects
/// nothing.
/// </para>
/// <para>
/// <em>Unkeyed</em>, because a keyed digest is a key-management problem, and this repository
/// ships no place to keep a key that outlives every row it has to match. A deployment that
/// has one — an HSM, a KMS — gains nothing here it could not get by canonicalising and
/// hashing the identifier before it reaches the flow, and it loses nothing by not doing so:
/// the column is personal data either way, is inside the tenant's row-level security either
/// way, and is erasable either way.
/// </para>
/// <para>
/// <em>Not anonymisation</em>, stated here rather than left to be assumed. SHA-256 over a
/// national identifier is preimage-resistant in the abstract and not in practice, because
/// the space of national identifiers is small enough to enumerate. Anyone holding a
/// candidate identifier can confirm whether this deployment has seen it. What the digest
/// buys is that reading the column does not <em>hand out</em> identifiers — an operator, a
/// backup, a support export and a <c>SELECT *</c> all see sixty-four hex characters — and
/// that a subject's rows can be found without storing the thing that finds them.
/// </para>
/// </remarks>
public static class SubjectDigest
{
    /// <summary>
    /// The domain-separation prefix every digest is computed over, and its version.
    /// </summary>
    /// <remarks>
    /// Domain separation, so a digest computed here can never collide with a digest of the same
    /// string taken for another purpose in the same system — and so the scheme can be versioned
    /// if the hash ever has to change: a second version writes a different prefix, and rows
    /// written under the first stay matchable by the first.
    /// </remarks>
    public const string Domain = "flowx.subject.v1";

    /// <summary>The number of characters a digest is written as.</summary>
    public const int Length = 64;

    /// <summary>
    /// What separates <see cref="Domain"/> from the identifier: the ASCII unit separator.
    /// </summary>
    /// <remarks>
    /// A control character rather than a colon or a dot, because it is not a character an
    /// identifier contains. Without it, a subject value beginning with the remainder of the
    /// prefix would digest to the same handle as a different value under a different prefix,
    /// which is exactly the collision domain separation exists to prevent.
    /// </remarks>
    private const char Separator = (char)0x1F;

    /// <summary>
    /// Computes the digest of one subject identifier.
    /// </summary>
    /// <param name="subject">The identifier, exactly as the contract member held it.</param>
    /// <returns>Lower-case hexadecimal, <see cref="Length"/> characters.</returns>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is null or blank.</exception>
    /// <remarks>
    /// <strong>No trimming, no case folding, no normalisation.</strong> Two spellings of one
    /// identifier are two subjects here, and that is the honest behaviour: which spellings of
    /// a national identifier, a patient number or an email address are the same person is a
    /// question about that identifier's issuing authority, and a platform that guessed would
    /// silently merge two people or silently split one. An application that knows the answer
    /// canonicalises before the value reaches the flow.
    /// </remarks>
    public static string Of(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException(
                "A subject digest is computed from an identifier, and this one is blank. A " +
                "blank subject would give every record with a missing identifier the same " +
                "handle, so an erasure for one of them would erase all of them.",
                nameof(subject));
        }

        var bytes = Encoding.UTF8.GetBytes(Domain + Separator + subject);

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Computes the digest, or answers <c>null</c> when there is no identifier to compute one
    /// from.
    /// </summary>
    /// <param name="subject">The identifier, or null or blank when the contract carried none.</param>
    /// <returns>The digest, or <c>null</c>.</returns>
    /// <remarks>
    /// The write path's overload. A row that carries no digest is a row erasure cannot find,
    /// which is a truthful record of an instance whose input named no subject — and it is
    /// strictly better than the alternative, which is every subject-less instance in the
    /// deployment sharing one handle.
    /// </remarks>
    public static string? OfOrNull(string? subject) =>
        string.IsNullOrWhiteSpace(subject) ? null : Of(subject);
}
