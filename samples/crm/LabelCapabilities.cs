using FlowX;

namespace Crm;

/// <summary>
/// Renames one thing for one tenant, without renaming it for anybody else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The identifier never moves.</strong> A rename changes the label and nothing else: the
/// object's name, the field's name and the entity's kind stay what they were, because they are in
/// saved views, in guards, in roll-up filters, in jsonb keys and in whatever a client cached last
/// week. A settings screen that renamed the identifier would be a settings screen that silently
/// broke every one of those.
/// </para>
/// <para>
/// <strong><c>crm.admin</c>.</strong> Renaming what everybody in a tenant sees is not a thing one
/// representative does on their own screen.
/// </para>
/// </remarks>
[Capability("crm.label.set", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class SetCrmLabel : ICapability<SetLabel, LabelSet>
{
    private readonly LabelStore _labels;

    /// <summary>Creates the capability.</summary>
    /// <param name="labels">Writes the label.</param>
    /// <exception cref="ArgumentNullException"><paramref name="labels"/> is null.</exception>
    public SetCrmLabel(LabelStore labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        _labels = labels;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LabelSet>> ExecuteAsync(
        SetLabel input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var subjects = (input.Target is null ? 0 : 1)
            + (input.Field is null ? 0 : 1)
            + (input.Entity is null ? 0 : 1);

        if (subjects != 1)
        {
            return Result.Fail<LabelSet>(LabelErrors.NameOneSubject());
        }

        if (input.Column is { Length: > 0 } && input.Entity is null)
        {
            return Result.Fail<LabelSet>(LabelErrors.ColumnNeedsItsEntity());
        }

        if (input.Label is not { Length: > 0 and <= LabelLimits.MaxLength })
        {
            return Result.Fail<LabelSet>(LabelErrors.LabelIsNotUsable(input.Label ?? string.Empty));
        }

        if (input.Entity is { } kind)
        {
            var column = input.Column ?? LabelLimits.TheEntityItself;

            // Checked against the closed list, so a settings screen cannot accumulate labels for
            // columns that do not exist — rows nothing reads and nobody knows to delete.
            if (column.Length > 0
                && !EntityColumns.Of(kind).Contains(column, StringComparer.Ordinal))
            {
                return Result.Fail<LabelSet>(LabelErrors.ColumnIsNotOfEntity(kind, column));
            }

            await _labels
                .SetEntityLabelAsync(ctx.TenantId, kind, column, input.Label, ct)
                .ConfigureAwait(false);

            return Result.Ok(new LabelSet(input.Label));
        }

        var renamed = await _labels
            .RenameAsync(
                ctx.TenantId,
                input.Target ?? input.Field!.Value,
                input.Target is not null,
                input.Label,
                ct)
            .ConfigureAwait(false);

        return renamed
            ? Result.Ok(new LabelSet(input.Label))
            : Result.Fail<LabelSet>(LabelErrors.SubjectNotFound());
    }
}
