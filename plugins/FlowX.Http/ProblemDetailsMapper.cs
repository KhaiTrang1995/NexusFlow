using Microsoft.AspNetCore.Mvc;

namespace FlowX.Http;

/// <summary>
/// Maps a FlowX <see cref="Error"/> onto an RFC 7807 Problem Details response.
/// </summary>
/// <remarks>
/// <para>
/// One mapping, derived from <see cref="ErrorCategory"/>, so every endpoint in every
/// application reports the same failure the same way. A per-endpoint mapping is how a
/// codebase ends up returning 400, 409 and 422 for the same business condition
/// depending on who wrote the handler.
/// </para>
/// <para>
/// <strong>An <see cref="ErrorCategory.Internal"/> error never reaches the client
/// intact.</strong> Its message is written by us, for us — it names types, hosts, query
/// shapes and occasionally data — and returning it is an information leak (OWASP A05).
/// The client gets a fixed sentence and the correlation id; the real message goes to
/// the log, where the correlation id joins them back up.
/// </para>
/// </remarks>
public static class ProblemDetailsMapper
{
    /// <summary>Base URI for the error taxonomy. Each code resolves to its documentation.</summary>
    public const string TypeUriPrefix = "https://flowx.dev/errors/";

    /// <summary>The single sentence an internal failure is allowed to say.</summary>
    public const string InternalDetail =
        "The request could not be completed. Quote the correlation id when reporting this.";

    /// <summary>Builds the Problem Details document for an error.</summary>
    /// <param name="error">The failure to report.</param>
    /// <param name="instance">The request path, so the document identifies the occurrence.</param>
    /// <param name="correlationId">Ties the response to the server-side log record.</param>
    public static ProblemDetails ToProblemDetails(Error error, string instance, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(error);

        var isInternal = error.Category == ErrorCategory.Internal;

        var problem = new ProblemDetails
        {
            Type = TypeUriPrefix + error.Code,
            Title = TitleFor(error.Category),
            Status = error.Category.ToHttpStatusCode(),
            Detail = isInternal ? InternalDetail : error.Message,
            Instance = instance,
        };

        problem.Extensions["code"] = error.Code;
        problem.Extensions["correlationId"] = correlationId;

        // Structured detail is the capability's own, and describes a business condition
        // the caller can act on — a SKU, a limit, an attempt count. It is withheld for
        // Internal for the same reason the message is.
        if (!isInternal && error.Data is { Count: > 0 })
        {
            foreach (var pair in error.Data)
            {
                problem.Extensions[pair.Key] = pair.Value;
            }
        }

        return problem;
    }

    /// <summary>
    /// The human-readable title for a category.
    /// </summary>
    /// <remarks>
    /// Fixed per category rather than taken from the error, because RFC 7807 says the
    /// title should not change between occurrences of the same problem type. The
    /// occurrence-specific part is <c>detail</c>.
    /// </remarks>
    public static string TitleFor(ErrorCategory category) => category switch
    {
        ErrorCategory.Validation => "The request is not valid",
        ErrorCategory.NotFound => "The resource was not found",
        ErrorCategory.Conflict => "The request conflicts with the current state",
        ErrorCategory.Forbidden => "The request is not permitted",
        ErrorCategory.Unavailable => "The service is temporarily unavailable",
        _ => "An unexpected error occurred",
    };
}
