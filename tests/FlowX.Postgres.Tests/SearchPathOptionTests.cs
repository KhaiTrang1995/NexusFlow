using Npgsql;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// Whether the adapter puts the schema on the connection, and whether it can be told not to.
/// </summary>
/// <remarks>
/// Needs no database: the question is what the built connection string says, and that is
/// decided before anything is opened. The behaviour it guards is not cosmetic — a startup
/// parameter is exactly what a transaction pooler cannot carry, and
/// <see cref="PostgresJournalOptions.SetSearchPathOnConnection"/> records the two ways
/// PgBouncer fails with one.
/// </remarks>
public sealed class SearchPathOptionTests
{
    private const string ConnectionString = "Host=localhost;Username=postgres;Database=postgres";

    /// <summary>By default the schema travels on the connection, as it always has.</summary>
    [Fact]
    public void TheSchemaIsPutOnTheConnectionByDefault()
    {
        using var source = ServiceCollectionExtensions.BuildDataSource(
            ConnectionString, new PostgresJournalOptions { Schema = "flowx" });

        new NpgsqlConnectionStringBuilder(source.ConnectionString).SearchPath.ShouldBe(
            "flowx",
            "the default stopped putting the schema on the connection, which silently changes "
            + "which schema every existing direct deployment reaches.");
    }

    /// <summary>And a deployment that supplies it another way can say so.</summary>
    /// <remarks>
    /// The switch a pooled deployment needs. Asserted as *absent* rather than as some other
    /// value, because anything the adapter writes here becomes a startup parameter and a
    /// pooler rejects the connection over it.
    /// </remarks>
    [Fact]
    public void TheSchemaIsLeftOffWhenTheDeploymentSuppliesIt()
    {
        using var source = ServiceCollectionExtensions.BuildDataSource(
            ConnectionString,
            new PostgresJournalOptions { Schema = "flowx", SetSearchPathOnConnection = false });

        new NpgsqlConnectionStringBuilder(source.ConnectionString).SearchPath.ShouldBeNullOrEmpty(
            "the adapter still sends a search_path startup parameter after being told the "
            + "deployment supplies the schema. PgBouncer refuses a connection carrying one.");
    }
}
