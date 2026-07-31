using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// Turns a reader's current row into the record the journal contract hands back.
/// </summary>
/// <remarks>
/// Ordinals rather than names, matched to the column lists in <see cref="JournalSql"/>.
/// Reading by name costs a lookup on every column of every row of a resume scan, which is
/// the read budget B8 is a budget on. The pairing is only safe because both halves are in
/// this one folder and neither is assembled at run time.
/// </remarks>
internal static class JournalRows
{
    /// <summary>Reads a <c>flow_instance</c> row.</summary>
    public static FlowInstanceRecord Instance(NpgsqlDataReader reader) => new()
    {
        InstanceId = reader.GetGuid(0),
        FlowId = reader.GetString(1),
        FlowVersion = reader.GetString(2),
        TenantId = Db.NullableString(reader, 3),
        State = StoredEnums.ToInstanceState(reader.GetString(4)),
        Fence = new FencingToken(reader.GetInt64(5)),
        ResumeHint = Db.NullableInt(reader, 6),
        InputJson = Db.NullableString(reader, 7),
        StateBagJson = Db.NullableString(reader, 8),
        CorrelationId = reader.GetString(9),
        TraceId = Db.NullableString(reader, 10),
        DeadlineAt = Db.NullableTimestamp(reader, 11),
        ParentInstanceId = Db.NullableGuid(reader, 12),
        ParentScope = StepScope.Parse(reader.GetString(13)),
        ParentStepId = Db.NullableInt(reader, 14),
        CreatedAt = Db.ReadTimestamp(reader, 15),
        UpdatedAt = Db.ReadTimestamp(reader, 16),
    };

    /// <summary>Reads a <c>flow_step</c> row.</summary>
    /// <param name="reader">The reader, positioned on the row.</param>
    /// <param name="instanceId">
    /// The instance, supplied rather than selected: every row of the query belongs to the
    /// instance the query asked about, so returning the column would be a value per row for
    /// a fact stated once.
    /// </param>
    public static JournalStep Step(NpgsqlDataReader reader, Guid instanceId) => new()
    {
        Key = new StepKey(
            instanceId,
            StepScope.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetInt32(2)),
        Sequence = reader.GetInt64(3),
        CapabilityId = reader.GetString(4),
        CapabilityVersion = reader.GetString(5),
        Outcome = StoredEnums.ToOutcome(reader.GetString(6)),
        ResultJson = Db.NullableString(reader, 7),
        Nondeterminism = NondeterminismJson.FromJson(Db.NullableString(reader, 8)),
        Duration = TimeSpan.FromMilliseconds(reader.GetInt64(9)),
        CommittedAt = Db.ReadTimestamp(reader, 10),
    };

    /// <summary>Reads an <c>outbox_event</c> row.</summary>
    public static OutboxRecord Outbox(NpgsqlDataReader reader) => new()
    {
        EventId = reader.GetGuid(0),
        InstanceId = reader.GetGuid(1),
        Type = reader.GetString(2),
        SchemaVersion = reader.GetString(3),
        PartitionKey = Db.NullableString(reader, 4),
        PayloadJson = Db.NullableString(reader, 5),
        PublishedAt = Db.NullableTimestamp(reader, 6),
    };
}
