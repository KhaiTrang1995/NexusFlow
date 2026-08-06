using System.Globalization;
using FlowX;

namespace Crm;

/// <summary>
/// Declares a rule that refuses a record.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>crm.admin</c>, because a rule decides what every future write of every flow may
/// say.</strong> A representative who could declare one could stop their colleagues working.
/// </para>
/// <para>
/// <strong>The field is checked here, when the rule is declared.</strong> That is the same
/// property <see cref="ProcessPublishing.Validate"/> holds for a transition guard and it is the
/// whole reason both are worth having: a rule naming a field nobody declared would silently never
/// fire, which is worse than one that never existed.
/// </para>
/// </remarks>
[Capability("crm.custom.define_validation_rule", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class DefineCustomValidationRule
    : ICapability<DefineValidationRule, ValidationRuleDefined>
{
    private readonly CustomSchemaStore _schema;
    private readonly FieldPolicyStore _policy;

    /// <summary>Creates the capability.</summary>
    /// <param name="schema">Reads what may be named.</param>
    /// <param name="policy">Writes the rule.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DefineCustomValidationRule(CustomSchemaStore schema, FieldPolicyStore policy)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(policy);

        _schema = schema;
        _policy = policy;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ValidationRuleDefined>> ExecuteAsync(
        DefineValidationRule input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.AppliesTo is null == input.Target is null)
        {
            return Result.Fail<ValidationRuleDefined>(CustomSchemaErrors.FieldHasNoOwner());
        }

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<ValidationRuleDefined>(
                CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        // An ordering operator with a bound that is not a number would compare 0 to 0 and never
        // hold — the same fault ProcessPublishing.Validate refuses, refused at the same moment.
        if (ProcessRules.IsNumeric(input.Operator) &&
            !decimal.TryParse(input.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            return Result.Fail<ValidationRuleDefined>(
                FieldPolicyErrors.RuleBoundIsNotNumeric(input.Operator, input.Value));
        }

        var nameable = await NameableAsync(input, ctx, ct).ConfigureAwait(false);

        if (!nameable.Contains(input.Field))
        {
            return Result.Fail<ValidationRuleDefined>(
                FieldPolicyErrors.RuleNamesNoField(input.Field, nameable));
        }

        var id = await _policy
            .DeclareRuleAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is null
            ? Result.Fail<ValidationRuleDefined>(CustomSchemaErrors.NameIsTaken(input.Name))
            : Result.Ok(new ValidationRuleDefined(id.Value, input.Name));
    }

    /// <summary>What a rule on this owner may name.</summary>
    /// <remarks>
    /// An opportunity's rule may name the six built-in process fields as well as the declared
    /// ones, because those are what <see cref="ProcessFacts"/> already exposes and an
    /// administrator who can guard on <c>amount</c> expects to be able to validate on it. A
    /// custom object has no built-in fields, so only its own.
    /// </remarks>
    private async ValueTask<IReadOnlySet<string>> NameableAsync(
        DefineValidationRule input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        if (input.Target is { } target)
        {
            var declared = await _schema.FieldsForAsync(ctx.TenantId, target, ct).ConfigureAwait(false);

            return new HashSet<string>(declared.Keys, StringComparer.Ordinal);
        }

        var forEntity = await _schema
            .FieldsForAsync(ctx.TenantId, input.AppliesTo!.Value, ct)
            .ConfigureAwait(false);

        return input.AppliesTo == EntityKind.Opportunity
            ? ProcessFields.Including(forEntity)
            : new HashSet<string>(forEntity.Keys, StringComparer.Ordinal);
    }
}
