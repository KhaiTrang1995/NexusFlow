using System.Text.Json;
using FlowX.Generated;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// The scheme this sample registers, held to the stances its capabilities declare.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The sample could not onboard anybody before this scheme existed.</strong>
/// Authorisation is decided in the step loop against <c>HttpContext.User</c>, so a host with no
/// authentication answered <c>403 authorization.not_authenticated</c> at <c>offer.validate</c>
/// and every transcript in the README was of a sample that no longer ran. What stops that
/// recurring is not the scheme but <see cref="TheOperatorHoldsEveryPermissionThisApplicationNames"/>:
/// the grants are read out of the manifest rather than listed here, so a capability added with a
/// permission nobody holds fails this test naming the grant.
/// </para>
/// <para>
/// <strong>And the flow nobody calls is checked from the other direction.</strong> An occurrence
/// has no caller, so a permission on a scheduled flow's capability would make it undeployable —
/// <see cref="TheScheduledFlowAsksForNoPrincipal"/> is what keeps that from being discovered at
/// 02:00.
/// </para>
/// </remarks>
public sealed class AuthenticationTests
{
    private static readonly JsonDocument Manifest = JsonDocument.Parse(FlowXManifest.Json);

    /// <summary>
    /// Every permission any capability in this application names is one the operator token
    /// carries.
    /// </summary>
    [Fact]
    public void TheOperatorHoldsEveryPermissionThisApplicationNames()
    {
        var granted = PeopleOpsTokens.Claims[PeopleOpsTokens.PeopleOps]
            .Single(claim => claim.Type == "scope")
            .Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var required = Declared();

        // Not empty, so the assertion below cannot pass by there being nothing to check —
        // which is how this test would rot if the stances were ever dropped.
        required.ShouldNotBeEmpty();

        foreach (var permission in required)
        {
            granted.ShouldContain(
                permission,
                $"No demonstration token holds '{permission}', so the capability naming it " +
                "cannot be reached over HTTP and the sample cannot run its own README.");
        }
    }

    /// <summary>The recruiter is authenticated and holds none of the six writes.</summary>
    /// <remarks>
    /// The negative half. A token that quietly held everything would satisfy the test above and
    /// leave the sample with no way to show that authenticated is not authorised.
    /// </remarks>
    [Fact]
    public void TheRecruiterIsAuthenticatedAndHoldsNoWritePermission()
    {
        var granted = PeopleOpsTokens.Claims[PeopleOpsTokens.Recruiter]
            .Single(claim => claim.Type == "scope")
            .Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var permission in Declared())
        {
            granted.ShouldNotContain(permission);
        }
    }

    /// <summary>
    /// <c>offer.window.close</c>'s capability names no permission, because nothing authenticates
    /// an occurrence.
    /// </summary>
    [Fact]
    public void TheScheduledFlowAsksForNoPrincipal()
    {
        var scheduled = Manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Single(capability => capability.GetProperty("id").GetString() == "offer.window.close");

        scheduled.GetProperty("authorization").GetProperty("mode").GetString().ShouldBe("Internal");
        scheduled.GetProperty("authorization").TryGetProperty("value", out _).ShouldBeFalse();
    }

    /// <summary>Every permission the manifest's capabilities name, deduplicated.</summary>
    private static IReadOnlyList<string> Declared() =>
        [.. Manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(capability => capability.GetProperty("authorization"))
            .Where(authorization => authorization.TryGetProperty("value", out _))
            .Select(authorization => authorization.GetProperty("value").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static permission => permission, StringComparer.Ordinal)];
}
