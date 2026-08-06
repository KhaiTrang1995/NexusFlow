using System.Security.Claims;
using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>Declares a rule that refuses a record.</summary>
/// <param name="AppliesTo">The built-in kind, or null when <paramref name="Target"/> is given.</param>
/// <param name="Target">The custom object, or null when <paramref name="AppliesTo"/> is given.</param>
/// <param name="Name">The identifier. Lower case, snake case.</param>
/// <param name="Field">What it reads. A declared field, or a built-in one on a built-in entity.</param>
/// <param name="Operator">How it compares. The same five a transition guard has.</param>
/// <param name="Value">What it compares against.</param>
/// <param name="Message">
/// What the caller is told when it fires. The whole reason a rule beats a <c>CHECK</c>
/// constraint: an administrator writes the sentence the person who tripped it reads.
/// </param>
public sealed record DefineValidationRule(
    EntityKind? AppliesTo,
    Guid? Target,
    string Name,
    string Field,
    GuardOperator Operator,
    string Value,
    string Message);

/// <summary>The rule that was declared.</summary>
/// <param name="RuleId">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record ValidationRuleDefined(Guid RuleId, string Name);

/// <summary>A declared rule, as much of it as evaluating one needs.</summary>
/// <param name="Id">The rule.</param>
/// <param name="Name">Its name.</param>
/// <param name="Field">What it reads.</param>
/// <param name="Operator">How it compares.</param>
/// <param name="Value">What it compares against.</param>
/// <param name="Message">What the caller is told.</param>
public sealed record ValidationRuleRow(
    Guid Id,
    string Name,
    string Field,
    GuardOperator Operator,
    string Value,
    string Message);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the validation and field-policy machinery can produce.</summary>
public static class FieldPolicyErrors
{
    /// <summary>A record tripped a rule an administrator wrote.</summary>
    /// <param name="rule">Which rule.</param>
    /// <remarks>
    /// <strong>The administrator's sentence, not this build's.</strong> A generic message with
    /// the rule's name in it would make every one of these read the same, which is exactly what
    /// the <c>message</c> column exists to avoid.
    /// </remarks>
    public static Error RuleRefusedIt(ValidationRuleRow rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new Error("crm.custom_validation_failed", rule.Message, ErrorCategory.Validation)
            .With("rule", rule.Name)
            .With("field", rule.Field);
    }

    /// <summary>A rule was declared over a field nothing has.</summary>
    /// <param name="field">What was named.</param>
    /// <param name="nameable">What may be named.</param>
    /// <remarks>
    /// Refused when the rule is declared rather than when a record first trips it — the same
    /// property <see cref="ProcessPublishing.Validate"/> holds for a guard, and for the same
    /// reason: a rule that silently never fires is worse than one that never existed.
    /// </remarks>
    public static Error RuleNamesNoField(string field, IEnumerable<string> nameable) =>
        new Error(
            "crm.custom_rule_field_not_declared",
            $"'{field}' is not a field a rule may name.",
            ErrorCategory.Validation)
            .With("field", field)
            .With("nameable", string.Join(", ", nameable.Order(StringComparer.Ordinal)));

    /// <summary>An ordering rule was given a bound that is not a number.</summary>
    /// <param name="op">Which operator.</param>
    /// <param name="value">What was sent.</param>
    public static Error RuleBoundIsNotNumeric(GuardOperator op, string value) =>
        new Error(
            "crm.custom_rule_bound_not_numeric",
            $"'{op}' compares numbers, and '{value}' is not one.",
            ErrorCategory.Validation)
            .With("operator", op.ToString())
            .With("value", value);

    /// <summary>The caller does not hold the grant this field requires.</summary>
    /// <param name="field">Which field.</param>
    /// <param name="permission">What it requires.</param>
    /// <remarks>
    /// <c>Forbidden</c> and it names the scope, because the caller can do something about that —
    /// ask for it. Hiding which grant is missing turns a permissions question into a support
    /// ticket.
    /// </remarks>
    public static Error FieldRequiresAGrant(string field, string permission) =>
        new Error(
            "crm.custom_field_forbidden",
            $"Writing '{field}' requires the '{permission}' grant.",
            ErrorCategory.Forbidden)
            .With("field", field)
            .With("permission", permission);

    /// <summary>Another record already holds that value in a field declared unique.</summary>
    /// <param name="field">Which field.</param>
    /// <param name="value">What was sent.</param>
    public static Error ValueIsNotUnique(string field, string value) =>
        new Error(
            "crm.custom_value_not_unique",
            $"'{field}' must be unique and another record already holds that value.",
            ErrorCategory.Conflict)
            .With("field", field)
            .With("value", value);
}

// ------------------------------------------------------------------------------ the pure rules

/// <summary>
/// Whether a set of values trips a rule, and whether a caller may write a field — both as pure
/// functions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here reads a database or a clock</strong>, for the reason
/// <see cref="ProcessRules"/> gives: the rules and the declarations are read once and every value
/// is checked against the same snapshot, so two rules cannot disagree about what was declared.
/// </para>
/// <para>
/// <strong>A rule refuses when its condition holds.</strong> That is Salesforce's polarity and it
/// is the useful one: an administrator writes down what is wrong, not the negation of everything
/// that is right. <c>amount &gt; 100000</c> as a rule means "over a hundred thousand is not
/// allowed here", which is the sentence somebody can check.
/// </para>
/// </remarks>
public static class CustomFieldPolicy
{
    /// <summary>The first rule these values trip, or null when they trip none.</summary>
    /// <param name="rules">The active rules for the entity.</param>
    /// <param name="values">What is being written, by field name.</param>
    /// <param name="existing">
    /// What the entity already holds, for a partial update. A rule over a field the caller did
    /// not mention is evaluated against what is already there — otherwise setting one field
    /// would be refused by a rule about another, which no administrator intends.
    /// </param>
    /// <returns>The error, or null.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <strong>One rule reported, not all of them.</strong> The first is what the caller has to
    /// fix, and a list would still be read top-down — while a caller acting on a list of five
    /// would fix all five and discover the sixth.
    /// </remarks>
    public static Error? FirstViolation(
        IEnumerable<ValidationRuleRow> rules,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var rule in rules)
        {
            var actual = values.TryGetValue(rule.Field, out var sent)
                ? sent
                : existing is not null && existing.TryGetValue(rule.Field, out var held)
                    ? held
                    : null;

            // ProcessRules.Holds, not a second copy of the switch: a rule and a guard are the
            // same five operators over the same text, and two implementations would drift the
            // first time somebody fixed one of them.
            if (ProcessRules.Holds(rule.Operator, rule.Value, actual))
            {
                return FieldPolicyErrors.RuleRefusedIt(rule);
            }
        }

        return null;
    }

    /// <summary>The first field this build computes, or null when none of them is computed.</summary>
    /// <param name="declared">The fields declared for the entity, by name.</param>
    /// <param name="values">What is being written.</param>
    /// <returns>The error, or null.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <strong>Refused rather than ignored, and checked before the grants.</strong> A computed
    /// field a caller may write is a lie — the next recompute overwrites it, so the write appears
    /// to succeed and silently does nothing. "Nothing may write this" is also a better answer
    /// than "you may not" to somebody nobody could have permitted.
    /// </remarks>
    public static Error? FirstComputedField(
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var name in values.Keys)
        {
            if (declared.TryGetValue(name, out var field) && field.IsComputed)
            {
                return RollupErrors.FieldIsComputed(name);
            }
        }

        return null;
    }

    /// <summary>The first field this caller may not write, or null when they may write them all.</summary>
    /// <param name="declared">The fields declared for the entity, by name.</param>
    /// <param name="values">What is being written.</param>
    /// <param name="scopes">The grants the caller holds, read off their claims by the flow.</param>
    /// <returns>The error, or null.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Write only, and the column says so too.</strong> Withholding a field from a
    /// <em>reader</em> means filtering it out of every projection that carries it, and the jsonb
    /// column travels with its row — a read rule not applied everywhere is a read rule that leaks
    /// the first time somebody adds a projection. Restricting the write is one check at one door.
    /// Read-side masking is a real feature and this is not it.
    /// </para>
    /// <para>
    /// <strong>The scopes come off the claims the trigger authenticated</strong>, exactly as the
    /// capability stance's own permission check does, and off nothing in the body.
    /// </para>
    /// </remarks>
    public static Error? FirstForbiddenField(
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(scopes);

        var held = new HashSet<string>(scopes, StringComparer.Ordinal);

        foreach (var name in values.Keys)
        {
            if (declared.TryGetValue(name, out var field) &&
                field.RequiredPermission is { Length: > 0 } permission &&
                !held.Contains(permission))
            {
                return FieldPolicyErrors.FieldRequiresAGrant(name, permission);
            }
        }

        return null;
    }

    /// <summary>What a redacted value is replaced with.</summary>
    /// <remarks>
    /// A placeholder rather than an omitted key, and <c>ProblemDetailsMapper.Redacted</c> makes
    /// the same choice for the same reason: a caller who cannot tell a withheld field from an
    /// unset one cannot tell a permissions problem from a data problem, and will chase the wrong
    /// one. The key is present, the value says why it is not.
    /// </remarks>
    public const string Redacted = "[redacted]";

    /// <summary>
    /// One record's values, with anything this caller may not read replaced.
    /// </summary>
    /// <param name="declared">The fields declared for the object, by name.</param>
    /// <param name="values">What the row holds.</param>
    /// <param name="scopes">The grants the caller holds.</param>
    /// <param name="redacted">Collects the names withheld, so the caller can be told which.</param>
    /// <returns>What the caller may see.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>This is the only door.</strong> Migration <c>0007</c> argued that a read rule not
    /// applied to every projection is a read rule that leaks, and it was right — the answer is
    /// that there is one projection of custom values and this is it. A second one masks by
    /// calling this or it leaks; there is no third option, and that is why this is a function
    /// rather than three lines inside the query capability.
    /// </para>
    /// <para>
    /// <strong>It is not applied where a rule is evaluated.</strong>
    /// <c>CustomFieldPolicy.FirstViolation</c> reads the row as it is, because a validation rule
    /// that could be defeated by not holding a grant would be a rule anybody could switch off.
    /// Masking is what a caller sees, never what the application decides on.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string?> Mask(
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<string> scopes,
        ISet<string> redacted)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(redacted);

        var held = new HashSet<string>(scopes, StringComparer.Ordinal);
        var visible = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            if (declared.TryGetValue(name, out var field) &&
                field.ReadPermission is { Length: > 0 } permission &&
                !held.Contains(permission))
            {
                visible[name] = Redacted;
                redacted.Add(name);

                continue;
            }

            visible[name] = value;
        }

        return visible;
    }

    /// <summary>The scopes a caller holds, for a flow to project onto a capability's input.</summary>
    /// <param name="principal">Whoever the trigger authenticated, or null.</param>
    /// <returns>The scopes, empty when nobody is calling.</returns>
    /// <remarks>
    /// Space-delimited, because that is what an OAuth 2.0 access token carries — the same reading
    /// <c>CrmTokenHandler</c> writes and the engine's own permission check makes.
    /// </remarks>
    public static IReadOnlyList<string> Scopes(ClaimsPrincipal? principal)
    {
        var scopes = new List<string>();

        foreach (var claim in principal?.FindAll("scope") ?? [])
        {
            foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                scopes.Add(scope);
            }
        }

        return scopes;
    }
}
