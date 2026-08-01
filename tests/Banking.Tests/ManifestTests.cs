using System.Text.Json;
using Banking;
using FlowX;
using FlowX.Generated;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// The manifest the sample's build produced, and the compiled plan beside it.
/// </summary>
/// <remarks>
/// Asserted against the generated constant rather than a file, because that is what the
/// build actually emits — <c>flowx manifest</c> only copies it to disk. The two assertions
/// at the end are the important ones: they are where the published document and the running
/// program are compared, and they are what stop this sample's README from drifting into a
/// claim the runtime does not keep.
/// </remarks>
public sealed class ManifestTests
{
    private static readonly JsonDocument Manifest = JsonDocument.Parse(FlowXManifest.Json);

    private static JsonElement Flow => Manifest.RootElement.GetProperty("flows")[0];

    private static Dictionary<string, JsonElement> Capabilities =>
        Manifest.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c);

    [Fact]
    public void NamesTheFlowItsProfileAndItsDeadline()
    {
        Flow.GetProperty("id").GetString().ShouldBe("transfer.execute");
        Flow.GetProperty("version").GetString().ShouldBe("1.0.0");
        Flow.GetProperty("profile").GetString().ShouldBe("Durable");
        Flow.GetProperty("deadline").GetString().ShouldBe("PT60S");
    }

    [Fact]
    public void RecordsBothAccountNumbersAsSensitive()
    {
        Flow.GetProperty("input").GetProperty("sensitive").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(["CreditorIban", "DebtorIban"]);

        // The output carries two ledger references and no account, so there is nothing to
        // mark. Present-and-absent is a statement rather than an omission.
        Flow.GetProperty("output").TryGetProperty("sensitive", out _).ShouldBeFalse();
    }

    [Fact]
    public void PublishesTheAddressTheFlowDeclares()
    {
        var trigger = Flow.GetProperty("triggers")[0];

        trigger.GetProperty("kind").GetString().ShouldBe("Http");
        trigger.GetProperty("method").GetString().ShouldBe("POST");
        trigger.GetProperty("route").GetString().ShouldBe("/api/v1/transfers");
        trigger.GetProperty("idempotent").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// The graph is published with its shape, not flattened into a list of steps.
    /// </summary>
    /// <remarks>
    /// The condition, its <c>Fail</c> arm, and the switch with its three branches — two arms
    /// and the empty one a <c>Book</c> transfer takes — are all in the document. A reviewer
    /// asking "what does this application do to my money" reads this rather than the code.
    /// </remarks>
    [Fact]
    public void PublishesTheBranchesAndTheRejection()
    {
        var steps = Flow.GetProperty("steps").EnumerateArray().ToList();

        steps.Select(s => s.GetProperty("kind").GetString()).ShouldBe(
            ["Capability", "Condition", "Switch", "Capability", "Capability", "Capability", "Emit"]);

        // The insufficient-funds arm.
        var condition = steps[1];
        condition.GetProperty("branches")[0][0].GetProperty("kind").GetString().ShouldBe("Fail");

        // Three branches: Sepa screens, Swift screens and routes, and the third is the arm
        // a value matching no case takes — empty, because there is no Default.
        var branches = steps[2].GetProperty("branches").EnumerateArray().ToList();

        branches.Count.ShouldBe(3);
        branches[0].GetArrayLength().ShouldBe(1);
        branches[1].GetArrayLength().ShouldBe(2);
        branches[2].GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void RecordsEachLegAndItsInverse()
    {
        var steps = Flow.GetProperty("steps").EnumerateArray().ToList();

        steps[3].GetProperty("capability").GetString().ShouldBe("ledger.post_debit@1.0.0");
        steps[3].GetProperty("compensation").GetString().ShouldBe("ledger.reverse_debit@1.0.0");

        steps[4].GetProperty("capability").GetString().ShouldBe("ledger.post_credit@1.0.0");
        steps[4].GetProperty("compensation").GetString().ShouldBe("ledger.reverse_credit@1.0.0");

        // Not compensable, and the absence is what says so.
        steps[5].GetProperty("capability").GetString().ShouldBe("settlement.record@1.0.0");
        steps[5].TryGetProperty("compensation", out _).ShouldBeFalse();
    }

    /// <summary>
    /// Every capability's authorisation stance, which is what an access review reads.
    /// </summary>
    /// <remarks>
    /// The stance travels with the operation rather than with a URL, so it applies
    /// identically over HTTP, over a bus and to an agent — as a <em>declaration</em>. Nothing
    /// in this release checks it: see <c>NothingEnforcesTheseDeclarations</c>.
    /// </remarks>
    [Fact]
    public void RecordsEveryCapabilitysAuthorisationStance()
    {
        var capabilities = Capabilities;

        capabilities["ledger.post_debit"].GetProperty("authorization").GetProperty("mode")
            .GetString().ShouldBe("Permission");

        capabilities["ledger.post_debit"].GetProperty("authorization").GetProperty("value")
            .GetString().ShouldBe("ledger:post");

        capabilities["ledger.post_credit"].GetProperty("authorization").GetProperty("value")
            .GetString().ShouldBe("ledger:post");

        capabilities["compliance.screen_sanctions"].GetProperty("authorization")
            .GetProperty("value").GetString().ShouldBe("compliance:screen");

        // The reversals are reachable only from the engine. That is the answer to "could
        // somebody move money by asking for an undo", and it is in the published document.
        capabilities["ledger.reverse_debit"].GetProperty("authorization").GetProperty("mode")
            .GetString().ShouldBe("Internal");

        capabilities["ledger.reverse_credit"].GetProperty("authorization").GetProperty("mode")
            .GetString().ShouldBe("Internal");

        // A stance that names nothing publishes no value, rather than an empty one.
        capabilities["transfer.validate"].GetProperty("authorization")
            .TryGetProperty("value", out _).ShouldBeFalse();
    }

    [Fact]
    public void EveryLedgerCapabilityDeclaresItsSideEffectAndItsIdempotency()
    {
        var capabilities = Capabilities;

        foreach (var id in new[]
        {
            "ledger.post_debit", "ledger.post_credit", "ledger.reverse_debit", "ledger.reverse_credit",
        })
        {
            capabilities[id].GetProperty("sideEffects").EnumerateArray()
                .Select(e => e.GetString()).ShouldBe(["core-ledger"], id);

            // Idempotent = true is what permits a Retry (FLOWX1014) and what
            // PolicyChain requires of a capability carrying a CompensationRetry. It is a
            // promise the capability keeps by presenting the ledger a derived key.
            capabilities[id].GetProperty("idempotent").GetBoolean().ShouldBeTrue(id);
        }
    }

    [Fact]
    public void PublishesEachCapabilitysErrorCatalogue()
    {
        var capabilities = Capabilities;

        Codes(capabilities["transfer.validate"])
            .ShouldBe(["ledger.account_unknown", "transfer.invalid_amount"]);

        Codes(capabilities["compliance.screen_sanctions"]).ShouldBe(["compliance.sanctions_hit"]);
        Codes(capabilities["correspondent.resolve"]).ShouldBe(["correspondent.not_found"]);
        Codes(capabilities["ledger.post_credit"]).ShouldBe(["ledger.account_closed"]);

        // Present and empty, which is a statement: the debit has no failure path of its own,
        // and that is different from "nothing could be read about it".
        Codes(capabilities["ledger.post_debit"]).ShouldBeEmpty();
    }

    /// <summary>
    /// The rejection the flow can return is <em>not</em> in the manifest's error catalogue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>.Fail(TransferErrors.InsufficientFunds)</c> is compiled into a <c>"kind": "Fail"</c>
    /// node and the error itself reaches only a field of the generated dispatcher: the
    /// manifest publishes structure, and an <c>Error</c>'s message interpolates business
    /// values. The consequence is that <c>transfer.insufficient_funds</c> — a
    /// <c>409</c> a caller will certainly meet — is absent from the flow's <c>errors</c>
    /// array, while every code a capability returns is present.
    /// </para>
    /// <para>
    /// So a client SDK or an error catalogue generated from this document is incomplete for
    /// exactly the outcomes a flow author chose to state in the graph. That is asserted here
    /// rather than described in a README, because a README does not go red.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFailArmsErrorCodeIsNotPublished()
    {
        Flow.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ShouldBe(
            [
                "compliance.sanctions_hit",
                "correspondent.not_found",
                "ledger.account_closed",
                "ledger.account_unknown",
                "transfer.invalid_amount",
            ]);

        FlowXManifest.Json.ShouldNotContain("transfer.insufficient_funds", Case.Sensitive);
    }

    [Fact]
    public void RecordsTheEventTheFlowStages()
    {
        Flow.GetProperty("emits").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["transfer.completed"]);

        var declared = Manifest.RootElement.GetProperty("events")[0];

        declared.GetProperty("type").GetString().ShouldBe("transfer.completed");
        declared.GetProperty("schemaVersion").GetString().ShouldBe("1.0.0");
    }

    [Fact]
    public void NoAccountNumberOrErrorMessageReachesTheManifest()
    {
        foreach (var fragment in new[]
        {
            "GB33BUKB", "DE89370400", "must be positive", "not held at this bank", "sanctions list",
        })
        {
            FlowXManifest.Json.ShouldNotContain(fragment, Case.Insensitive);
        }
    }

    [Fact]
    public void TheSourcePointerIsRelativeToTheProject()
    {
        var source = Flow.GetProperty("source").GetString().ShouldNotBeNull();

        // Moves whenever the file's header does — most recently when the flow's remarks
        // gained the paragraph explaining its FLOWX1032 suppression. The number is the
        // assertion, not an incidental: a source pointer that drifts from the declaration
        // it names is a pointer a reader follows to the wrong line.
        source.ShouldBe("ExecuteTransferFlow.cs:52");
        source.ShouldNotStartWith("/");
        source.ShouldNotContain(":\\");
    }

    // ------------------------------------------------------------------ published vs. real

    /// <summary>
    /// The manifest publishes the policies, and the compiled plan now carries them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the sample's central honesty assertion, and it is deliberately a
    /// test rather than a paragraph. It used to assert the opposite.</strong> The manifest
    /// says the ledger steps carry <c>Timeout</c>, <c>Audit</c> and <c>CompensationRetry</c>;
    /// the generated plan carried no <c>PolicyChain</c> at all, because <c>FlowEmitter</c>
    /// emitted every node as <c>StepNode.ForCapability(index, capability, compensation)</c>
    /// and passed no policies. So <c>ExecutionPlan.HasCompensationPolicies</c> was false for
    /// every compiled flow and <c>FlowEngine</c> dispatched each undo exactly once, whatever
    /// the author had written.
    /// </para>
    /// <para>
    /// <c>CompensationRetry</c> is the one policy the runtime executes (WP-57), which is what
    /// made the gap worth pinning: it was not "the policy engine is P4", it was that the
    /// single implemented policy was unreachable from the DSL. The emitter now splits the
    /// declared set — the compensation retry onto <c>CompensationPolicies</c>, everything else
    /// onto <c>Policies</c> — so the two artifacts agree about the same source line.
    /// </para>
    /// <para>
    /// <strong>The half being carried is not the half being run.</strong> Nothing in
    /// <c>FlowEngine</c> reads <c>StepNode.Policies</c>: no timeout is armed and no rate limit
    /// is counted, exactly as before. What the plan now states is what was declared, which is
    /// the precondition for P4 executing it and, until then, for a reader of the plan seeing
    /// what a reader of the manifest sees.
    /// </para>
    /// <para>
    /// <strong>"The forward half" is one stage too narrow, and the assertion below shows
    /// it.</strong> <c>Audit</c> is a <c>PolicyStage.Consistency</c> policy — stage 7, the
    /// same stage as the <c>CompensationRetry</c> that runs — and it sits on
    /// <c>Policies</c> rather than <c>CompensationPolicies</c>, because
    /// <c>PolicyChain.ForStep</c> splits by what a policy <em>wraps</em>. So it is carried and
    /// inert alongside the <c>Timeout</c>. <c>FLOWX1032</c> reports both, and
    /// <c>ReferenceSamplePolicyTests</c> asserts it does so against this file's real source.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePlanCarriesTheDeclaredPolicyChain()
    {
        var plan = ExecuteTransferFlow.Plan;

        // Flipped from ShouldBeFalse. This is the gate FlowEngine reads before it does any
        // retry bookkeeping at all, and it was false for every flow the compiler produced.
        plan.HasCompensationPolicies.ShouldBeTrue(
            "Policies.LedgerPost declares CompensationRetry on both ledger legs, so a failing " +
            "reversal is retried rather than dispatched once and abandoned.");

        var debitStep = plan.Graph.Steps.Single(s => s.Capability?.Id == "ledger.post_debit");

        // Flipped from `CompensationPolicies.IsEmpty.ShouldBeTrue()` on every step.
        debitStep.CompensationPolicies.Ordered
            .Select(p => p.Kind)
            .ShouldBe(["CompensationRetry"],
                "The undo's chain wraps ledger.reverse_debit and carries only what applies " +
                "to it. Timeout and Audit wrap the forward post.");

        debitStep.CompensationRetry.Attempts.ShouldBe(
            5,
            "The number Policies.LedgerPost declares, resolved into the node when the plan " +
            "was built rather than walked while an incident is in progress.");

        debitStep.CompensationRetry.IsRetrying.ShouldBeTrue();

        // Flipped from `Policies.IsEmpty.ShouldBeTrue()`. Carried, and still executed by
        // nothing — the Policy Engine is P4 and the forward path runs zero policies.
        debitStep.Policies.Ordered
            .Select(p => p.Kind)
            .ShouldBe(["Timeout", "Audit"],
                "Ordered by stage: Resilience before Consistency, ADR-0011's fixed order.");

        // A step that declared no set still carries nothing, which is what keeps the flags
        // that gate the engine's fast paths meaningful.
        plan.Graph.Steps
            .Single(s => s.Capability?.Id == "ledger.post_credit")
            .CompensationRetry.Attempts.ShouldBe(5);

        // And the document agrees, which is the point: a reader of the manifest and a reader
        // of the plan now find the same retry.
        var debit = Flow.GetProperty("steps")[3];

        debit.GetProperty("policies").EnumerateArray()
            .Select(p => p.GetProperty("kind").GetString())
            .ShouldContain("CompensationRetry");
    }

    /// <summary>
    /// The plan the endpoint runs is the one the manifest describes.
    /// </summary>
    /// <remarks>
    /// The two are produced by one reading of the source, so a disagreement would mean the
    /// generator emitted a document about a different program. Cheap to assert and the whole
    /// premise of ADR-0005.
    /// </remarks>
    [Fact]
    public void ThePlanAndTheDocumentDescribeTheSameFlow()
    {
        var plan = ExecuteTransferFlow.Plan;

        plan.Flow.Id.ShouldBe(Flow.GetProperty("id").GetString());
        plan.Flow.Version.ShouldBe(Flow.GetProperty("version").GetString());
        plan.Flow.Profile.ShouldBe(ExecutionProfile.Durable);
        plan.Flow.Deadline.ShouldBe(TimeSpan.FromSeconds(60));

        plan.HasEmit.ShouldBeTrue("the outbox is staged only for a plan that declares one.");
    }

    private static string[] Codes(JsonElement capability) => [.. capability.GetProperty("errors")
        .EnumerateArray()
        .Select(e => e.GetProperty("code").GetString()!)];
}
