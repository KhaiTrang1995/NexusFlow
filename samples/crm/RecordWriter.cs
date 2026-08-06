using FlowX;

namespace Crm;

/// <summary>
/// Everything that has to be true before a custom record is stored, in the order it has to be
/// asked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Extracted when the bulk import needed it.</strong> There are now two ways a record
/// gets written — one at a time over HTTP, and ten thousand at a time by the job sweep — and a
/// second copy of seven rules is a second copy that drifts. The import would have been the copy
/// that forgot field-level security, because nobody writes an importer thinking about who is
/// allowed to fill in a column.
/// </para>
/// <para>
/// <strong>A function and not a capability.</strong> A capability calling a capability is what
/// <c>CapabilitiesDoNotCallCapabilities</c> refuses, and rightly: two authorisation stances on
/// one call is one stance nobody can name. The rules live here, the stance stays on each caller.
/// </para>
/// <para>
/// <strong>The id is the caller's.</strong> <see cref="CreateCustomRecord"/> mints one per
/// request; the import derives one from the job and the row's position, so re-running a chunk
/// after a crash writes the same ids and the insert is a no-op. Deciding it here would take that
/// choice away from the caller that needs it.
/// </para>
/// </remarks>
public static class RecordWriter
{
    /// <summary>Writes one record, or says why it was refused.</summary>
    /// <param name="store">Reads the declarations and writes the row.</param>
    /// <param name="policy">Reads the rules, and claims the unique values.</param>
    /// <param name="formulas">Reads the formulas whose answers this write computes.</param>
    /// <param name="id">What the record is to be called.</param>
    /// <param name="target">Which object it belongs to.</param>
    /// <param name="declared">Its object's fields, read once by the caller.</param>
    /// <param name="values">What was sent.</param>
    /// <param name="scopes">The grants the writer holds.</param>
    /// <param name="ctx">The invocation, for its tenant and its clock.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The refusal, or null when the record was written.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static async ValueTask<Error?> WriteAsync(
        CustomSchemaStore store,
        FieldPolicyStore policy,
        FormulaStore formulas,
        Guid id,
        Guid target,
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<string> scopes,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(ctx);

        // A whole record, so a required field with no value is a fault. Reported one at a time:
        // the first is what the caller has to fix, and a list of every fault would still be read
        // top-down.
        if (CustomValues.Validate(declared, values, requireComplete: true) is { Count: > 0 } faults)
        {
            return faults[0];
        }

        // The half Validate cannot answer: whether a reference points at a record this tenant
        // has, of the object the field names.
        if (await CustomReferences
                .UnresolvableAsync(store, declared, values, ctx, ct)
                .ConfigureAwait(false) is { } dangling)
        {
            return dangling;
        }

        // Field-level security before the rules, because "you may not write this" is a better
        // answer than "what you wrote is wrong" to somebody who was never allowed to write it.
        if (CustomFieldPolicy.FirstComputedField(declared, values) is { } computed)
        {
            return computed;
        }

        if (CustomFieldPolicy.FirstForbiddenField(declared, values, scopes) is { } forbidden)
        {
            return forbidden;
        }

        // Computed before the rules and after the type check, which is the only order that works:
        // a formula reads values that have been checked, and a rule must be able to refuse what a
        // formula produced.
        var declaredFormulas = await formulas
            .FormulasForAsync(ctx.TenantId, target, ct)
            .ConfigureAwait(false);

        var written = Formulas.Apply(declaredFormulas, values);

        var rules = await policy.RulesForAsync(ctx.TenantId, target, ct).ConfigureAwait(false);

        if (CustomFieldPolicy.FirstViolation(rules, written) is { } refused)
        {
            return refused;
        }

        // Claimed before the record is written, because the other order lets two writers both see
        // a free value. A claim left behind by a failed write refuses a later writer, which is the
        // safe direction — hence the release below rather than nothing.
        if (await policy.ClaimAsync(ctx.TenantId, id, declared, written, ct).ConfigureAwait(false)
            is { } taken)
        {
            await policy.ReleaseAsync(ctx.TenantId, id, ct).ConfigureAwait(false);

            return FieldPolicyErrors.ValueIsNotUnique(taken, written[taken]!);
        }

        try
        {
            await store
                .WriteRecordAsync(
                    ctx.TenantId, id, target, CustomValues.ToJson(declared, written),
                    ctx.UtcNow, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            await policy.ReleaseAsync(ctx.TenantId, id, ct).ConfigureAwait(false);

            throw;
        }

        return null;
    }
}
