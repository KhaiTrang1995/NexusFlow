using FlowX;

namespace Workflow.Tests;

/// <summary>Thrown by <see cref="NodeDiesJournal"/> in place of a node disappearing.</summary>
/// <remarks>
/// An exception rather than a refused commit, because a refusal is a thing the engine handles
/// — it fails the flow and unwinds — and a node that dies does not get to unwind. Killing the
/// process is the faithful simulation and is not available to a test in the same process, so
/// this throws out of the step loop instead: the flow stops where it is, the journal keeps
/// everything committed up to that point, and nothing compensates.
/// </remarks>
public sealed class NodeDiedException : Exception
{
    /// <summary>The node died writing the given commit.</summary>
    /// <param name="commit">Which commit it did not survive.</param>
    public NodeDiedException(int commit)
        : base($"The node died writing commit {commit}.")
    {
    }

    /// <summary>Creates the exception with a default message.</summary>
    public NodeDiedException()
        : base("The node died.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What happened.</param>
    public NodeDiedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">What caused it.</param>
    public NodeDiedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A journal that stops being reachable partway through a flow.
/// </summary>
/// <remarks>
/// <para>
/// Wraps a real store rather than replacing one, so every row this test reasons about was
/// written by the conformance suite's reference implementation and is readable afterwards
/// exactly as a surviving node would read it.
/// </para>
/// <para>
/// The counter is over <em>commits</em>, in the order the engine makes them — which for a
/// flow that forks is partly the scheduler's order. Tests using it must therefore choose a
/// number whose position is stable regardless of how the fork interleaves, which is why the
/// resume test dies on the commit after the sub-flow rather than on one inside the fork.
/// </para>
/// </remarks>
internal sealed class NodeDiesJournal : IFlowJournal
{
    private readonly IFlowJournal _inner;
    private int _commits;

    internal NodeDiesJournal(IFlowJournal inner) => _inner = inner;

    /// <summary>Which commit the node does not survive, or null once it is well again.</summary>
    internal int? DiesOnCommit { get; set; }

    /// <summary>How many commits have been attempted.</summary>
    internal int Commits => _commits;

    /// <inheritdoc />
    public ValueTask<Result<JournalStep>> CommitAsync(
        StepCommit commit, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _commits) == DiesOnCommit)
        {
            throw new NodeDiedException(_commits);
        }

        return _inner.CommitAsync(commit, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> StartAsync(
        FlowInstanceStart start, CancellationToken cancellationToken) =>
        _inner.StartAsync(start, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<FencingToken>> FenceAsync(
        Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
        _inner.FenceAsync(instanceId, token, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
        Guid instanceId,
        FencingToken token,
        FlowInstanceState state,
        JournalPayload stateBag,
        FlowWake? wake,
        CancellationToken cancellationToken) =>
        _inner.CompleteAsync(instanceId, token, state, stateBag, wake, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        _inner.ReadInstanceAsync(instanceId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        _inner.ReadResumeFrontierAsync(instanceId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        _inner.ReadOutboxAsync(instanceId, cancellationToken);
}
