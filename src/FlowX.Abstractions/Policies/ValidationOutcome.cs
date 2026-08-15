namespace FlowX;

/// <summary>
/// One contract member that failed one declared rule.
/// </summary>
/// <param name="Field">
/// The member's name, as the contract declares it. It reaches the caller, so it is the name a
/// caller can act on.
/// </param>
/// <param name="Rule">
/// Which rule refused it — <c>required</c>, <c>range</c> or <c>length</c>. Stable and
/// machine-readable, so a client can branch on it without parsing English.
/// </param>
/// <param name="Message">
/// What was expected, in words. <strong>Never what was supplied.</strong>
/// </param>
/// <remarks>
/// <para>
/// <strong>The message names the bound and never the value, and that is a structural property
/// rather than a convention.</strong> Every message is built by the compiler out of the rule's
/// own declared bounds — literals in the author's source — so there is no expression in the
/// generated code through which a member's value could reach one. A member the flow marks
/// <c>[Sensitive]</c> therefore cannot appear in a validation message, for the same reason it
/// cannot appear in a journal row: there is no path.
/// </para>
/// <para>
/// It is also why the reverse — redacting a sensitive field's <em>name</em> — is not done. A
/// caller told that something is wrong and not which field cannot fix it, and the name of a
/// field is part of the published contract already.
/// </para>
/// </remarks>
public readonly record struct FieldError(string Field, string Rule, string Message);

/// <summary>
/// What a step's generated validation found: nothing, some field errors, or that there was no
/// generated validation to run.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three states rather than two, and the third one is what keeps stage 3 honest.</strong>
/// <see cref="Unavailable"/> is <c>default</c>, so a dispatcher that does not implement
/// <c>Validate</c> answers it without writing a line — and the engine refuses the step rather
/// than admitting it. A two-state outcome whose default was "valid" would make a declared
/// <c>Validate</c> read as satisfied on every dispatcher that had not been regenerated, which
/// is the half-executing policy
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
/// refuses, arriving through a default value.
/// </para>
/// <para>
/// A <c>struct</c>, and <see cref="Valid"/> allocates nothing: the overwhelmingly common answer
/// is "this input is fine", and a validated step must not cost a heap object per execution to
/// say so.
/// </para>
/// </remarks>
public readonly struct ValidationOutcome : IEquatable<ValidationOutcome>
{
    private ValidationOutcome(bool ran, IReadOnlyList<FieldError>? failures)
    {
        WasChecked = ran;
        Failures = failures;
    }

    /// <summary>The input satisfied every declared rule.</summary>
    public static ValidationOutcome Valid { get; } = new(true, null);

    /// <summary>
    /// Nothing checked the input. The engine reads this as a refusal, never as an admission.
    /// </summary>
    /// <remarks>
    /// <c>default</c>, deliberately. See this type's remarks.
    /// </remarks>
    public static ValidationOutcome Unavailable => default;

    /// <summary>The input broke at least one declared rule.</summary>
    /// <param name="failures">What broke. Must not be empty — an empty list is <see cref="Valid"/>.</param>
    public static ValidationOutcome Invalid(IReadOnlyList<FieldError> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        return failures.Count == 0 ? Valid : new ValidationOutcome(true, failures);
    }

    /// <summary>Whether anything actually looked at the input.</summary>
    public bool WasChecked { get; }

    /// <summary>What broke, or <c>null</c> when nothing did.</summary>
    public IReadOnlyList<FieldError>? Failures { get; }

    /// <summary>True when the input was checked and satisfied every rule.</summary>
    public bool IsValid => WasChecked && Failures is null or { Count: 0 };

    /// <inheritdoc />
    public bool Equals(ValidationOutcome other) =>
        WasChecked == other.WasChecked && ReferenceEquals(Failures, other.Failures);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValidationOutcome other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(WasChecked, Failures);

    /// <summary>Whether two outcomes are the same.</summary>
    public static bool operator ==(ValidationOutcome left, ValidationOutcome right) => left.Equals(right);

    /// <summary>Whether two outcomes differ.</summary>
    public static bool operator !=(ValidationOutcome left, ValidationOutcome right) => !left.Equals(right);
}
