using System.Text.RegularExpressions;
using Crm;
using FlowX.Generated;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The <c>curl</c> sequence in <c>samples/crm/README.md</c>, checked against what the build
/// actually publishes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A README nobody checks is a README that is wrong within a month.</strong> Every route
/// the page tells a reader to call is read out of the page and looked for in the generated
/// manifest, and every token it tells them to present is looked for in
/// <see cref="CrmTokens.Claims"/>. Renaming a route or a token breaks this test rather than
/// somebody's first ten minutes with the sample.
/// </para>
/// <para>
/// <strong>What it does not do is run the sequence.</strong> That needs a live application, a
/// database and the ids each call hands to the next; the flows themselves are asserted by the
/// suites beside this one. What is left for a page to get wrong is the names, and that is what
/// this reads.
/// </para>
/// </remarks>
public sealed partial class SampleReadmeTests
{
    private static readonly string Readme =
        File.ReadAllText(Path.Combine(RepositoryRoot().FullName, "samples", "crm", "README.md"));

    [Fact]
    public void EveryRouteTheReadmeTellsAReaderToCallIsPublished()
    {
        var routes = RouteInCurl().Matches(Readme)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        routes.ShouldNotBeEmpty("the page documents a sequence, so it names routes.");

        var published = PublishedRoute().Matches(FlowXManifest.Json)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        published.ShouldNotBeEmpty("the manifest publishes the routes the triggers declare.");

        foreach (var route in routes)
        {
            // Whole route, not a substring: '/api/v1/crm/order' is inside '/api/v1/crm/orders'
            // and a page that dropped the 's' would otherwise pass this test and 404 the reader.
            published.ShouldContain(
                route, $"the README tells a reader to POST {route} and no flow declares it.");
        }
    }

    [Fact]
    public void EveryTokenTheReadmePresentsExists()
    {
        var tokens = BearerToken().Matches(Readme)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        tokens.ShouldNotBeEmpty();

        foreach (var token in tokens)
        {
            CrmTokens.Claims.ShouldContainKey(
                token, $"the README presents '{token}' and no such token is minted.");
        }
    }

    /// <summary>
    /// The tables of a schema this build does not have would be a page describing another
    /// deployment.
    /// </summary>
    [Fact]
    public void TheSchemaVersionTheReadmeImpliesIsTheOneThisBuildWrites()
    {
        Readme.ShouldContain(
            "Fifty-one tables",
            Case.Sensitive,
            "the count is the one CrmSchemaReader emits and TenantIsolationTests counts policies for.");

        CrmMigrator.TargetVersion.ShouldBe(19);
    }

    [GeneratedRegex(@"http://localhost:5000(/api/[^\s\\']+)")]
    private static partial Regex RouteInCurl();

    [GeneratedRegex("\"route\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex PublishedRoute();

    [GeneratedRegex(@"Authorization: Bearer ([a-z0-9-]+)")]
    private static partial Regex BearerToken();

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlowX.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            "FlowX.slnx was not found above " + AppContext.BaseDirectory);
    }
}
