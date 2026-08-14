using System;
using System.Collections.Generic;
using System.Linq;

namespace FlowX.Functions.Model;

/// <summary>Which platform binding one declared trigger becomes.</summary>
/// <remarks>
/// Not <c>TriggerKind</c> renamed. A kind is what the flow declared; this is what a
/// scaled-to-zero host can serve, and the two differ in exactly one place —
/// <see cref="Timer"/> carries both <c>Schedule</c> and <c>Change</c>, because a host that
/// holds no connection between invocations cannot <c>LISTEN</c> and reads the cursor on the
/// platform's clock instead.
/// </remarks>
public enum FunctionBinding
{
    /// <summary>An HTTP request, from <c>[HttpTrigger]</c> or <c>[AgentTrigger]</c>.</summary>
    Http,

    /// <summary>A Service Bus message, from a bus trigger.</summary>
    ServiceBus,

    /// <summary>A platform timer, from <c>[CronTrigger]</c> or <c>[ChangeTrigger]</c>.</summary>
    Timer,
}

/// <summary>What the timer entry point does when it fires.</summary>
public enum TimerPass
{
    /// <summary>One schedule pass — <c>FlowScheduleScan.RunOnceAsync</c>.</summary>
    Schedule,

    /// <summary>One change pass — <c>FlowChangeScan.RunOnceAsync</c>.</summary>
    Change,
}

/// <summary>
/// One <c>[Function]</c> entry point, expressed without a single Roslyn type.
/// </summary>
/// <param name="Name">
/// The function's name, which is what the platform's portal, its logs and its scaling rules
/// address. Derived from the flow's type name and the binding, and unique within the assembly.
/// </param>
/// <param name="Binding">Which platform trigger reaches it.</param>
/// <param name="FlowId">The flow's business identity, for the summary and the bus lookup.</param>
/// <param name="FlowTypeName">
/// The flow's full type name, whose generated partial carries <c>Plan</c>, <c>Dispatcher</c>
/// and <c>Projection</c> — the three things this entry point hands the runtime.
/// </param>
/// <param name="InputTypeName">The flow's input contract, for an <see cref="FunctionBinding.Http"/>.</param>
/// <param name="OutputTypeName">The flow's output contract, for an <see cref="FunctionBinding.Http"/>.</param>
/// <param name="Method">The declared HTTP method, lower-cased as the platform wants it.</param>
/// <param name="Route">The declared route, without its leading slash, as the platform wants it.</param>
/// <param name="Topic">The declared topic, for a <see cref="FunctionBinding.ServiceBus"/>.</param>
/// <param name="Group">The declared consumer group, which names the subscription.</param>
/// <param name="Idempotent">
/// Whether the declaration demands an <c>Idempotency-Key</c> header. Copied off the trigger
/// rather than defaulted, because a mutating endpoint that declares the rule and does not
/// enforce it is the failure <c>EveryMutatingHttpFlowRequiresAnIdempotencyKey</c> exists to
/// catch — and it would catch the declaration, not this.
/// </param>
/// <param name="JsonContextName">
/// The full type name of the <c>JsonSerializerContext</c> this assembly declares, which is
/// where both of an HTTP flow's contracts are serialised from. Read from the compilation
/// rather than assumed, for the reason <c>FlowPlanGenerator</c> reads it: a worker publishes
/// trimmed, and a reflective serialiser is what a trimmed publish removes.
/// </param>
public sealed record FunctionModel(
    string Name,
    FunctionBinding Binding,
    string FlowId,
    string FlowTypeName,
    string? InputTypeName = null,
    string? OutputTypeName = null,
    string? Method = null,
    string? Route = null,
    string? Topic = null,
    string? Group = null,
    bool Idempotent = false,
    string? JsonContextName = null)
{
    /// <summary>
    /// How often the platform wakes a host so a sweep can look, in NCronTab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One minute, and it is deliberately not the declared expression.</strong>
    /// Translating <c>0 2 * * *</c> into a <c>TimerTrigger</c> would put a second copy of the
    /// schedule in the deployment — and the two would not merely drift, they would disagree
    /// about zones: <c>[CronTrigger]</c> carries an IANA <c>TimeZone</c> that the platform's
    /// timer has no field for, so a Berlin 02:00 would fire at 02:00 UTC and be an hour wrong
    /// for half the year. ADR-0031 also derives every occurrence's instance id from the
    /// declared expression, so a firing at the wrong minute is not a late run of the right
    /// occurrence — it is a different one.
    /// </para>
    /// <para>
    /// So the platform's timer says only "you may look now" and the declaration decides what is
    /// due. That costs an invocation a minute per deployment and buys one copy of the schedule.
    /// </para>
    /// </remarks>
    public const string PollingCron = "0 * * * * *";

}

/// <summary>
/// One timer entry point, serving every flow whose declaration that sweep fires.
/// </summary>
/// <param name="Pass">Which sweep the firing runs.</param>
/// <param name="FlowIds">
/// The flows whose declarations that sweep serves, ordinally sorted. Carried for the summary
/// alone — the sweep reads the catalogue, not this list.
/// </param>
/// <remarks>
/// <para>
/// <strong>One function per sweep, not one per flow, and the difference is real cost.</strong>
/// <c>FlowScheduleScan.RunOnceAsync</c> is a pass over every registered schedule; a timer per
/// <c>[CronTrigger]</c> would run that same whole pass once per declaration per minute — ten
/// scheduled flows would be ten passes a minute doing nine tenths of nothing, each one a
/// billed invocation and a set of queries. What decides that a given occurrence is due is the
/// declaration the sweep reads, so a second timer adds no reachability at all.
/// </para>
/// <para>
/// It also removes a way to be wrong: ADR-0031 makes a second firing of one occurrence inert
/// through the derived instance id, so ten timers would have been safe and wasteful rather
/// than incorrect — which is exactly the kind of defect nobody finds, because nothing breaks.
/// </para>
/// </remarks>
public sealed record TimerFunctionModel(TimerPass Pass, IReadOnlyList<string> FlowIds)
{
    /// <summary>The function's name, which is what the platform addresses it by.</summary>
    /// <remarks>
    /// Fixed rather than derived from the assembly, unlike the class around it: there is one
    /// of each per worker, and a name a deployment can predict is one an operator can find in
    /// the portal without reading generated source.
    /// </remarks>
    public string Name => Pass == TimerPass.Change ? "FlowXChangePass" : "FlowXSchedulePass";
}

/// <summary>
/// A declared trigger this package deliberately generates no entry point for, and why.
/// </summary>
/// <param name="FlowId">The flow that declared it.</param>
/// <param name="Kind">The declared kind, as the manifest spells it.</param>
/// <param name="Reason">
/// Why nothing is bound, written for whoever opens the generated file looking for the entry
/// point they expected. This is the whole point of the record existing: a kind that produced
/// silence would look identical to a generator that had not run.
/// </param>
public sealed record UnboundTrigger(string FlowId, string Kind, string Reason);
