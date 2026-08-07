using FlowX;
using FlowX.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The sample starts without a broker, which is what it has always claimed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the rest of the suite could not catch this.</strong> <see cref="CrmApplication"/>
/// composes the application by hand and leaves the outbox pump out, so nothing there ever
/// resolves an <see cref="IEventPublisher"/> — and the factory
/// <c>AddFlowXPostgresOutbox</c> registers only runs when somebody does. Four hundred and
/// ninety-five passing tests, and <c>dotnet run</c> died on <c>StartAsync</c> with a
/// service-not-registered exception. A composition nothing composes is a composition nothing
/// tests.
/// </para>
/// <para>
/// <strong>So this builds the composition rather than the behaviour.</strong> The assertion is
/// that a host wired the way <c>Program.cs</c> wires one — outbox, pump, no broker — starts.
/// </para>
/// </remarks>
public sealed class BrokerlessCompositionTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A host with the outbox, the pump and no broker starts.</summary>
    [Fact]
    public async Task AHostWithNoBrokerStarts()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        using var host = BrokerlessHost(crm);

        // The line that used to throw. Nothing after it is the point.
        await Should.NotThrowAsync(() => host.StartAsync(Cancellation));
        await host.StopAsync(Cancellation);
    }

    /// <summary>The drain resolves, and it is the one that publishes nothing.</summary>
    [Fact]
    public async Task TheDrainResolvesAgainstThePublisherThatSendsNowhere()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        using var host = BrokerlessHost(crm);

        host.Services.GetRequiredService<IEventPublisher>().ShouldBeOfType<UnpublishedOutbox>();
        Should.NotThrow(() => host.Services.GetRequiredService<PostgresOutboxPublisher>());
    }

    /// <summary>
    /// It acknowledges nothing, so a staged event survives until a broker exists.
    /// </summary>
    /// <remarks>
    /// The half that matters more than starting. A publisher that reported "sent" would let the
    /// sweep mark every row published, and every event staged before somebody configured a broker
    /// would be gone — silently, permanently, and with the process looking perfectly healthy.
    /// </remarks>
    [Fact]
    public async Task ItAcknowledgesNothingSoNoStagedEventIsLost()
    {
        var publisher = new UnpublishedOutbox();

        var batch = new[]
        {
            new OutboxRecord
            {
                EventId = Guid.NewGuid(),
                InstanceId = Guid.NewGuid(),
                Type = "lead.created",
                SchemaVersion = "1.0.0",
                PayloadJson = "{}",
                TenantId = "crm-northwind",
            },
        };

        var published = await publisher.PublishAsync(batch, Cancellation);

        published.IsSuccess.ShouldBeTrue("no broker is not a broken broker.");
        published.Value.ShouldBe(0, "a prefix of nought leaves every row pending.");
    }


    /// <summary>
    /// The drain survives the database going away, instead of taking the host with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reproduced in one line before this existed:</strong> stop PostgreSQL under a
    /// running API and the process exited. A <c>BackgroundService</c> whose <c>ExecuteAsync</c>
    /// throws stops the host by default, so a failover, a patch or somebody's `pg_ctl restart`
    /// was an outage — and it made the readiness probe pointless, because a process that exits
    /// when the database does can never report that the database is gone.
    /// </para>
    /// <para>
    /// A data source pointing at a port nothing listens on is the same failure without the
    /// timing: the first statement throws, and what is asserted is that the pump is still
    /// running a second later.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDrainSurvivesADatabaseThatGoesAway()
    {
        await using var nowhere = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=crm;Username=crm;Password=none;Timeout=1");

        using var pump = new CrmOutboxPump(
            new PostgresOutboxPublisher(nowhere, new UnpublishedOutbox()),
            NullLogger<CrmOutboxPump>.Instance);

        using var stopping = new CancellationTokenSource();

        await pump.StartAsync(stopping.Token);

        // Long enough for the first statement to fail and the loop to come back round.
        await Task.Delay(TimeSpan.FromMilliseconds(750), Cancellation);

        // Asserted through `is null` rather than `ShouldNotBeNull()`: the latter takes and
        // returns the Task, and a Task-valued expression statement is a forgotten await as far
        // as the compiler is concerned.
        var running = pump.ExecuteTask;

        (running is null).ShouldBeFalse("the pump never started.");
        running!.IsFaulted.ShouldBeFalse("a faulted ExecuteAsync is what stops the host.");
        running.IsCompleted.ShouldBeFalse("it should still be trying.");

        await stopping.CancelAsync();
        await pump.StopAsync(Cancellation);
    }

    /// <summary>
    /// Composes the outbox the way <c>Program.cs</c> does when no broker is configured.
    /// </summary>
    private static IHost BrokerlessHost(CrmSchemaHarness crm)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddFlowXPostgres(
            CrmDatabase.ConnectionString!,
            new PostgresJournalOptions { Schema = crm.Schema });

        builder.Services.AddFlowXPostgresOutbox();
        builder.Services.AddFlowXPostgresChangeFeed();
        builder.Services.AddHostedService<CrmOutboxPump>();

        // The `else` branch of Program.cs, and the whole subject of this file.
        builder.Services.AddSingleton<IEventPublisher, UnpublishedOutbox>();

        return builder.Build();
    }
}
