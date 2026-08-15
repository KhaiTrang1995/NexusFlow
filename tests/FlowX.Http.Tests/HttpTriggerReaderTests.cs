using System.Security.Claims;
using FlowX.Http;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace FlowX.Http.Tests;

/// <summary>
/// The boundary where HTTP stops. Everything past it sees correlation, tenant and
/// idempotency and cannot tell they came from a request — which is what makes quality
/// goal Q4 hold in practice rather than on paper.
/// </summary>
public sealed class HttpTriggerReaderTests
{
    private static DefaultHttpContext Request(
        Action<HttpContext>? configure = null,
        params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/orders";

        if (claims.Length > 0)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        configure?.Invoke(context);
        return context;
    }

    // ── Tenant resolution (OWASP A01 / A07) ────────────────────────────────────

    [Theory]
    [InlineData("tid")]
    [InlineData("tenant_id")]
    [InlineData("http://schemas.flowx.dev/claims/tenant")]
    public void ReadsTheTenantFromAValidatedClaim(string claimType)
    {
        var context = Request(claims: new Claim(claimType, "acme"));

        HttpTriggerReader.ReadTenant(context.User).ShouldBe("acme");
    }

    [Fact]
    public void NeverReadsTheTenantFromAHeader()
    {
        var context = Request(c =>
        {
            c.Request.Headers["X-Tenant-ID"] = "victim-corp";
            c.Request.Headers["tid"] = "victim-corp";
            c.Request.Headers["tenant_id"] = "victim-corp";
        });

        HttpTriggerReader.ReadTenant(context.User).ShouldBeNull(
            "A tenant read from a header is a tenant the caller chooses. That is a " +
            "cross-tenant read waiting to happen, and there is deliberately no " +
            "configuration hook to enable it (OWASP A01/A07).");
    }

    [Fact]
    public void IgnoresATenantClaimOnAnUnauthenticatedPrincipal()
    {
        var identity = new ClaimsIdentity(new[] { new Claim("tid", "acme") });  // no auth type
        var principal = new ClaimsPrincipal(identity);

        identity.IsAuthenticated.ShouldBeFalse();
        HttpTriggerReader.ReadTenant(principal).ShouldBeNull(
            "An unvalidated claim is an assertion by the caller, not by the issuer.");
    }

    [Fact]
    public void ReturnsNoTenantRatherThanADefaultWhenThereIsNoClaim()
    {
        HttpTriggerReader.ReadTenant(Request(claims: new Claim("sub", "user-1")).User).ShouldBeNull(
            "A default tenant is the shape of a cross-tenant leak: the request proceeds, " +
            "reads succeed, and the wrong customer's data comes back.");

        HttpTriggerReader.ReadTenant(null).ShouldBeNull();
    }

    [Fact]
    public void PrefersTheFirstConfiguredClaimTypeDeterministically()
    {
        var context = Request(claims: [new Claim("tenant_id", "second"), new Claim("tid", "first")]);

        HttpTriggerReader.ReadTenant(context.User).ShouldBe("first",
            "Claim precedence must not depend on the order the identity provider happens " +
            "to emit claims in.");
    }

    // ── Idempotency (mutating endpoints) ───────────────────────────────────────

    [Fact]
    public void RejectsAMutatingRequestWithNoIdempotencyKey()
    {
        var result = HttpTriggerReader.Read(Request(), requireIdempotencyKey: true);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("http.idempotency_key_required");
        result.Error.Category.ShouldBe(ErrorCategory.Validation,
            "Terminal: retrying the same request without the header cannot succeed.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsABlankIdempotencyKeyAsMissing(string blank)
    {
        var context = Request(c => c.Request.Headers[FlowXHeaders.IdempotencyKey] = blank);

        HttpTriggerReader.Read(context, requireIdempotencyKey: true).IsFailure.ShouldBeTrue(
            "An endpoint that promises deduplication and then does not deduplicate is " +
            "worse than one that never promised.");
    }

    [Fact]
    public void UsesTheCallersIdempotencyKeyWhenSupplied()
    {
        var context = Request(c => c.Request.Headers[FlowXHeaders.IdempotencyKey] = "order-42");

        HttpTriggerReader.Read(context, requireIdempotencyKey: true)
            .Value.IdempotencyKey.ShouldBe("order-42");
    }

    [Fact]
    public void GeneratesAKeyWhenTheEndpointDoesNotRequireOne()
    {
        var result = HttpTriggerReader.Read(Request(), requireIdempotencyKey: false);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IdempotencyKey.ShouldNotBeNullOrWhiteSpace(
            "A generated key still makes retries *within* the flow safe. It cannot " +
            "deduplicate across requests, which is what the caller declined to ask for.");
    }

    [Fact]
    public void GeneratesADistinctKeyPerRequest()
    {
        var first = HttpTriggerReader.Read(Request(), false).Value.IdempotencyKey;
        var second = HttpTriggerReader.Read(Request(), false).Value.IdempotencyKey;

        second.ShouldNotBe(first);
    }

    // ── Correlation ────────────────────────────────────────────────────────────

    [Fact]
    public void ContinuesTheCallersW3CTrace()
    {
        var context = Request(c =>
            c.Request.Headers[FlowXHeaders.TraceParent] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

        HttpTriggerReader.ReadCorrelationId(context)
            .ShouldBe("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                "Restarting the trace severs the request from everything upstream of " +
                "this service, which is the whole reason distributed tracing exists.");
    }

    [Fact]
    public void FallsBackToTheCorrelationHeaderForCallersThatDoNotSpeakW3C()
    {
        var context = Request(c => c.Request.Headers[FlowXHeaders.CorrelationId] = "legacy-123");

        HttpTriggerReader.ReadCorrelationId(context).ShouldBe("legacy-123");
    }

    [Fact]
    public void PrefersTraceParentOverTheFallbackHeader()
    {
        var context = Request(c =>
        {
            c.Request.Headers[FlowXHeaders.TraceParent] = "00-abc-def-01";
            c.Request.Headers[FlowXHeaders.CorrelationId] = "legacy-123";
        });

        HttpTriggerReader.ReadCorrelationId(context).ShouldBe("00-abc-def-01");
    }

    [Fact]
    public void GeneratesACorrelationIdWhenTheCallerSuppliesNone()
        => HttpTriggerReader.ReadCorrelationId(Request()).ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void CarriesCorrelationAndTenantIntoTheInvocation()
    {
        var context = Request(
            c =>
            {
                c.Request.Headers[FlowXHeaders.TraceParent] = "00-abc-def-01";
                c.Request.Headers[FlowXHeaders.IdempotencyKey] = "key-1";
            },
            new Claim("tid", "acme"));

        var invocation = HttpTriggerReader.Read(context, requireIdempotencyKey: true).Value;

        invocation.CorrelationId.ShouldBe("00-abc-def-01");
        invocation.TenantId.ShouldBe("acme");
        invocation.IdempotencyKey.ShouldBe("key-1");
    }

    // ── Purpose limitation (GDPR 5(1)(b)) ──────────────────────────────────────

    /// <summary>The processing purpose crosses the boundary on a validated claim.</summary>
    /// <remarks>
    /// This is the whole of where a purpose enters, and past this line the engine compares it
    /// with whatever a step's <c>PolicySet.Consent(...)</c> declared without being able to tell
    /// which transport produced it.
    /// </remarks>
    [Theory]
    [InlineData("purpose")]
    [InlineData("http://schemas.flowx.dev/claims/purpose")]
    public void ReadsThePurposeFromAValidatedClaim(string claimType)
    {
        var context = Request(claims: new Claim(claimType, "treatment"));

        HttpTriggerReader.ReadPurpose(context.User).ShouldBe("treatment");

        HttpTriggerReader.Read(context, requireIdempotencyKey: false).Value.Purpose
            .ShouldBe("treatment", "and it reaches the invocation, not only the reader.");
    }

    /// <summary>A purpose is never taken from a header, whatever the header is called.</summary>
    /// <remarks>
    /// <strong>The assertion the policy rests on.</strong> A purpose limitation whose input is
    /// chosen by the party being limited is not a limitation — it is a field the caller fills
    /// in to unlock the step. Same objection as the tenant's, one row down
    /// <c>docs/15 §3</c>'s Boundary 1.
    /// </remarks>
    [Fact]
    public void NeverReadsThePurposeFromAHeader()
    {
        var context = Request(c =>
        {
            c.Request.Headers["X-Purpose"] = "treatment";
            c.Request.Headers["purpose"] = "treatment";
        });

        HttpTriggerReader.Read(context, requireIdempotencyKey: false).Value.Purpose.ShouldBeNull(
            "a caller that could name its own purpose would be granting itself the consent " +
            "the policy exists to check.");
    }

    /// <summary>A purpose claim on an unauthenticated principal is not a purpose.</summary>
    /// <remarks>
    /// ASP.NET Core hands every anonymous request a <see cref="ClaimsPrincipal"/> that can
    /// carry any claim at all, so a reader that only checked for the claim's presence would
    /// read a header with extra steps.
    /// </remarks>
    [Fact]
    public void IgnoresAPurposeClaimOnAnUnauthenticatedPrincipal()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("purpose", "treatment")]));

        HttpTriggerReader.ReadPurpose(principal).ShouldBeNull();
    }

    /// <summary>No claim means no purpose, and a consent-gated step then refuses.</summary>
    [Fact]
    public void ReturnsNoPurposeRatherThanADefaultWhenThereIsNoClaim()
    {
        HttpTriggerReader.ReadPurpose(Request(claims: new Claim("sub", "user-1")).User)
            .ShouldBeNull("a default purpose is a gate that opens for everyone who never " +
                          "heard of it, which is the failure the policy exists to close.");

        HttpTriggerReader.ReadPurpose(null).ShouldBeNull();
    }

    [Fact]
    public void RejectsANullContext()
    {
        Should.Throw<ArgumentNullException>(() => HttpTriggerReader.Read(null!, false));
        Should.Throw<ArgumentNullException>(() => HttpTriggerReader.ReadCorrelationId(null!));
    }
}
