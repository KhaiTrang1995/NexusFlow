using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What the engine does with a declared <c>Audit</c>: stage 7's other half, executed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is the inversion of one assertion and the arrival of the rest.</strong>
/// <c>PolicyExecutionTests.AnAuditIsAStageSevenPolicyAndStillExecutesNowhere</c> asserted that
/// an <c>Audit</c> reached no flag and no code path, and
/// <c>docs/diagnostics/FLOWX1032.md</c> named it as the test that would go red on the day the
/// policy ran. It went red; what replaced it is below.
/// </para>
/// <para>
/// <strong>Two things have to be proved and they are different.</strong> That a record is
/// written at all — an audit that records nothing is indistinguishable from the inert version
/// it replaces — and that the record answers the question
/// <a href="../../docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a> left
/// open: *"an auditor reconstructing 'who authorised this transfer' must read two events.
/// Nothing yet writes those events."* <see cref="TheTrailNamesTheStarterAndTheDelivererApart"/>
/// writes both and reads them apart.
/// </para>
/// <para>
/// <strong>And that a <c>[Sensitive]</c> member cannot reach a record.</strong> That is not a
/// property of care taken here: the record carries a <c>JournalPayload</c>, whose only exit
/// redacts, and <c>redact</c> is a longer list handed to the same pass.
/// <see cref="AMarkedMemberIsRedactedOutOfTheRecordAndSoIsTheDeclaredRedactList"/> is what
/// stops that becoming false quietly.
/// </para>
/// </remarks>
public sealed partial class AuditPolicyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A step's result, with one member the flow marks sensitive.</summary>
    private sealed record Posted(string EntryId, string DebtorIban, decimal Amount);

    /// <summary>A step's input, so the composed record has a <c>request</c> as well.</summary>
    private sealed record Instruction(string Reference, decimal Amount);

    [JsonSerializable(typeof(Posted))]
    [JsonSerializable(typeof(Instruction))]
    private sealed partial class AuditJson : JsonSerializerContext;

    /// <summary>
    /// The dispatcher's half, written by hand exactly as the generator emits it.
    /// </summary>
    /// <remarks>
    /// The composition is <c>JournalPayload.OfState</c> over a <c>request</c> and a
    /// <c>result</c> member, given the flow's <c>SensitiveMembers</c> <em>and</em> the declared
    /// <c>redact</c> list. Writing it by hand first is what proves the seam is implementable —
    /// the same reason every other <c>IStepDispatcher</c> member was hand-written before the
    /// generator emitted it.
    /// </remarks>
    private static JournalPayload Describe(IReadOnlyList<string> redact) =>
        JournalPayload.OfState(
            [
                JournalMember.Of("request", new Instruction("ref-1", 42m), AuditJson.Default),
                JournalMember.Of("result", new Posted("entry-1", "GB33BUKB20201555555555", 42m), AuditJson.Default),
            ],
            [.. SensitiveMembers, .. redact]);

    /// <summary>What the flow's own contracts declare, as the generator emits it.</summary>
    private static readonly string[] SensitiveMembers = ["DebtorIban"];

    private static ExecutionPlan Plan(PolicySet set, ExecutionProfile profile = ExecutionProfile.Ephemeral) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("ledger.post", "1.0.0", profile, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(
                    0, Plans.Validate, policies: PolicyChain.ForStep(set, Plans.Validate)),
                StepNode.ForCapability(1, Plans.Capture),
            ]));

    private static PolicySet Financial =>
        PolicySet.Named("ledger-post").Audit("financial", "Amount");

    // ------------------------------------------------------------------ it writes something

    /// <summary>
    /// A step declaring an <c>Audit</c> produces a record, and a step beside it does not.
    /// </summary>
    /// <remarks>
    /// Both halves. A test asserting only "one record exists" would pass against an engine that
    /// audited every step it ran, which is a different feature and a much worse one — the trail
    /// would then say nothing about which steps an author thought were worth recording.
    /// </remarks>
    [Fact]
    public async Task AnAuditedStepProducesARecordAndAnUnauditedOneDoesNot()
    {
        var sink = new RecordingAuditSink();
        var plan = Plan(Financial);

        plan.HasAuditedSteps.ShouldBeTrue(
            "The plan-level gate has to be true or the step loop never looks at the node.");

        var result = await new FlowEngine(new FakeClock(T0), audit: sink)
            .ExecuteAsync(plan, new RecordingDispatcher(), Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        sink.Records.Count.ShouldBe(
            1,
            "Two steps ran and one declared an Audit. A record per step would make the policy " +
            "a setting rather than a declaration.");

        var record = sink.Records[0];

        record.Category.ShouldBe("financial");
        record.CapabilityId.ShouldBe(Plans.Validate.Id);
        record.StepIndex.ShouldBe(0);
        record.FlowId.ShouldBe("ledger.post");
        record.FlowVersion.ShouldBe("1.0.0");
        record.CorrelationId.ShouldBe("corr-1");
        record.IdempotencyKey.ShouldBe("idem-1");
        record.TenantId.ShouldBe("acme");
        record.RecordedAt.ShouldBe(T0);
    }

    /// <summary>A step that failed is not audited, because stage 7 follows execution.</summary>
    /// <remarks>
    /// <c>docs/10 §2</c>'s table gives the reason one row up: "compensation registered before
    /// the step succeeds → compensating something that never happened". An audit of a step that
    /// did not happen is the same mistake with a different noun, and a compliance query
    /// counting records would count it.
    /// </remarks>
    [Fact]
    public async Task AFailedStepIsNotAudited()
    {
        var sink = new RecordingAuditSink();

        var dispatcher = new RecordingDispatcher()
            .FailAt(0, new Error("ledger.rejected", "declined", ErrorCategory.Validation));

        var result = await new FlowEngine(new FakeClock(T0), audit: sink)
            .ExecuteAsync(Plan(Financial), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();

        sink.Records.ShouldBeEmpty(
            "Audit is a Consistency policy: it records what happened, and nothing happened.");
    }

    /// <summary>
    /// A declared <c>Audit</c> with nowhere to write it fails the step rather than skipping it.
    /// </summary>
    /// <remarks>
    /// The one seam on this path that does not degrade, and the reason is
    /// <c>FLOWX1032</c>'s third remedy: "do not ship this flow on this release, if the step
    /// genuinely cannot run without the policy — a regulated write whose audit record is the
    /// reason it is allowed to happen". An engine that quietly ran the step would put the
    /// deployment back in the state the whole feature exists to end, with no diagnostic left to
    /// raise it.
    /// </remarks>
    [Fact]
    public async Task AnAuditWithNoSinkRefusesTheFlow()
    {
        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(Financial), new RecordingDispatcher(), Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();

        result.Error!.Code.ShouldBe(
            FlowErrors.AuditSinkNotConfiguredCode,
            "A missing sink is a wiring defect in the host, named as one.");
    }

    /// <summary>A sink that refuses fails the flow, and the audited step unwinds.</summary>
    /// <remarks>
    /// The record is written after the step's commit and after it joins the compensation stack,
    /// so a refusal here undoes the very step it was going to describe. That is the correct
    /// end: the effect is reversed rather than left standing with nothing describing it.
    /// </remarks>
    [Fact]
    public async Task ASinkThatRefusesUnwindsTheStepItWouldHaveDescribed()
    {
        var sink = new RecordingAuditSink { Refusal = new InvalidOperationException("the log is full") };

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("ledger.post", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(
                    0,
                    Plans.Reserve,
                    Plans.Release,
                    policies: PolicyChain.ForStep(Financial, Plans.Reserve)),
            ]));

        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0), audit: sink)
            .ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();

        result.Error!.Code.ShouldBe(FlowErrors.AuditNotRecordedCode);

        dispatcher.Compensated.ShouldBe(
            [0],
            "The step happened and cannot be described, so it is taken back. An audit that " +
            "may be dropped is not an audit trail.");
    }

    // ------------------------------------------------------------------- redact means something

    /// <summary>
    /// A <c>[Sensitive]</c> member and a declared <c>redact</c> member are both replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the answer to the objection that had stage 7 declined twice.</strong>
    /// <c>PLAN §6a</c>: "an audit record the engine can write carries no payload, which makes
    /// <c>redact</c> vacuous". The record carries the journal's own payload, and the
    /// <c>redact</c> list reaches the one redaction pass FlowX has — so <c>Amount</c>, which no
    /// contract marks, is stripped because this policy said to, and <c>DebtorIban</c> is
    /// stripped because the flow's contract did.
    /// </para>
    /// <para>
    /// <strong>Both are asserted, and so is the member that survives.</strong> A test that only
    /// checked for the absence of two values would pass against a record that carried nothing
    /// at all, which is exactly the state being fixed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AMarkedMemberIsRedactedOutOfTheRecordAndSoIsTheDeclaredRedactList()
    {
        var sink = new RecordingAuditSink();

        var dispatcher = new RecordingDispatcher
        {
            Audit = (_, _, redact) => Describe(redact),
        };

        var result = await new FlowEngine(new FakeClock(T0), audit: sink)
            .ExecuteAsync(Plan(Financial), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        dispatcher.RedactionsAsked.Single().ShouldBe(
            ["Amount"],
            "The list the author wrote reaches the payload builder. If it did not, everything " +
            "below would pass for the wrong reason.");

        var document = sink.Records.Single().Payload.ToJson()!;

        using var parsed = JsonDocument.Parse(document);

        var result0 = parsed.RootElement.GetProperty("result");

        result0.GetProperty("DebtorIban").GetString().ShouldBe(
            JournalPayload.Redacted,
            "The flow marks it [Sensitive]. There is one redaction pass and the record goes " +
            "through it — the record is not a second exit from a value.");

        result0.GetProperty("Amount").GetString().ShouldBe(
            JournalPayload.Redacted,
            "Nothing marks Amount sensitive; the policy's redact list named it. This is what " +
            "redact means, and it is the half that could not exist while the record was empty.");

        result0.GetProperty("EntryId").GetString().ShouldBe(
            "entry-1",
            "And the rest of the record survives, or the two assertions above would hold for a " +
            "record that carried nothing.");

        parsed.RootElement.GetProperty("request").GetProperty("Amount").GetString().ShouldBe(
            JournalPayload.Redacted,
            "Matched at every depth, like every other redaction: a marked member inside the " +
            "request is not less sensitive for being one level down.");

        document.Contains("GB33BUKB20201555555555", StringComparison.Ordinal).ShouldBeFalse(
            "And the literal value reaches no sink, which is the assertion an auditor of this " +
            "control actually wants.");
    }

    // ------------------------------------------------------------------- the two principals

    /// <summary>
    /// A trail across a wait names the starter on one record and the deliverer on the next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>ADR-0028 §3's first negative, closed.</strong> That record accepted that "a
    /// durable flow's authorisation is discontinuous across a wait. The steps before are
    /// decided against the starter, the steps after against the deliverer … an auditor
    /// reconstructing 'who authorised this transfer' must read two events. Nothing yet writes
    /// those events." This writes both, and reads two different names and two different
    /// authorities off them.
    /// </para>
    /// <para>
    /// <strong>The authority is what makes it answerable without knowing the flow.</strong>
    /// Two records naming two people is only evidence of a discontinuity to a reader who
    /// already knows there is a wait between step 0 and step 2. <c>Starter</c> and
    /// <c>Deliverer</c> say which is which, so "who authorised this transfer" is a query over
    /// the trail rather than a reconstruction from the graph.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheTrailNamesTheStarterAndTheDelivererApart()
    {
        var journal = new InMemoryFlowJournal();
        var sink = new RecordingAuditSink();
        var instanceId = Guid.NewGuid();

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("transfer.execute", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30)),
            StepGraph.Create([
                StepNode.ForCapability(
                    0, Plans.Validate, policies: PolicyChain.ForStep(Financial, Plans.Validate)),
                StepNode.ForAwaitSignal(1, "transfer.approved", TimeSpan.FromDays(7)),
                StepNode.ForCapability(
                    2, Plans.Capture, policies: PolicyChain.ForStep(Financial, Plans.Capture)),
            ]));

        var engine = new FlowEngine(new FakeClock(T0), audit: sink);

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Started, instanceId, new FencingToken(1), cancellationToken: Ct);

        begun.IsSuccess.ShouldBeTrue();

        var first = await engine.ExecuteAsync(
            plan, new RecordingDispatcher(), Started, begun.Value, Ct);

        first.IsSuspended.ShouldBeTrue("the flow parked at the wait");

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), Ct);

        resumed.IsSuccess.ShouldBeTrue();

        var second = await engine.ExecuteAsync(
            plan,
            new RecordingDispatcher(),
            Delivered,
            resumed.Value.WithSignal(FlowSignal.Of("transfer.approved", new Approval("ok"))),
            Ct);

        second.IsSuccess.ShouldBeTrue(second.Error?.ToString());

        sink.Records.Count.ShouldBe(2, "one audited step before the wait, one after it");

        sink.Records[0].Authority.ShouldBe(AuditAuthority.Starter);
        sink.Records[0].Principal.ShouldBe("ada");

        sink.Records[1].Authority.ShouldBe(
            AuditAuthority.Deliverer,
            "The execution rehydrated a committed frontier, so its principal can only have " +
            "come from SignalAsync — the journal row deliberately keeps no claims (ADR-0028 " +
            "§2.2), so there is nowhere else for one to have come from.");

        sink.Records[1].Principal.ShouldBe(
            "grace",
            "Two answers to 'who authorised this transfer', and the trail now has both.");

        sink.Records.Select(r => r.InstanceId).Distinct().Single().ShouldBe(
            instanceId.ToString(),
            "and they are tied to one instance, which is what makes it a query.");
    }

    /// <summary>A sweep is recorded as the platform, not as an anonymous caller.</summary>
    /// <remarks>
    /// The ordering inside <c>AuthorityOf</c> is the control. A timer sweep and a recovery scan
    /// carry no principal by construction (ADR-0028 §2.3 — <c>IsContinuation</c> is set in one
    /// place and only where there is neither a signal nor a principal), so asking about the
    /// principal first would file them under <c>Anonymous</c> and make "a step ran for nobody"
    /// ambiguous between a public step and a sweep.
    /// </remarks>
    [Fact]
    public async Task AContinuationIsRecordedAsThePlatform()
    {
        var sink = new RecordingAuditSink();

        var result = await new FlowEngine(new FakeClock(T0), audit: sink).ExecuteAsync(
            Plan(Financial),
            new RecordingDispatcher(),
            new FlowInvocation("corr", "idem", Principal: null, IsContinuation: true),
            Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        sink.Records.Single().Authority.ShouldBe(AuditAuthority.Platform);
        sink.Records.Single().Principal.ShouldBeNull();
    }

    /// <summary>An unauthenticated principal is anonymous, and the record says which stance.</summary>
    /// <remarks>
    /// The <c>principal?.Identity?.IsAuthenticated == true</c> question ADR-0028 §2.1 makes the
    /// difference between a control that holds and one that fails open, asked here for the
    /// record rather than for the decision: a non-null <c>ClaimsPrincipal</c> that no scheme
    /// authenticated must not be recorded as somebody.
    /// </remarks>
    [Fact]
    public async Task ANonNullButUnauthenticatedPrincipalIsRecordedAsAnonymous()
    {
        var sink = new RecordingAuditSink();

        var result = await new FlowEngine(new FakeClock(T0), audit: sink).ExecuteAsync(
            Plan(Financial),
            new RecordingDispatcher(),
            new FlowInvocation("corr", "idem", Principal: new ClaimsPrincipal(new ClaimsIdentity())),
            Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        sink.Records.Single().Authority.ShouldBe(AuditAuthority.Anonymous);
        sink.Records.Single().Principal.ShouldBeNull();
    }

    private sealed record Approval(string Verdict);

    private static FlowInvocation Started =>
        new("corr", "idem", TenantId: "acme", Principal: Named("ada"));

    private static FlowInvocation Delivered =>
        new("corr", "idem", TenantId: "acme", Principal: Named("grace"));

    private static ClaimsPrincipal Named(string name) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], authenticationType: "Test"));
}
