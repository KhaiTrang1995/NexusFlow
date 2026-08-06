using System.Globalization;
using FlowX;

namespace Crm;

/// <summary>What a formula does to its two operands.</summary>
/// <remarks>
/// <strong>Six, and each is a total function.</strong> Closed for the reason every other
/// vocabulary in this sample is: an open set would be a string nothing dispatches on. Each has an
/// answer for a missing operand, which is stated at the arm that gives it — a formula that threw
/// on an unset field would make one blank cell fail the whole write.
/// </remarks>
public enum FormulaOperation
{
    /// <summary>Joins the two as text.</summary>
    Concat = 0,

    /// <summary>Adds them as numbers.</summary>
    Add = 1,

    /// <summary>Subtracts the right from the left.</summary>
    Subtract = 2,

    /// <summary>Multiplies them.</summary>
    Multiply = 3,

    /// <summary>Divides the left by the right.</summary>
    Divide = 4,

    /// <summary>The left, or the right when the left is not set.</summary>
    Coalesce = 5,
}

/// <summary>Declares a field computed from other fields of the same record.</summary>
/// <param name="Field">The field that holds the answer. Becomes read-only.</param>
/// <param name="Operation">What to do to the operands.</param>
/// <param name="Left">The left operand. Always a field of the same owner.</param>
/// <param name="Right">The right operand as a field, or null when <paramref name="Literal"/> is given.</param>
/// <param name="Literal">The right operand as a constant, or null when <paramref name="Right"/> is given.</param>
public sealed record DefineFormula(
    Guid Field,
    FormulaOperation Operation,
    string Left,
    string? Right,
    string? Literal);

/// <summary>The formula that was declared.</summary>
/// <param name="FormulaId">Its id.</param>
/// <param name="Field">The field it computes.</param>
public sealed record FormulaDefined(Guid FormulaId, Guid Field);

/// <summary>A declared formula, as much of it as evaluating one needs.</summary>
/// <param name="Id">The formula.</param>
/// <param name="FieldName">The field that holds the answer.</param>
/// <param name="Operation">What to do.</param>
/// <param name="Left">The left operand's field name.</param>
/// <param name="Right">The right operand's field name, or null.</param>
/// <param name="Literal">The right operand's constant, or null.</param>
public sealed record FormulaRow(
    Guid Id,
    string FieldName,
    FormulaOperation Operation,
    string Left,
    string? Right,
    string? Literal);

/// <summary>Refusals the formula machinery can produce.</summary>
public static class FormulaErrors
{
    /// <summary>The right operand was given as a field and a constant, or as neither.</summary>
    public static Error OperandIsAmbiguous() =>
        new(
            "crm.formula_operand_ambiguous",
            "A formula's right operand is a field or a constant, and this named neither or both.",
            ErrorCategory.Validation);

    /// <summary>An operand is not a field of the same owner.</summary>
    /// <param name="field">What was named.</param>
    public static Error OperandNotDeclared(string field) =>
        new Error(
            "crm.formula_operand_not_declared",
            $"'{field}' is not a field of the object this formula's field belongs to.",
            ErrorCategory.Validation)
            .With("field", field);

    /// <summary>An operand is itself computed.</summary>
    /// <param name="field">What was named.</param>
    /// <remarks>
    /// <strong>Refused so that evaluation stays a single pass.</strong> A formula over a formula
    /// needs an order to evaluate them in, which is a dependency graph, which can hold a cycle.
    /// An administrator who wants one declares the intermediate and reads it — which is better
    /// than a nested expression, because the intermediate is nameable, queryable and checkable.
    /// </remarks>
    public static Error OperandIsComputed(string field) =>
        new Error(
            "crm.formula_operand_is_computed",
            $"'{field}' is itself computed, and a formula may not read another one. Declare the " +
            "intermediate field and read that.",
            ErrorCategory.Validation)
            .With("field", field);

    /// <summary>An arithmetic formula was given a constant that is not a number.</summary>
    /// <param name="operation">Which operation.</param>
    /// <param name="literal">What was sent.</param>
    public static Error LiteralIsNotNumeric(FormulaOperation operation, string literal) =>
        new Error(
            "crm.formula_literal_not_numeric",
            $"'{operation}' works on numbers, and '{literal}' is not one.",
            ErrorCategory.Validation)
            .With("operation", operation.ToString())
            .With("literal", literal);
}

/// <summary>
/// Evaluating a formula — a pure function of one record's values.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here reads a database, a clock or another formula.</strong> The declarations
/// are read once and every formula is evaluated against the same snapshot of the caller's values,
/// in one pass and in no particular order, which is exactly what refusing a formula over a
/// formula buys.
/// </para>
/// <para>
/// <strong>Every arm answers for a missing operand.</strong> A formula that threw on an unset
/// field would make one blank cell fail a whole write, and a formula that silently produced zero
/// would make an empty column read as a real number. Null is the answer where there is nothing
/// to compute, which stores as JSON null and reads back as unset.
/// </para>
/// </remarks>
public static class Formulas
{
    /// <summary>What a formula evaluates to over these values.</summary>
    /// <param name="formula">The declaration.</param>
    /// <param name="values">The record's values, by field name.</param>
    /// <returns>The answer, or null when there is nothing to compute.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static string? Evaluate(
        FormulaRow formula,
        IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(values);

        var left = values.GetValueOrDefault(formula.Left);
        var right = formula.Literal ?? values.GetValueOrDefault(formula.Right!);

        return formula.Operation switch
        {
            // Coalesce is the one operation with an answer when the left is missing — that is
            // what it is for — so it comes before the shared guard below.
            FormulaOperation.Coalesce => left ?? right,

            // Text, and a missing operand contributes nothing rather than the word "null".
            FormulaOperation.Concat when left is not null || right is not null =>
                (left ?? string.Empty) + (right ?? string.Empty),

            FormulaOperation.Add => Arithmetic(left, right, static (a, b) => a + b),
            FormulaOperation.Subtract => Arithmetic(left, right, static (a, b) => a - b),
            FormulaOperation.Multiply => Arithmetic(left, right, static (a, b) => a * b),

            // Division by zero is null rather than an exception, for the reason the class remarks
            // give: a formula must not be able to fail a write. A row whose divisor is zero has
            // no answer, and saying so is what a blank cell means.
            FormulaOperation.Divide => Arithmetic(
                left, right, static (a, b) => b == 0m ? null : a / b),

            _ => null,
        };
    }

    /// <summary>Whether an operation works on numbers.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>Whether both operands have to parse.</returns>
    public static bool IsArithmetic(FormulaOperation operation) =>
        operation is FormulaOperation.Add
            or FormulaOperation.Subtract
            or FormulaOperation.Multiply
            or FormulaOperation.Divide;

    /// <summary>Every formula's answer, merged over the values it was given.</summary>
    /// <param name="formulas">The declarations for the entity.</param>
    /// <param name="values">What is being written.</param>
    /// <returns>The values, with every computed field set.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <strong>Read from the input, written to the copy.</strong> Every formula sees the caller's
    /// values and none sees another's answer, which is what makes the order they are evaluated in
    /// irrelevant — and therefore what makes "no formula over a formula" a rule with teeth rather
    /// than a convention.
    /// </remarks>
    public static IReadOnlyDictionary<string, string?> Apply(
        IEnumerable<FormulaRow> formulas,
        IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(values);

        var computed = new Dictionary<string, string?>(values, StringComparer.Ordinal);

        foreach (var formula in formulas)
        {
            computed[formula.FieldName] = Evaluate(formula, values);
        }

        return computed;
    }

    private static string? Arithmetic(
        string? left,
        string? right,
        Func<decimal, decimal, decimal?> apply)
    {
        if (!decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ||
            !decimal.TryParse(right, NumberStyles.Number, CultureInfo.InvariantCulture, out var b))
        {
            return null;
        }

        return apply(a, b)?.ToString(CultureInfo.InvariantCulture);
    }
}
