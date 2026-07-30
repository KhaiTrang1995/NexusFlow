namespace FlowX;

/// <summary>
/// A flow plan violates an invariant that makes it unexecutable.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>defect</em>, not a business failure, which is why it is an
/// exception rather than an <see cref="Error"/> (see
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>:
/// results are for outcomes a caller could reasonably handle, and there is no
/// sensible handling for "the compiled graph has a gap in it").
/// </para>
/// <para>
/// In normal operation nobody sees this type: the analyzers reject these shapes at
/// build time. It exists because a plan can also be constructed programmatically —
/// by a test, by a future dynamic profile — and the guarantee the engine relies on
/// must hold on that path too.
/// </para>
/// </remarks>
public sealed class InvalidFlowPlanException : Exception
{
    /// <summary>Creates the exception with a message describing the violated invariant.</summary>
    public InvalidFlowPlanException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    public InvalidFlowPlanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message. Prefer an overload that explains the violation.</summary>
    public InvalidFlowPlanException()
    {
    }
}
