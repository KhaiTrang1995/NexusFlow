using FlowX;

namespace Workflow;

/// <summary>
/// Gives a new joiner somewhere to sit and a way to get in: allocate a desk, issue a pass.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A flow in its own right, composed by another.</strong> Nothing here knows it has a
/// parent. It has its own id, its own version, its own deadline and — because it declares
/// <c>Durable</c> — its own <c>flow_instance</c> row when a durable parent composes it. That
/// is what makes <c>.SubFlow&lt;&gt;</c> composition rather than inlining: the parent's
/// compiled graph carries one <c>StepKind.SubFlow</c> node naming this flow's id, and none of
/// the steps below appear in it. A parent that composed a hundred-step child would still be
/// the size of the flow its author wrote.
/// </para>
/// <para>
/// <strong>Its budget is <c>min(parent's remaining, PT20S)</c>.</strong> Composition can only
/// ever shorten: a child cannot buy time its parent does not have, and a parent cannot buy
/// the child more than this line allows.
/// </para>
/// <para>
/// <strong>Both steps are compensable, and the first carries the only policy kind the engine
/// knows how to execute — which still does not execute, because the generator drops the
/// chain before it reaches the plan (see <see cref="Policies"/>).</strong> If
/// <c>workspace.issue_pass</c> fails, this flow unwinds
/// <c>workspace.release_desk</c> itself. If it succeeds and the <em>parent</em> later fails,
/// the parent unwinds both of them, in reverse, at the position the composition occupies in
/// the parent's own stack — which is the guarantee that stops extracting steps into a
/// sub-flow from silently weakening a saga.
/// </para>
/// </remarks>
[Flow("workspace.provision", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "facilities")]
[FlowDeadline("PT20S")]
public sealed partial class ProvisionWorkspaceFlow : Flow<ProvisionWorkspace, WorkspaceReady>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ProvisionWorkspace, WorkspaceReady> flow)
    {
        // CA1062 on a protected override the framework calls. samples/ecommerce takes the
        // same line: the guard is cheap, it is what the analyzer asks for repository-wide,
        // and a builder that arrived null would otherwise be a NullReferenceException inside
        // a generated file rather than at the flow that declared it.
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<AllocateDesk>().CompensateWith<ReleaseDesk>()
                .WithPolicy(Policies.FacilitiesUndo)
            .Step<IssueBuildingPass>().CompensateWith<CancelBuildingPass>()
            .Return(ctx => new WorkspaceReady(
                ctx.Get<DeskAllocation>().DeskId,
                ctx.Get<BuildingPass>().PassId));
    }
}
