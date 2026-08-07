using FlowX;
using FlowX.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
