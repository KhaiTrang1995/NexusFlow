using System.Net;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What a tenant has declared, listed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> Twelve things could be declared and none could be
/// listed: a validation rule, an SLA policy, a connector, a label. A configuration surface whose
/// only read is "try something and see what refuses it" is one nobody trusts, and every setup
/// screen in the client was drawing a list it had been compiled with.
/// </para>
/// </remarks>
public sealed class ConfigApiTests
{
    private const string Config = "/api/v1/crm/config";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Each kind reads its own table, and says what each row does.</summary>
    [Fact]
    public async Task EachKindListsWhatWasDeclared()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.PostAsync(
            "/api/v1/crm/service/policies",
            new DefineSlaPolicy("gold", "Gold", CasePriority.Urgent, 15, 120, BusinessHoursOnly: false),
            CrmTokens.NorthwindManager);

        var policies = await ListAsync(app, ConfigKind.SlaPolicy);

        policies.Items.Count.ShouldBe(1);
        policies.Items[0]!.Label.ShouldBe("Gold");

        // The sentence is the point: a row of columns is not what an administrator came to read.
        policies.Items[0]!.Summary.ShouldContain("Urgent");
        policies.Items[0]!.Summary.ShouldContain("15");
        policies.Items[0]!.Summary.ShouldContain("around the clock");
    }

    /// <summary>A kind with nothing declared answers with nothing, not with an error.</summary>
    /// <remarks>
    /// Twelve statements behind one route, and the way that goes wrong is a kind falling through
    /// the switch to somebody else's table — which comes back as rows rather than as a refusal.
    /// </remarks>
    [Fact]
    public async Task EveryKindAnswersOnAnEmptyTenant()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        foreach (var kind in Enum.GetValues<ConfigKind>())
        {
            var list = await ListAsync(app, kind);

            list.Kind.ShouldBe(kind, "the answer named a kind nobody asked for.");
            list.Items.ShouldBeEmpty($"{kind} read somebody else's table.");
        }
    }

    /// <summary>
    /// A day of opening hours is named, and not named the same thing seven times.
    /// </summary>
    /// <remarks>
    /// <strong>The bug this holds shut.</strong> <c>to_char(to_timestamp(n, 'ID'), 'Day')</c>
    /// reads as though it names a weekday and does not: with no date to anchor it every value
    /// formats identically, so five rows all came back "Saturday". A label that is wrong six
    /// times in seven looks deliberate, which is why nobody would have queried it.
    /// </remarks>
    [Fact]
    public async Task EachDaysOpeningHoursAreNamedForThatDay()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.PostAsync(
            "/api/v1/crm/service/hours",
            new SetBusinessHours([
                new OpeningHoursOfDay(DayOfWeek.Monday, "09:00", "17:30"),
                new OpeningHoursOfDay(DayOfWeek.Wednesday, "09:00", "17:30"),
                new OpeningHoursOfDay(DayOfWeek.Saturday, "10:00", "14:00"),
            ]),
            CrmTokens.NorthwindManager);

        var hours = await ListAsync(app, ConfigKind.BusinessHours);

        hours.Items.Select(item => item.Label).ShouldBe(["Monday", "Wednesday", "Saturday"]);
        hours.Items[2]!.Summary.ShouldContain("10:00");
    }

    /// <summary>Reading the tenant's configuration is an administrator's grant.</summary>
    /// <remarks>
    /// These sentences say what refuses a write, who approves what, and where this server sends.
    /// A setup screen anybody could read is a map of the controls for anybody who would rather
    /// not meet them.
    /// </remarks>
    [Fact]
    public async Task ARepresentativeMayNotReadTheTenantsConfiguration()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Config, new ReadConfig(ConfigKind.SlaPolicy, 25), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>One tenant's configuration is never another's.</summary>
    /// <remarks>
    /// Asserted against the store rather than over HTTP: this sample mints no administrator token
    /// for Contoso, and a test that asked with a representative's would be asserting the
    /// permission check rather than the isolation. The scope is what is under test here.
    /// </remarks>
    [Fact]
    public async Task OneTenantsConfigurationIsOnlyItsOwn()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO sla_policy (
                policy_id, tenant_id, name, label, priority, first_response_minutes,
                resolution_minutes, business_hours_only, is_active)
            VALUES (gen_random_uuid(), @tenant, 'gold', 'Gold', 'Urgent', 15, 120, false, true)
            """,
            Cancellation,
            ("tenant", CrmSchemaHarness.Northwind));

        var store = new ConfigStore(crm.DataSource);
        var read = new ReadConfig(ConfigKind.SlaPolicy, 50);

        (await store.ListAsync(CrmSchemaHarness.Northwind, read, Cancellation)).Count.ShouldBe(1);

        (await store.ListAsync(CrmSchemaHarness.Contoso, read, Cancellation)).ShouldBeEmpty(
            "row-level security is what makes this true, not the statement.");
    }

    /// <summary>A limit above the ceiling is clamped rather than honoured.</summary>
    [Fact]
    public async Task TheLimitIsClamped()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Config,
            new ReadConfig(ConfigKind.Territory, int.MaxValue),
            CrmTokens.NorthwindManager,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "a silly limit is not a refusal.");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<ConfigList> ListAsync(
        CrmApplication app,
        ConfigKind kind,
        string token = CrmTokens.NorthwindManager)
    {
        var response = await app.PostAsync(
            Config, new ReadConfig(kind, 100), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{kind} did not answer.");

        return await CrmApplication.ReadAsync<ConfigList>(response);
    }
}
