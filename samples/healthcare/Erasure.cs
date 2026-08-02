using System.Security.Claims;
using System.Text.Json;
using FlowX;
using FlowX.Hosting;
using FlowX.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Healthcare;

/// <summary>
/// The right to erasure, exposed as an endpoint this application writes by hand.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not a flow, and the reason is worth reading before copying this file.</strong> A
/// flow is a unit of work a trigger starts and a journal records. An erasure is the opposite
/// of both: it exists to remove journal payloads, and journaling it would open an instance
/// whose input carries the identifier it was called to destroy — which the redaction pass
/// would then replace with <c>[redacted]</c>, leaving a row that records that somebody was
/// erased and cannot say who. So it is a store operation invoked from an endpoint, and
/// <see cref="ISubjectErasure"/> is the seam.
/// </para>
/// <para>
/// <strong>Not a CLI verb either, and that is a refusal rather than an omission.</strong>
/// <c>flowx purge --subject …</c> is what this sample's README used to promise. It is not
/// built and is not planned: the CLI reads the journal as rows
/// (<a href="../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md">ADR-0020</a>), and a
/// national identifier passed as <c>--subject</c> lands in shell history, in the process table
/// and in whatever collects both — three new places for the value this sample exists to keep
/// out of one.
/// </para>
/// <para>
/// <strong>Nothing about a hand-written endpoint is free.</strong> A flow gets tenant
/// resolution, residency and the capability's authorisation stance from the platform, at
/// admission, before anything is allocated. This endpoint gets none of it and does the same
/// three checks itself — which is what the first half of the handler is, and which is the
/// honest cost of stepping outside the flow model rather than something to hide.
/// </para>
/// <para>
/// <strong>A <see cref="RequestDelegate"/> rather than a minimal-API handler</strong>, for
/// the reason <c>FlowEndpointExtensions</c> gives: delegate binding reflects over the
/// handler's parameters and over the body type, which the trim and AOT analysers refuse
/// (constraint C2). Reading and writing through the generated <see cref="HealthcareJsonContext"/>
/// costs one line each and no reflection.
/// </para>
/// </remarks>
public static class ErasureEndpoint
{
    /// <summary>The grant an erasure needs.</summary>
    public const string Permission = "records:erase";

    /// <summary>The route this application answers erasure petitions on.</summary>
    public const string Route = "/api/v1/patients/erasure";

    /// <summary>Maps the erasure endpoint onto the application.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The mapped endpoint, so conventions can be added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointConventionBuilder MapPatientErasure(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var builder = app.Map(Route, HandleAsync);

        builder.WithMetadata(new HttpMethodMetadata(["POST"]));

        return builder;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var petition = await JsonSerializer
            .DeserializeAsync(
                context.Request.Body,
                HealthcareJsonContext.Default.ErasurePetition,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (petition is null || string.IsNullOrWhiteSpace(petition.NationalId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // The same admissions a flow gets for free, in the order the host applies them: who
        // are you, which clinic, and may this clinic's data be processed here at all.
        var tenantId = ClaimTenantResolver.FromClaims(context.User);

        if (context.User.Identity?.IsAuthenticated != true
            || tenantId is not { Length: > 0 }
            || !HoldsPermission(context.User))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var residency = context.RequestServices.GetRequiredService<IOptions<FlowXOptions>>()
            .Value.Residency;

        if (!residency.Permits(tenantId))
        {
            // The same error the host would have produced at admission, from the same
            // primitive, so the refusal an operator sees here reads exactly as the one an
            // intake sees.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // The digest is computed here, in the process that was handed the identifier, and the
        // identifier goes no further. What crosses into the store is sixty-four hex characters
        // — see SubjectDigest for why that is a join key rather than anonymisation, and why
        // the distinction is stated rather than assumed.
        var receipt = await context.RequestServices.GetRequiredService<ISubjectErasure>()
            .EraseAsync(
                new ErasureRequest
                {
                    SubjectDigest = SubjectDigest.Of(petition.NationalId),
                    TenantId = tenantId,
                    Mode = petition.Confirm ? ErasureMode.Confirm : ErasureMode.DryRun,
                },
                context.RequestAborted)
            .ConfigureAwait(false);

        if (receipt.IsFailure)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await JsonSerializer
            .SerializeAsync(
                context.Response.Body,
                ErasureReport.From(receipt.Value),
                HealthcareJsonContext.Default.ErasureReport,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>Whether the caller holds <see cref="Permission"/> in an OAuth 2.0 scope claim.</summary>
    /// <remarks>
    /// Space-delimited, because that is what an access token carries. This is the check a
    /// capability's <c>Authorization.Permission</c> stance performs in the step loop; doing it
    /// by hand here is the second thing this endpoint pays for not being a flow.
    /// </remarks>
    private static bool HoldsPermission(ClaimsPrincipal principal) =>
        principal.Claims
            .Where(static claim => string.Equals(claim.Type, "scope", StringComparison.Ordinal))
            .SelectMany(static claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(Permission, StringComparer.Ordinal);
}
