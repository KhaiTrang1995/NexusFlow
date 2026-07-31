using Npgsql;
using NpgsqlTypes;

namespace FlowX.Postgres;

/// <summary>
/// Builds parameters with their PostgreSQL type stated, and reads columns back with the
/// nullability stated.
/// </summary>
/// <remarks>
/// <para>
/// Every parameter declares its <see cref="NpgsqlDbType"/> rather than letting the driver
/// infer one from the CLR value. Inference is where the trim- and AOT-unsafe half of a data
/// access library lives, and it is also where a null becomes untyped and the server starts
/// guessing — <c>COALESCE(@x, column)</c> cannot be planned against a parameter whose type
/// nobody stated. Constraint C2 and the correctness of the statements are the same
/// requirement here.
/// </para>
/// <para>
/// The read helpers exist because <c>IsDBNull</c> followed by a typed accessor is three
/// lines that are easy to write twice and easy to get backwards once.
/// </para>
/// </remarks>
internal static class Db
{
    /// <summary>A <c>uuid</c> parameter.</summary>
    public static NpgsqlParameter Uuid(string name, Guid value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value };

    /// <summary>A nullable <c>uuid</c> parameter.</summary>
    public static NpgsqlParameter Uuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = Boxed(value) };

    /// <summary>A <c>text</c> parameter.</summary>
    public static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    /// <summary>A <c>json</c> parameter, carrying a document already rendered as text.</summary>
    /// <remarks>
    /// <c>json</c> rather than <c>jsonb</c>, and the value is passed as a string rather
    /// than as an object. Both halves are what keeps the payload byte-identical to what the
    /// generated <c>System.Text.Json</c> context produced: <c>jsonb</c> would re-render it
    /// and an object parameter would re-serialise it through the driver's own reflection.
    /// </remarks>
    public static NpgsqlParameter Json(string name, string? value) =>
        new(name, NpgsqlDbType.Json) { Value = (object?)value ?? DBNull.Value };

    /// <summary>An <c>int</c> parameter.</summary>
    public static NpgsqlParameter Int(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };

    /// <summary>A nullable <c>int</c> parameter.</summary>
    public static NpgsqlParameter Int(string name, int? value) =>
        new(name, NpgsqlDbType.Integer) { Value = Boxed(value) };

    /// <summary>A <c>bigint</c> parameter.</summary>
    public static NpgsqlParameter Long(string name, long value) =>
        new(name, NpgsqlDbType.Bigint) { Value = value };

    /// <summary>A nullable <c>timestamptz</c> parameter.</summary>
    public static NpgsqlParameter Timestamp(string name, DateTimeOffset? value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = Boxed(value) };

    /// <summary>An <c>interval</c> parameter.</summary>
    public static NpgsqlParameter Interval(string name, TimeSpan value) =>
        new(name, NpgsqlDbType.Interval) { Value = value };

    /// <summary>
    /// Whether a column on the current row is null.
    /// </summary>
    /// <remarks>
    /// The read helpers are deliberately synchronous, and calling them from an asynchronous
    /// method is not the blocking call it looks like. Npgsql buffers the whole row during
    /// <c>ReadAsync</c> unless the command was issued with
    /// <c>CommandBehavior.SequentialAccess</c>, which nothing here does, so every accessor
    /// below reads out of memory. Keeping them in one sync surface is also what keeps the
    /// I/O and the row interpretation separable.
    /// </remarks>
    public static bool IsNull(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal);

    /// <summary>Reads a <c>timestamptz</c> column that the schema declares NOT NULL.</summary>
    public static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.GetFieldValue<DateTimeOffset>(ordinal);

    /// <summary>Reads a column that the schema allows to be null.</summary>
    public static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Reads a nullable <c>int</c> column.</summary>
    public static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    /// <summary>Reads a nullable <c>uuid</c> column.</summary>
    public static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    /// <summary>Reads a nullable <c>timestamptz</c> column.</summary>
    public static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader, ordinal);

    private static object Boxed<T>(T? value)
        where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;
}
