using System.Reflection;
using FlowX;
using FlowX.Postgres;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Properties of the entity records themselves, checked without a database because none of them
/// needs one.
/// </summary>
/// <remarks>
/// These are the assertions that fail on the day a member is renamed, a marker is dropped or a
/// contract is added without being registered — in this branch, rather than in whichever of
/// packages 4 to 12 first tried to journal it.
/// </remarks>
public sealed class ContractTests
{
    /// <summary>The eight entities and five process types of §5.</summary>
    /// <remarks>
    /// Listed rather than discovered by scanning the assembly for records. A list is what fails
    /// when a type is deleted; a scan would simply find one fewer and pass.
    /// </remarks>
    public static IReadOnlyList<Type> Entities { get; } =
    [
        typeof(Lead), typeof(Account), typeof(Contact), typeof(Opportunity),
        typeof(Quote), typeof(QuoteLine), typeof(SalesOrder), typeof(Activity),
        typeof(ProcessDefinition), typeof(ProcessStage), typeof(ProcessTransition),
        typeof(TransitionGuard), typeof(TransitionAction),
    ];

    /// <summary>The same list, as xUnit wants it.</summary>
    public static TheoryData<Type> EntityRecords => [.. Entities];

    /// <summary>
    /// The three members §5 marks sensitive carry <c>[Sensitive]</c>.
    /// </summary>
    /// <remarks>
    /// §5.1 asks for it on <c>Contact.Email</c> and <c>Contact.Phone</c>.
    /// <c>Lead.Email</c> is this package's addition: it is the same personal datum before
    /// anybody decided the person was worth talking to, and marking one and not the other would
    /// put an address in a journal row for exactly as long as the person was unqualified.
    /// </remarks>
    [Theory]
    [InlineData(typeof(Contact), nameof(Contact.Email))]
    [InlineData(typeof(Contact), nameof(Contact.Phone))]
    [InlineData(typeof(Lead), nameof(Lead.Email))]
    public void PersonalDataIsMarkedSensitive(Type contract, string member)
    {
        ArgumentNullException.ThrowIfNull(contract);

        contract.GetProperty(member)!
            .GetCustomAttribute<SensitiveAttribute>()
            .ShouldNotBeNull(
                $"{contract.Name}.{member} carries personal data and is not marked. The marker " +
                "is what lists it under the contract's `sensitive` array in the manifest and " +
                "what strips it out of a problem document — neither of which anything has to " +
                "remember to do, and both of which stop the moment this attribute goes.");
    }

    /// <summary>
    /// No entity record carries a tenant, and that is this package's one argument with §5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §5.1, §5.3 and §5.4 give <c>Lead</c>, <c>Account</c>, <c>Activity</c> and
    /// <c>ProcessDefinition</c> a <c>TenantId Tenant</c> member. The records have it nowhere.
    /// </para>
    /// <para>
    /// A tenant on a contract is a value a caller can set. ADR-0046 settles where a tenant
    /// comes from — validated claims, never a header, never the payload — and the whole
    /// isolation story of §6 rests on the runtime deriving it rather than reading it. The first
    /// capability to trust such a member would reintroduce exactly the cross-tenant read the
    /// policies exist to refuse, and it would do so on a code path that reads as ordinary.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EntityRecords))]
    public void NoEntityRecordCarriesATenant(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var carried = contract.GetProperties()
            .Select(static property => property.Name)
            .Where(static name =>
                name.Equals("Tenant", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TenantId", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        carried.ShouldBeEmpty(
            $"{contract.Name} carries a tenant on its contract. It is on " +
            "CapabilityContext.TenantId at run time and in the row's tenant_id column at rest, " +
            "and a third copy that arrives on a request body is the one a caller chooses.");
    }

    /// <summary>Every entity record is a record, and is sealed.</summary>
    /// <remarks>
    /// A contract somebody can derive from is a contract whose wire shape a derived type can
    /// widen without the serialiser context knowing, and whose value equality stops being
    /// value equality.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EntityRecords))]
    public void EveryEntityRecordIsASealedRecord(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        contract.IsSealed.ShouldBeTrue($"{contract.Name} is not sealed.");

        contract.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .ShouldNotBeNull($"{contract.Name} is not a record.");
    }

    /// <summary>
    /// <c>Money</c> and <c>RelatedRef</c> are value objects, and no monetary member is a bare
    /// decimal.
    /// </summary>
    /// <remarks>
    /// §5.2: "the <c>event-driven</c> sample already found what happens when a currency is
    /// hard-coded: four transports agreed with each other and all four were wrong. Amounts here
    /// are never bare decimals." That is a rule about every record in the file, so it is
    /// checked over every record in the file rather than over the four somebody remembered.
    /// </remarks>
    [Fact]
    public void EveryAmountCarriesItsCurrency()
    {
        typeof(Money).IsValueType.ShouldBeTrue("Money is a value object.");
        typeof(RelatedRef).IsValueType.ShouldBeTrue("RelatedRef is a value object.");

        var bare = Entities
            .SelectMany(static contract => contract.GetProperties()
                .Where(static property => property.PropertyType == typeof(decimal)
                    || property.PropertyType == typeof(decimal?))
                .Select(property => $"{contract.Name}.{property.Name}"))
            .ToArray();

        bare.ShouldBeEmpty(
            "a monetary member is a bare decimal, so its currency is whatever the reader " +
            "assumes:" + Environment.NewLine + string.Join(Environment.NewLine, bare));
    }

    /// <summary>Every contract this application can serialise is in the generated context.</summary>
    /// <remarks>
    /// <c>JournalPayload.Of</c> takes a <c>JsonTypeInfo&lt;T&gt;</c> and has no overload that
    /// reflects over a type, so a contract outside the context is one that fails at the moment
    /// somebody first journals it — which, for these thirteen, would be in another branch,
    /// against another author, for a reason belonging to this one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EntityRecords))]
    public void EveryEntityRecordIsInTheSerialiserContext(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        CrmJsonContext.Default.GetTypeInfo(contract).ShouldNotBeNull(
            $"{contract.Name} is not in CrmJsonContext. Add a [JsonSerializable] line for it.");
    }

    /// <summary>Every migration in the list has its script, and every script is in the list.</summary>
    /// <remarks>
    /// The two halves fail differently and both are silent. A migration listed without its
    /// resource is an application that cannot create its own schema, and it fails at start-up
    /// against a customer's database rather than here. A resource that is not listed is DDL
    /// nobody applies, and it fails at the first statement that needs the table.
    /// </remarks>
    [Fact]
    public void TheMigrationListAndTheEmbeddedScriptsAgree()
    {
        foreach (var migration in CrmMigrator.Migrations)
        {
            CrmMigrator.ReadScript(migration).ShouldNotBeNullOrWhiteSpace(
                $"{migration} is listed and its script is empty or missing.");
        }

        var embedded = typeof(CrmMigrator).Assembly.GetManifestResourceNames()
            .Where(static name => name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(static name => name[(name.LastIndexOf('.', name.Length - 5) + 1)..])
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var listed = CrmMigrator.Migrations
            .Select(static m => m.Resource)
            .OrderBy(static r => r, StringComparer.Ordinal)
            .ToArray();

        embedded.ShouldBe(
            listed,
            customMessage: "an embedded .sql is not in CrmMigrator.Migrations, or the other way round.");

        CrmMigrator.Migrations
            .Select(static m => m.Version)
            .ToArray()
            .ShouldBe(
                [.. Enumerable.Range(1, CrmMigrator.Migrations.Count)],
                customMessage: "versions are 1..n, in order.");
    }

    /// <summary>
    /// The sample narrows a connection to exactly the role and setting the journal's own
    /// migration installs.
    /// </summary>
    /// <remarks>
    /// <c>CrmTenantScope</c> is a second copy of <c>FlowX.Postgres</c>'s internal
    /// <c>TenantScope</c>, written out because that type is not this sample's to make public.
    /// Two copies must agree, and the shipped migration is the thing both of them are copies
    /// of — so it is read here, out of the plugin's own embedded resource, rather than the two
    /// C# constants being compared to each other and both being wrong together.
    /// </remarks>
    [Fact]
    public void TheSampleScopesAConnectionExactlyAsTheJournalDoes()
    {
        var installed = PostgresMigrator.ReadScript(
            PostgresMigrator.Migrations.Single(static m => m.Name == "tenant_row_level_security"));

        installed.ShouldContain(
            CrmTenantScope.RoleName,
            customMessage:
            "the journal's row-level security migration does not mention the role this sample " +
            "narrows to, so the sample is assuming a role the platform does not create.");

        installed.ShouldContain(
            CrmTenantScope.SettingName,
            customMessage:
            "the journal's policies read a different setting from the one this sample sets, so " +
            "the CRM tables and the journal tables are isolating on two different values.");
    }
}
