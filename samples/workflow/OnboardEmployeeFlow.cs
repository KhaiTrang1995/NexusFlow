using FlowX;

namespace Workflow;

/// <summary>
/// Onboards a new joiner: engage them, give them an account, provision them in parallel,
/// issue their kit one item at a time, find them a desk, screen them, welcome them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole shipped control-flow surface in one flow.</strong> Every method
/// <c>docs/08-Flow-Definition.md §4</c> lists as available in all profiles appears below —
/// <c>Step</c>, <c>CompensateWith</c>, <c>WithPolicy</c>, <c>Switch</c>/<c>Case</c>/
/// <c>Default</c>, <c>Fail</c>, <c>Parallel</c>, <c>ForEach</c>, <c>SubFlow</c>,
/// <c>When</c>/<c>Otherwise</c>, <c>Emit</c> and <c>Return</c> — and they are nested rather
/// than listed, because how they combine is the part a reader cannot get from the table.
/// </para>
/// <para>
/// <strong>What is not below, and why it is not.</strong> <c>AwaitSignal</c>, <c>Delay</c>
/// and <c>OnTimeout</c> — the three that would make this a multi-day process with human
/// waits — are absent, and the reason changed at WP-63. <c>Delay</c> and <c>OnTimeout</c>
/// are still absent because they do not work: there is no timer, and both are reported as
/// <c>FLOWX1031</c>. <c>AwaitSignal</c> works, and is absent from <em>this</em> flow for a
/// different reason: this flow carries an <c>[HttpTrigger]</c>, the generated endpoint
/// answers <c>200</c> with the projected output, and a suspended flow has no output to
/// project. The wait lives in <see cref="AcceptOfferFlow"/>, which has no trigger attribute
/// and two hand-written routes in <c>Program.cs</c>. The README's second section is the
/// account.
/// </para>
/// <para>
/// <strong>Everything below compiles to one flat <c>StepNode[]</c>.</strong> The conditional
/// is a <c>Branch</c> carrying its false target and a <c>Jump</c> closing the <c>then</c>
/// block; the switch is one node carrying a target per case plus a default, and a <c>Jump</c>
/// closing each case; the fork is one node carrying its branch entry points and its join; the
/// loop is one node carrying its join, with the body laid out once however many items the
/// collection holds. The engine holds no branch stack and never recurses — the index advances
/// by one or to a target, and every target is validated forward and in range when the graph
/// is built, which is why the loop is guaranteed to terminate
/// (<c>docs/06-Execution-Engine.md §3</c>).
/// </para>
/// <para>
/// <strong><c>Durable</c>, and it means something here.</strong> Each step boundary commits a
/// journal row under a lease's fencing token, so the unwind stack survives the process that
/// built it — which is what <c>samples/ecommerce</c> deliberately gives up by declaring
/// <c>Ephemeral</c>. The price is that this application cannot start without a journal: a
/// <c>Durable</c> flow on a host that registered none is refused with
/// <c>flow.durability_not_configured</c> before its first step, which is the right answer for
/// an unconfigured deployment and the reason <c>Program.cs</c> wires PostgreSQL.
/// </para>
/// </remarks>
[Flow("employee.onboard", Version = "2.0.0", Profile = ExecutionProfile.Durable, Owner = "people-ops")]
[FlowDeadline("PT60S")]
[HttpTrigger("POST", "/api/v1/onboarding", Idempotent = true)]
public sealed partial class OnboardEmployeeFlow : Flow<OnboardEmployee, OnboardingResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<OnboardEmployee, OnboardingResult> flow)
    {
        // CA1062, and samples/ecommerce answers it the same way. `Define` is a protected
        // override the framework calls, so the parameter cannot be null in practice — but
        // TreatWarningsAsErrors is on repository-wide and a guard here is cheaper than an
        // exemption that would have to be justified again in every future sample.
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateOffer>()

            // Branch on a value, not on a yes/no question. The selector is evaluated exactly
            // once and the cases are tested against it in declaration order with
            // EqualityComparer<TValue>.Default, so an enum is matched without being boxed.
            //
            // `Intern` matches no case. A switch with no Default would simply continue past
            // it — a branch nobody took does nothing — and here that would onboard someone
            // this flow has no engagement record for. Doing nothing is wrong, so the miss is
            // stated.
            //
            // It reads ctx.Input rather than ctx.Get<ValidatedOffer>(), and in a Durable flow
            // that is a deliberate preference rather than a style choice. Control transfers
            // are never journaled — the arm taken is re-derived by re-evaluating the selector,
            // which is what makes replay of control flow possible at all — and no state bag is
            // journaled yet either, so a resumed instance re-enters with only the input the
            // trigger re-seeds. A selector over a step's output cannot be re-derived after a
            // resume; one over the input can. See ResumeTests for what that costs the steps
            // that still bind step outputs.
            .Switch(ctx => ctx.Input.Employment)
                .Case(EmploymentType.Permanent, permanent => permanent
                    .Step<OpenPayrollRecord>().CompensateWith<ClosePayrollRecord>())
                .Case(EmploymentType.Contractor, contractor => contractor
                    .Step<SignSupplierAgreement>().CompensateWith<VoidSupplierAgreement>())

                // Terminal, and it unwinds. By the time this arm rejects, nothing compensable
                // has run — but that is a property of where it sits, not of `Fail`: the engine
                // is handed StepOutcome.Failed through the same call a declined dependency
                // comes back on, and cannot tell a deliberate rejection from an accident.
                .Default(unsupported => unsupported.Fail(OnboardingErrors.UnsupportedEmployment))

            // The policy set reaches flowx.manifest.json and this step's StepNode.Policies,
            // and stops there: the Policy Engine is P4, so nothing arms the timeout, the retry
            // or the breaker. Carried is not run — what the plan now says is what was declared,
            // which is the precondition for P4 executing it. What is real today is the
            // build-time check: the retry is only legal because identity.create declares
            // Idempotent = true, and FLOWX1014 would refuse it otherwise. See Policies.cs, and
            // WithPolicyTests, which pins both halves.
            .Step<CreateIdentity>().CompensateWith<DisableIdentity>()
                .WithPolicy(Policies.DirectoryService)

            // Three branches, two of which carry their own inverse. A compensation declared
            // inside a branch is registered when that branch's step completes and unwound in
            // the flow's own strict-reverse order, so a later failure anywhere undoes the
            // laptop and the access grant even though neither is on the main line.
            //
            // The three outputs are three different contracts because the state bag is keyed
            // by type: two branches declaring the same output would be two threads writing one
            // key, and FLOWX1013 refuses it at build time.
            .Parallel(p => p
                    .Branch(hardware => hardware
                        .Step<OrderLaptop>().CompensateWith<CancelLaptopOrder>())
                    .Branch(access => access
                        .Step<GrantSystemAccess>().CompensateWith<RevokeSystemAccess>())
                    .Branch<ScheduleInduction>(),
                merge: MergeStrategy.AllMustSucceed)

            // A loop whose body contains a conditional. The body is laid out once however many
            // items the collection holds, and the engine re-enters that span per element — so
            // the plan, the manifest and a rendered diagram are all independent of the size of
            // the data.
            //
            // MaxDegreeOfParallelism is 1 deliberately. Both arms of the conditional below
            // produce ApprovedEquipment, and the bag is keyed by type, so a bound above one
            // would have two elements racing for the same slot: the runtime guarantees the
            // failure mode is a wrong value and never a corrupted dictionary, which is not a
            // guarantee worth accepting in a sample. It is the same modelling question
            // FLOWX1013 asks of the fork above, and here it is answered by not forking.
            //
            // And note what equipment.assign binds: EquipmentRequest, the element — not the
            // ApprovedEquipment the arm above it just produced. A compensation is invoked
            // with the input of the step it undoes, and it runs after the loop has ended,
            // when the shared bag holds only the last element's copy. Binding the arm's
            // output here would make both undos return the same item and leave the other one
            // outstanding, with every step reporting success. Only the element is scoped to
            // its iteration; a compensable step inside a loop must bind it.
            .ForEach(
                ctx => ctx.Input.Equipment,
                item => item
                    .When(ctx => ctx.Get<EquipmentRequest>().NeedsApproval, needsApproval => needsApproval
                        .Step<RecordEquipmentApproval>())
                    .Otherwise(cleared => cleared
                        .Step<AutoClearEquipment>())

                    // Binds the element, for the reason above. Its undo is recorded with the
                    // scope it completed in, so it returns the item its own assign assigned
                    // rather than whichever item the loop ended on.
                    .Step<AssignEquipment>().CompensateWith<ReturnEquipment>(),
                options: new ForEachOptions { MaxDegreeOfParallelism = 1 })

            // One node in this graph, and a whole flow of its own. The child's steps are not
            // here; its result does not come back. What crosses the boundary is failure, in
            // both directions: the child's failure fails this flow, and this flow's later
            // failure unwinds the child's completed steps at the position the composition
            // occupies in this flow's stack.
            .SubFlow<ProvisionWorkspaceFlow, ProvisionWorkspace>(ctx => new ProvisionWorkspace(
                ctx.Get<Identity>().EmployeeId,
                ctx.Input.Site))

            // The second conditional, at the top level this time, so the trace shows a Branch
            // that is not inside anything.
            .When(ctx => ctx.Input.RequiresBackgroundCheck, screened => screened
                .Step<StartBackgroundCheck>())
            .Otherwise(waived => waived
                .Step<WaiveBackgroundCheck>())

            .Step<SendWelcomePack>()

            // Staged in this step's own transaction, because this flow is Durable and so has
            // a transaction to stage into. That is the condition FLOWX1024 checks, and it is
            // why this line needs no suppression where samples/ecommerce's does. The gap is
            // still the broker: IEventPublisher is declared and no plugin implements it, so
            // "published" today means "written to the outbox and handed to a publisher".
            .Emit<EmployeeOnboarded>(ctx => new EmployeeOnboarded(
                ctx.Get<Identity>().EmployeeId,
                ctx.Input.Site,
                ctx.Input.Equipment.Count))

            // The desk is not in here, and cannot be: the workspace flow allocated it and a
            // sub-flow's result does not flow into its parent's context. The projection can
            // only name what this flow's own steps produced.
            .Return(ctx => new OnboardingResult(
                ctx.Get<Identity>().EmployeeId,
                ctx.Input.Equipment.Count));
    }
}
