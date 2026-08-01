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

        source.ShouldBe("ExecuteTransferFlow.cs:42");
        source.ShouldNotStartWith("/");
        source.ShouldNotContain(":\\");
    }

    // ------------------------------------------------------------------ published vs. real

    /// <summary>
    /// The manifest publishes the policies; the compiled plan carries none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the sample's central honesty assertion, and it is deliberately a
    /// test rather than a paragraph.</strong> The manifest above says the ledger steps carry
    /// <c>Timeout</c>, <c>Audit</c> and <c>CompensationRetry</c>. The generated plan carries
    /// no <c>PolicyChain</c> at all: <c>FlowEmitter</c> emits every node as
    /// <c>StepNode.ForCapability(index, capability, compensation)</c> and passes no policies,
    /// so <c>ExecutionPlan.HasCompensationPolicies</c> is false and
    /// <c>FlowEngine</c> dispatches each undo exactly once.
    /// </para>
    /// <para>
    /// <c>CompensationRetry</c> is the one policy the runtime does execute (WP-57), which is
    /// what makes this worth pinning: the gap is not "the policy engine is P4", it is that
    /// the single implemented policy is unreachable from the DSL. If the generator ever
    /// starts emitting chains, this test fails and the README stops being wrong on the same
    /// commit.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePlanCarriesNoPolicyChain()
    {
        var plan = ExecuteTransferFlow.Plan;

        plan.HasCompensationPolicies.ShouldBeFalse(
            "the generated plan carries no policy chain, so the CompensationRetry declared " +
            "on both ledger steps never runs. A failing undo is dispatched once.");

        foreach (var step in plan.Graph.Steps)
        {
            step.Policies.IsEmpty.ShouldBeTrue(step.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            step.CompensationPolicies.IsEmpty.ShouldBeTrue(step.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // And the document says otherwise, which is the point: a reader of the manifest sees
        // a retry that a reader of the plan cannot find.
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
