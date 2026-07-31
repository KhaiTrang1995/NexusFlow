using System.Text.RegularExpressions;

namespace FlowX.Postgres;

/// <summary>
/// Guards the one value in this adapter that reaches SQL as an identifier rather than as a
/// parameter.
/// </summary>
internal static partial class Identifiers
{
    /// <summary>The longest identifier PostgreSQL stores without truncating it.</summary>
    private const int MaximumLength = 63;

    /// <summary>
    /// Returns the schema name, or throws if it is anything other than a bare lower-case
    /// identifier.
    /// </summary>
    /// <param name="schema">The configured schema name.</param>
    /// <exception cref="ArgumentException">The value is not a bare identifier.</exception>
    /// <remarks>
    /// Lower-case only, deliberately. An unquoted identifier is folded to lower case by the
    /// server while a quoted one is not, so <c>FlowX</c> and <c>flowx</c> are the same
    /// schema in one statement and two in another. Refusing the ambiguous spelling is
    /// cheaper than being right about it everywhere.
    /// </remarks>
    public static string RequireSchemaName(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        if (schema.Length > MaximumLength || !SchemaName().IsMatch(schema))
        {
            throw new ArgumentException(
                $"'{schema}' is not a usable schema name. Expected a bare lower-case " +
                $"identifier of at most {MaximumLength} characters — letters, digits and " +
                "underscores, not starting with a digit — so that it means the same thing " +
                "quoted and unquoted.",
                nameof(schema));
        }

        return schema;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}
