using FlowX.Conformance.InMemory;

namespace FlowX.Conformance;

/// <summary>
/// Runs the whole journal suite against the reference store.
/// </summary>
/// <remarks>
/// Four lines, and that is the point: claiming conformance is deriving and supplying a store.
/// A store author who has to edit the suite to make it pass has found a disagreement about
/// the contract, which is a conversation rather than an override.
/// </remarks>
public sealed class InMemoryJournalConformanceTests : JournalConformance
{
    /// <inheritdoc />
    protected override ValueTask<IFlowJournal> CreateJournalAsync() =>
        new(new InMemoryFlowJournal());
}

/// <summary>Runs the whole lease suite against the reference store.</summary>
public sealed class InMemoryLeaseStoreConformanceTests : LeaseStoreConformance
{
    /// <inheritdoc />
    protected override ValueTask<ILeaseStore> CreateStoreAsync() =>
        new(new InMemoryLeaseStore());
}
