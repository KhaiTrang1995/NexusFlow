using System.Globalization;
using FlowX;

namespace Crm;

/// <summary>
/// Declares a field computed from other fields of the same record.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every check here happens when the formula is declared</strong>, because a formula that
/// reads a field nobody has evaluates to null on every row for ever — and a column of blanks
/// reads exactly like a column nobody has filled in. Nobody investigates a blank.
/// </para>
/// <para>
/// <strong>An operand may not itself be computed.</strong> That is what keeps evaluation a single
/// pass with no dependency graph and therefore no possibility of a cycle; the cost is that
/// <c>(a + b) * c</c> is two declarations, and the benefit is that the intermediate has a name
/// somebody can query.
/// </para>
/// </remarks>
[Capability("crm.custom.define_formula", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class DefineCustomFormula : ICapability<DefineFormula, FormulaDefined>
{
    private readonly CustomSchemaStore _schema;
    private readonly RollupStore _rollups;
    private readonly FormulaStore _formulas;

    /// <summary>Creates the capability.</summary>
    /// <param name="schema">Reads what the owner declared, for the operands.</param>
    /// <param name="rollups">Reads which object a field belongs to, and whether it is computed.</param>
    /// <param name="formulas">Writes the declaration.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DefineCustomFormula(
        CustomSchemaStore schema,
        RollupStore rollups,
        FormulaStore formulas)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rollups);
        ArgumentNullException.ThrowIfNull(formulas);

        _schema = schema;
        _rollups = rollups;
        _formulas = formulas;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FormulaDefined>> ExecuteAsync(
        DefineFormula input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Right is null == input.Literal is null)
        {
            return Result.Fail<FormulaDefined>(FormulaErrors.OperandIsAmbiguous());
        }

        if (Formulas.IsArithmetic(input.Operation) &&
            input.Literal is { } literal &&
            !decimal.TryParse(literal, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            return Result.Fail<FormulaDefined>(
                FormulaErrors.LiteralIsNotNumeric(input.Operation, literal));
        }

        if (await _rollups.FieldOwnerAsync(ctx.TenantId, input.Field, ct).ConfigureAwait(false)
            is not { } field)
        {
            return Result.Fail<FormulaDefined>(RollupErrors.FieldNotFound(input.Field));
        }

        if (field.IsComputed)
        {
            return Result.Fail<FormulaDefined>(RollupErrors.FieldIsAlreadyComputed(input.Field));
        }

        // A formula belongs to a custom object in this build. The built-in entities carry custom
        // fields too, and a formula over those is the same machinery with a different owner read;
        // it is not built, and refusing it here beats declaring one that never runs.
        if (field.Object is not { } owner)
        {
            return Result.Fail<FormulaDefined>(RollupErrors.FieldIsNotOnTheParent(input.Field));
        }

        var declared = await _schema.FieldsForAsync(ctx.TenantId, owner, ct).ConfigureAwait(false);

        foreach (var operand in new[] { input.Left, input.Right })
        {
            if (operand is null)
            {
                continue;
            }

            if (!declared.TryGetValue(operand, out var row))
            {
                return Result.Fail<FormulaDefined>(FormulaErrors.OperandNotDeclared(operand));
            }

            if (row.IsComputed)
            {
                return Result.Fail<FormulaDefined>(FormulaErrors.OperandIsComputed(operand));
            }
        }

        var id = await _formulas
            .DeclareAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is null
            ? Result.Fail<FormulaDefined>(RollupErrors.FieldIsAlreadyComputed(input.Field))
            : Result.Ok(new FormulaDefined(id.Value, input.Field));
    }
}
