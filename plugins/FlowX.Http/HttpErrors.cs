namespace FlowX.Http;

/// <summary>
/// Errors the transport itself produces, as opposed to those a capability returns.
/// </summary>
/// <remarks>
/// All <see cref="ErrorCategory.Validation"/>, so
/// <see cref="ProblemDetailsMapper"/> maps them to <c>400</c> without this class
/// knowing an HTTP status code exists. That indirection is the point: the category is
/// the contract, and the mapping lives in one place.
/// </remarks>
public static class HttpErrors
{
    /// <summary>The request body was not valid JSON, or did not match the input contract.</summary>
    /// <param name="reason">The parser's own account of what went wrong.</param>
    public static Error MalformedBody(string reason) =>
        new Error(
            "http.malformed_body",
            "The request body could not be read as the flow's input contract.",
            ErrorCategory.Validation)
            .With("reason", reason);

    /// <summary>The request had no body, or a body of literal <c>null</c>.</summary>
    public static Error MissingBody() =>
        new Error(
            "http.missing_body",
            "This endpoint requires a JSON request body carrying the flow's input.",
            ErrorCategory.Validation);
}
