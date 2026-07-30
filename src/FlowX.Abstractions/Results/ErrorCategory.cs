namespace FlowX;

/// <summary>
/// The closed set of failure kinds. This is the single mapping point to every
/// transport: HTTP status, gRPC status, retryability and dead-lettering all derive
/// from it (see docs/04-Core-Concepts.md §8).
/// </summary>
/// <remarks>
/// The set is closed on purpose (ADR-0009 §"not extensible"). Add error
/// <em>codes</em>, never categories — a new category would silently change the
/// transport mapping table for every existing consumer.
/// </remarks>
public enum ErrorCategory
{
    /// <summary>Input is malformed or violates a stated rule. HTTP 400. Never retried.</summary>
    Validation = 0,

    /// <summary>The addressed resource does not exist. HTTP 404. Never retried.</summary>
    NotFound = 1,

    /// <summary>State prevents the operation right now. HTTP 409. Retried with backoff.</summary>
    Conflict = 2,

    /// <summary>The principal is not permitted. HTTP 403. Never retried; always audited.</summary>
    Forbidden = 3,

    /// <summary>A dependency is unreachable or overloaded. HTTP 503. Retried with backoff.</summary>
    Unavailable = 4,

    /// <summary>An unexpected fault. HTTP 500. Retried, then dead-lettered.</summary>
    Internal = 5,
}

/// <summary>Retry and transport semantics derived from <see cref="ErrorCategory"/>.</summary>
public static class ErrorCategoryExtensions
{
    /// <summary>
    /// True when no amount of retrying can change the outcome. Terminal errors are
    /// dead-lettered immediately rather than blocking a partition
    /// (docs/09-Trigger-Model.md §7).
    /// </summary>
    public static bool IsTerminal(this ErrorCategory category) => category switch
    {
        ErrorCategory.Validation => true,
        ErrorCategory.NotFound => true,
        ErrorCategory.Forbidden => true,
        _ => false,
    };

    /// <summary>The inverse of <see cref="IsTerminal"/>, stated positively for call sites.</summary>
    public static bool IsRetryable(this ErrorCategory category) => !category.IsTerminal();

    /// <summary>The canonical HTTP status for this category.</summary>
    public static int ToHttpStatusCode(this ErrorCategory category) => category switch
    {
        ErrorCategory.Validation => 400,
        ErrorCategory.NotFound => 404,
        ErrorCategory.Conflict => 409,
        ErrorCategory.Forbidden => 403,
        ErrorCategory.Unavailable => 503,
        _ => 500,
    };
}
