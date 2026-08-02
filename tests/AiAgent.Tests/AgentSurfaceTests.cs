using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FlowX.Mcp;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiAgent.Tests;

/// <summary>
/// The sample's claims, checked on the wire against the sample's own composition root.
/// </summary>
/// <remarks>
/// <para>
/// Three of them, and they are separable. <strong>Authorisation</strong> is the same decision for
/// an agent and for an HTTP caller, because there is one reader and one step loop.
/// <strong>Confirmation</strong> is enforced by this process rather than by the model's
/// cooperation, so a declined prompt means the flow was not entered. And an agent can
/// <strong>read the graph</strong> — the manifest, verbatim — rather than only be told about the
/// slice a tool list projects.
/// </para>
/// <para>
/// Every refusal assertion checks the payment provider's counter as well as the message. A server
/// that answered "refused" and refunded anyway would satisfy every assertion about JSON.
/// </para>
/// </remarks>
public sealed class AgentSurfaceTests
{
    private const string RefundTool = "ticket_refund";
    private const string SearchTool = "ticket_search";
    private const string ReviewTool = "ops_review";
    private const string RefundRoute = "/api/v1/refunds";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The sample starts and answers on both of the routes it maps.</summary>
    [Fact]
    public async Task TheApplicationStartsAndServesBothTransports()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative), Cancellation);

        health.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var initialize = await new AgentSession(client)
            .SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");

        var result = initialize.RootElement.GetProperty("result");

        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("AiAgent");

        // The resource capability is declared, so a client knows to ask before it does.
        result.GetProperty("capabilities").TryGetProperty("resources", out _).ShouldBeTrue();
    }

    /// <summary>Every flow this assembly declares has its dispatcher registered.</summary>
    /// <remarks>
    /// The generic form of a defect that has shipped here before: a flow whose dispatcher nobody
    /// registers fails differently depending on its triggers, and a flow reachable only by an
    /// agent fails on the first <c>tools/call</c> rather than at start-up. Enumerating the types
    /// is what makes this hold for the flow that does not exist yet.
    /// </remarks>
    [Fact]
    public async Task EveryDeclaredFlowsDispatcherIsRegistered()
    {
        using var host = await AiAgentHost.StartAsync();

        var dispatchers = typeof(IssueRefundFlow).Assembly.GetTypes()
            .Where(static type => type.IsNested && type.Name == "Dispatcher")
            .ToList();

        dispatchers.Count.ShouldBe(3);

        using var scope = host.Services.CreateScope();

        foreach (var dispatcher in dispatchers)
        {
            scope.ServiceProvider.GetService(dispatcher)
                .ShouldNotBeNull(
                    $"{dispatcher.DeclaringType!.Name}'s dispatcher is not registered, so the " +
                    "flow it belongs to cannot run. Add it to Program.cs.");
        }
    }

    /// <summary>
    /// <c>tools/list</c> publishes what the flows declared, and nothing a tool definition invented.
    /// </summary>
    [Fact]
    public async Task TheToolListIsAProjectionOfTheManifest()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        using var listed = await new AgentSession(client)
            .SendAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var tools = listed.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .ToDictionary(static tool => tool.GetProperty("name").GetString()!);

        tools.Keys.Order(StringComparer.Ordinal)
            .ShouldBe([ReviewTool, RefundTool, SearchTool]);

        var refund = tools[RefundTool];
        var annotations = refund.GetProperty("annotations");

        annotations.GetProperty("flowId").GetString().ShouldBe("ticket.refund");

        // Declared on payment.refund, not on the trigger, and reaching the descriptor by way of
        // the manifest. Nothing in IssueRefundFlow mentions any of these three.
        annotations.GetProperty("requiredPermissions").EnumerateArray()
            .Select(static permission => permission.GetString())
            .ShouldBe(["payment.refund"]);

        annotations.GetProperty("sideEffects").EnumerateArray()
            .Select(static effect => effect.GetString())
            .ShouldBe(["ledger", "payment-gateway"]);

        annotations.GetProperty("idempotent").GetBoolean().ShouldBeFalse();
        annotations.GetProperty("confirmationRequired").GetBoolean().ShouldBeTrue();

        refund.GetProperty("inputSchema").GetProperty("x-flowx-sensitive").EnumerateArray()
            .Select(static member => member.GetString())
            .ShouldBe(["CardholderReference"]);

        // The read tool declares ConfirmationMode.Never and reaches no side effect, so it asks
        // nobody — which is what keeps an enforcing deployment usable.
        tools[SearchTool].GetProperty("annotations")
            .GetProperty("confirmationRequired").GetBoolean().ShouldBeFalse();
    }

    /// <summary>
    /// A declined confirmation means the flow was never entered — not that the agent was told no.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The load-bearing test of this work package.</strong> docs/13 §6 states that the
    /// server does not prompt and that <c>confirmationRequired</c> is an annotation a client may
    /// act on; under <c>ConfirmationPolicy.Elicit</c> it is a gate this process holds. The two
    /// assertions that matter are that the question was asked at all, and that
    /// <see cref="CountingGateway.Refunds"/> is zero after the answer — a server that refused
    /// without asking, or asked and refunded anyway, fails one of them.
    /// </para>
    /// <para>
    /// The token is the <em>operator's</em>, which holds <c>payment.refund</c>. So nothing about
    /// this refusal is authorisation: the caller was entitled and a human said no.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADeclinedConfirmationMeansTheFlowIsNeverEntered()
    {
        var gateway = new CountingGateway();

        using var host = await AiAgentHost.StartAsync(gateway);
        using var client = host.GetTestClient();

        var session = new AgentSession(
            client, Tokens.Operator, static (_, _) => """{"action":"decline"}""");

        using var called = await session.StreamAsync(Refund("T-1001"));

        session.Asked.Select(static asked => asked.Method).ShouldBe([McpElicitation.Method]);

        var result = called.RootElement.GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();

        var error = result.GetProperty("structuredContent").GetProperty("error");

        error.GetProperty("code").GetString().ShouldBe(McpErrors.ConfirmationRefusedCode);
        error.GetProperty("detail").GetProperty("outcome").GetString().ShouldBe("declined");

        gateway.Refunds.ShouldBe(
            0,
            "A human declined the confirmation and the payment provider was called anyway. " +
            "The confirmation gate is decoration.");
    }

    /// <summary>An approved confirmation runs the flow, and the money moves once.</summary>
    [Fact]
    public async Task AnApprovedConfirmationRunsTheFlow()
    {
        var gateway = new CountingGateway();

        using var host = await AiAgentHost.StartAsync(gateway);
        using var client = host.GetTestClient();

        var session = new AgentSession(
            client, Tokens.Operator, static (_, _) => """{"action":"accept","content":{"approve":true}}""");

        using var called = await session.StreamAsync(Refund("T-1001"));

        var result = called.RootElement.GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();

        var output = result.GetProperty("structuredContent").GetProperty("output");

        output.GetProperty("ticketId").GetString().ShouldBe("T-1001");
        output.GetProperty("refundReference").GetString().ShouldNotBeNullOrEmpty();

        gateway.Refunds.ShouldBe(1);
    }

    /// <summary>
    /// The prompt names the declared consequences and withholds the argument marked
    /// <c>[Sensitive]</c>.
    /// </summary>
    /// <remarks>
    /// docs/13 §6's second safety property is that a confirmation prompt is accurate "because
    /// side effects are declared in the capability contract rather than guessed from a function
    /// name". This is that claim as an assertion on the text a human would read. It also checks
    /// the two things the prompt must <em>not</em> say: the cardholder reference, which the
    /// contract marks sensitive, and a currency amount, which the manifest does not carry — see
    /// <c>McpElicitation</c>.
    /// </remarks>
    [Fact]
    public async Task TheConfirmationPromptNamesTheDeclaredConsequencesAndWithholdsTheSecret()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        var session = new AgentSession(
            client, Tokens.Operator, static (_, _) => """{"action":"decline"}""");

        using var called = await session.StreamAsync(Refund("T-1001", secret: "cus_4f21b"));

        using var asked = JsonDocument.Parse(session.Asked.Single().Parameters);

        var message = asked.RootElement.GetProperty("message").GetString().ShouldNotBeNull();

        message.ShouldContain("payment-gateway");
        message.ShouldContain("ledger");
        message.ShouldContain("payment.refund");
        message.ShouldContain("T-1001");
        message.ShouldContain("CardholderReference");

        message.Contains("cus_4f21b", StringComparison.Ordinal).ShouldBeFalse(
            "The contract marks CardholderReference [Sensitive] and its value reached the " +
            "prompt, which a client transcribes and keeps.");

        // The manifest carries the side effect `payment-gateway` and carries no price, so the
        // prompt names the effect and not an amount. See McpElicitation.
        message.Contains("249", StringComparison.Ordinal).ShouldBeFalse(
            "The prompt states a currency amount, which the manifest does not carry — so it was " +
            "computed from somewhere else and can disagree with what the flow charges.");
    }

    /// <summary>
    /// Approving grants nothing: an agent without the permission is still refused at the step.
    /// </summary>
    /// <remarks>
    /// The distinction ADR-0060 turns on. <c>Tokens.Agent</c> holds <c>support.read</c> and
    /// <c>ops.read</c> and not <c>payment.refund</c>, and it answers its own prompt with an
    /// approval — which is exactly what a prompt-injected agent driving a permissive client
    /// would do. The refusal it gets is the step loop's, not the confirmation gate's, and the
    /// codes are different so the two cannot be confused for one another.
    /// </remarks>
    [Fact]
    public async Task ApprovingAConfirmationGrantsNoPermission()
    {
        var gateway = new CountingGateway();

        using var host = await AiAgentHost.StartAsync(gateway);
        using var client = host.GetTestClient();

        var session = new AgentSession(
            client, Tokens.Agent, static (_, _) => """{"action":"accept","content":{"approve":true}}""");

        using var called = await session.StreamAsync(Refund("T-1001"));

        var result = called.RootElement.GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("structuredContent").GetProperty("error").GetProperty("code")
            .GetString().ShouldBe("authorization.permission_denied");

        gateway.Refunds.ShouldBe(0);
    }

    /// <summary>A client that cannot be asked does not get to skip the question.</summary>
    /// <remarks>
    /// The "do it without asking the user" row of the sample's own attack table. Sending no
    /// <c>Accept: text/event-stream</c> is how a client says it cannot read a server request; a
    /// deployment that elicits answers that with a refusal rather than with a call, because the
    /// alternative is that declining to listen is how you avoid being asked.
    /// </remarks>
    [Fact]
    public async Task AClientThatCannotBeAskedIsRefusedRatherThanObeyed()
    {
        var gateway = new CountingGateway();

        using var host = await AiAgentHost.StartAsync(gateway);
        using var client = host.GetTestClient();

        using var called = await new AgentSession(client, Tokens.Operator).SendAsync(Refund("T-1001"));

        var result = called.RootElement.GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();

        var error = result.GetProperty("structuredContent").GetProperty("error");

        error.GetProperty("code").GetString().ShouldBe(McpErrors.ConfirmationRefusedCode);
        error.GetProperty("detail").GetProperty("outcome").GetString().ShouldBe("unelicitable");

        gateway.Refunds.ShouldBe(0);
    }

    /// <summary>A client that opens a stream and never answers times out, and nothing runs.</summary>
    /// <remarks>
    /// The arm a deadline exists for. Without it a client that reads the question and stops
    /// holds a request, a scope and a connection for the length of the flow's deadline, on a
    /// flow that has not started.
    /// </remarks>
    [Fact]
    public async Task AnUnansweredConfirmationTimesOutAndNothingRuns()
    {
        var gateway = new CountingGateway();

        using var host = await AiAgentHost.StartAsync(gateway);
        using var client = host.GetTestClient();

        // Reads the question and never answers it.
        var session = new AgentSession(client, Tokens.Operator, respond: null, answer: false);

        using var called = await session.StreamAsync(Refund("T-1001"));

        var error = called.RootElement.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("error");

        error.GetProperty("code").GetString().ShouldBe(McpErrors.ConfirmationRefusedCode);
        gateway.Refunds.ShouldBe(0);
    }

    /// <summary>A tool with no declared consequence runs without asking anybody.</summary>
    /// <remarks>
    /// The other half of the gate, and the half that decides whether an enforcing deployment is
    /// usable. <c>ticket.search</c> reaches one capability with no <c>SideEffects</c>, so its
    /// descriptor publishes <c>confirmationRequired: false</c> and the server asks nothing —
    /// under exactly the same options that refuse an unconfirmed refund.
    /// </remarks>
    [Fact]
    public async Task AToolWithNoDeclaredConsequenceIsNotElicited()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        var session = new AgentSession(client, Tokens.Agent);

        using var called = await session.StreamAsync(
            Call(7, SearchTool, """{"query":"refund"}"""));

        session.Asked.ShouldBeEmpty("A read-only tool was elicited, so every call now needs a human.");

        called.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
    }

    /// <summary>One stance refuses the same caller over both transports.</summary>
    /// <remarks>
    /// docs/13 §6's first safety property, asserted rather than restated. The shapes differ and
    /// are meant to — an agent gets a <c>result</c> carrying <c>isError</c> because the call
    /// happened, an HTTP caller gets <c>403</c> and RFC 7807 — and the <em>code</em> is one
    /// string, which is what makes it one decision rather than two that agree today.
    /// </remarks>
    [Fact]
    public async Task OneStanceRefusesTheSameCallerOnBothTransports()
    {
        var overHttp = new CountingGateway();
        var overAgent = new CountingGateway();

        using var httpHost = await AiAgentHost.StartAsync(overHttp);
        using var agentHost = await AiAgentHost.StartAsync(overAgent);
        using var httpClient = httpHost.GetTestClient();
        using var agentClient = agentHost.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RefundRoute)
        {
            Content = new StringContent(
                """{"ticketId":"T-1001","reason":"duplicate","cardholderReference":"cus_4f21b"}""",
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("Idempotency-Key", "both-1");
        request.Headers.Add("Authorization", "Bearer " + Tokens.Agent);

        using var refusedOverHttp = await httpClient.SendAsync(request, Cancellation);

        refusedOverHttp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var problem = JsonDocument.Parse(
            await refusedOverHttp.Content.ReadAsStringAsync(Cancellation));

        var httpCode = problem.RootElement.GetProperty("code").GetString();

        var session = new AgentSession(
            agentClient, Tokens.Agent, static (_, _) => """{"action":"accept","content":{"approve":true}}""");

        using var refusedOverAgent = await session.StreamAsync(Refund("T-1001"));

        var agentCode = refusedOverAgent.RootElement.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString();

        agentCode.ShouldBe("authorization.permission_denied");
        agentCode.ShouldBe(httpCode);

        overHttp.Refunds.ShouldBe(0);
        overAgent.Refunds.ShouldBe(0);
    }

    /// <summary>An anonymous agent is refused at the first step.</summary>
    [Fact]
    public async Task AnAnonymousAgentIsRefusedBeforeAnyCapabilityRuns()
    {
        var gateway = new CountingGateway();

        using var host = await AiAgentHost.StartAsync(gateway);
        using var client = host.GetTestClient();

        var session = new AgentSession(
            client, Tokens.Anonymous, static (_, _) => """{"action":"accept","content":{"approve":true}}""");

        using var called = await session.StreamAsync(Refund("T-1001"));

        called.RootElement.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("error").GetProperty("code")
            .GetString().ShouldBe("authorization.not_authenticated");

        gateway.Refunds.ShouldBe(0);
    }

    /// <summary>The manifest is readable, verbatim, as a resource.</summary>
    /// <remarks>
    /// docs/13 §1 says "AI does not read your code. It reads your graph" and names the artifact.
    /// This is the graph made addressable: the bytes an agent reads at <c>flowx://manifest</c> are
    /// the bytes the build compiled in, compared here rather than described.
    /// </remarks>
    [Fact]
    public async Task TheManifestIsReadableAsAResourceAndIsTheBuildsOwnBytes()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();
        var session = new AgentSession(client, Tokens.Agent);

        using var listed = await session.SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""");

        var uris = listed.RootElement.GetProperty("result").GetProperty("resources")
            .EnumerateArray()
            .Select(static resource => resource.GetProperty("uri").GetString())
            .ToList();

        uris.ShouldContain(McpResourceCatalog.ManifestUri);
        uris.ShouldContain(McpResourceCatalog.FlowUriPrefix + "ticket.refund");
        uris.ShouldContain(McpResourceCatalog.FlowUriPrefix + "ops.review");

        using var read = await session.SendAsync(
            Read(2, McpResourceCatalog.ManifestUri));

        read.RootElement.GetProperty("result").GetProperty("contents")[0]
            .GetProperty("text").GetString()
            .ShouldBe(FlowX.Generated.FlowXManifest.Json);
    }

    /// <summary>A flow resource is that flow's own entry, cut out of the document.</summary>
    [Fact]
    public async Task AFlowResourceIsTheManifestEntryAndNotASummaryOfIt()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        using var read = await new AgentSession(client, Tokens.Agent).SendAsync(
            Read(3, McpResourceCatalog.FlowUriPrefix + "ticket.refund"));

        var text = read.RootElement.GetProperty("result").GetProperty("contents")[0]
            .GetProperty("text").GetString().ShouldNotBeNull();

        using var slice = JsonDocument.Parse(text);

        slice.RootElement.GetProperty("id").GetString().ShouldBe("ticket.refund");

        // The step list, which no tool descriptor carries and which is the reason a resource is
        // worth having: a model deciding whether to call this can see what it does.
        slice.RootElement.GetProperty("steps").GetArrayLength().ShouldBe(2);
        slice.RootElement.GetProperty("deadline").GetString().ShouldBe("PT30S");
    }

    /// <summary>A resource this application does not publish is refused, not invented.</summary>
    [Fact]
    public async Task AResourceTheManifestDoesNotPublishIsRefused()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        using var read = await new AgentSession(client, Tokens.Agent).SendAsync(
            Read(4, McpResourceCatalog.FlowUriPrefix + "ticket.delete"));

        read.RootElement.GetProperty("error").GetProperty("data").GetProperty("code")
            .GetString().ShouldBe(McpErrors.UnknownResourceCode);
    }

    /// <summary>The review borrows the calling agent's model rather than one of its own.</summary>
    /// <remarks>
    /// The sampling claim end to end: the flow asks, the client's model answers, and the answer
    /// is what the tool returns. Nothing in this application holds an API key or reaches a model
    /// vendor — the completion happened on the caller's side of the connection.
    /// </remarks>
    [Fact]
    public async Task TheReviewBorrowsTheCallersModel()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        var session = new AgentSession(
            client,
            Tokens.Agent,
            static (method, _) => method == McpCallScope.Method
                ? """{"role":"assistant","content":{"type":"text","text":"Two notes and no defects."}}"""
                : null);

        using var called = await session.StreamAsync(Review(narrate: true));

        session.Asked.Select(static asked => asked.Method).ShouldBe([McpCallScope.Method]);

        var output = called.RootElement.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("output");

        output.GetProperty("narrated").GetBoolean().ShouldBeTrue();
        output.GetProperty("report").GetString().ShouldBe("Two notes and no defects.");
    }

    /// <summary>
    /// What the model is handed is the review, and there is no way to make it anything else.
    /// </summary>
    /// <remarks>
    /// docs/13 §5's last boundary — "AI runs on the manifest, not on data" — checked at the one
    /// place a request leaves the process. The user message is <c>ManifestReview.ToPrompt()</c>
    /// verbatim, which is a pure function of the build's manifest, so there is nothing a caller
    /// could put into a completion request short of changing that method's signature.
    /// </remarks>
    [Fact]
    public async Task WhatTheModelIsAskedIsAPureFunctionOfTheManifest()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        var session = new AgentSession(
            client,
            Tokens.Agent,
            static (_, _) => """{"role":"assistant","content":{"type":"text","text":"ok"}}""");

        using var called = await session.StreamAsync(Review(narrate: true));

        using var asked = JsonDocument.Parse(session.Asked.Single().Parameters);

        var sent = asked.RootElement.GetProperty("messages")[0]
            .GetProperty("content").GetProperty("text").GetString();

        sent.ShouldBe(FlowX.Ai.ManifestReview.Of(FlowX.Generated.FlowXManifest.Json).ToPrompt());
    }

    /// <summary>A caller with no model still gets the review.</summary>
    /// <remarks>
    /// The session below answers every server request with <c>-32601 Method not found</c>, which
    /// is what an MCP client that serves no sampling does. The flow reports
    /// <c>narrated: false</c> and returns the deterministic rendering rather than failing —
    /// because a flow that only works when an agent called it has two behaviours, and the honest
    /// one is the one that still answers.
    /// </remarks>
    [Fact]
    public async Task AClientWithNoModelStillGetsTheReview()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        var session = new AgentSession(client, Tokens.Agent);

        using var called = await session.StreamAsync(Review(narrate: true));

        var output = called.RootElement.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("output");

        output.GetProperty("narrated").GetBoolean().ShouldBeFalse();
        output.GetProperty("findings").GetInt32().ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// An HTTP caller reaching the same flow gets the same review, with no model in sight.
    /// </summary>
    /// <remarks>
    /// The reviewer is a capability, not an agent feature. It has no <c>[HttpTrigger]</c>, so this
    /// goes through the agent surface with the plain-JSON shape a non-streaming client uses — and
    /// arrives at a request scope whose <c>IAgentSampler</c> has no channel at all, which is the
    /// same state an HTTP or broker caller would produce.
    /// </remarks>
    [Fact]
    public async Task ACallerWithNoChannelAtAllStillGetsTheReview()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        using var called = await new AgentSession(client, Tokens.Agent).SendAsync(Review(narrate: true));

        var output = called.RootElement.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("output");

        output.GetProperty("narrated").GetBoolean().ShouldBeFalse();
    }

    /// <summary>The review is authorised like anything else.</summary>
    /// <remarks>
    /// It is a flow, so <c>ops.read</c> is decided in the step loop against the caller's claims.
    /// A review tool bolted onto the transport could not have been refused at all, which is one
    /// reason <c>McpServer</c> refuses at start-up any binding the manifest does not publish.
    /// </remarks>
    [Fact]
    public async Task TheReviewIsRefusedToACallerWithoutOpsRead()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        using var called = await new AgentSession(client, Tokens.Anonymous).SendAsync(Review(narrate: false));

        called.RootElement.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("error").GetProperty("code")
            .GetString().ShouldBe("authorization.not_authenticated");
    }

    /// <summary>A tool the manifest does not publish is a JSON-RPC error, not a flow failure.</summary>
    [Fact]
    public async Task ATooltheManifestDoesNotPublishIsRefusedBySurface()
    {
        using var host = await AiAgentHost.StartAsync();
        using var client = host.GetTestClient();

        using var called = await new AgentSession(client, Tokens.Operator).SendAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"ticket_delete","arguments":{}}}""");

        called.RootElement.TryGetProperty("result", out _).ShouldBeFalse();
        called.RootElement.GetProperty("error").GetProperty("data").GetProperty("code")
            .GetString().ShouldBe(McpErrors.UnknownToolCode);
    }

    /// <summary>
    /// Under the platform default the annotation is published and the server asks nothing.
    /// </summary>
    /// <remarks>
    /// The behaviour every host that does not opt in keeps, asserted so that
    /// <c>ConfirmationPolicy.Elicit</c> stays an opt-in rather than becoming the default by
    /// accident — which would break every MCP client that cannot read an event stream.
    /// </remarks>
    [Fact]
    public async Task TheDefaultPolicyAnnotatesAndDoesNotAsk()
    {
        var gateway = new CountingGateway();

        using var host = await AiAgentHost.StartAsync(gateway, ConfirmationPolicy.Annotate);
        using var client = host.GetTestClient();

        using var called = await new AgentSession(client, Tokens.Operator).SendAsync(Refund("T-1001"));

        called.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
        gateway.Refunds.ShouldBe(1);
    }

    private static string Refund(string ticketId, string secret = "cus_0001") =>
        Call(
            2,
            RefundTool,
            "{\"ticketId\":\"" + ticketId + "\",\"reason\":\"duplicate charge\"," +
            "\"cardholderReference\":\"" + secret + "\"}");

    private static string Review(bool narrate) =>
        Call(5, ReviewTool, "{\"narrate\":" + (narrate ? "true" : "false") + "}");

    /// <summary>One <c>tools/call</c> message.</summary>
    /// <remarks>
    /// Assembled by concatenation rather than written in a raw string literal, because a JSON
    /// document that ends in three closing braces cannot be written in one: <c>}}}</c> is
    /// ambiguous against the interpolation delimiter at every brace count.
    /// </remarks>
    private static string Call(int id, string tool, string arguments) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(CultureInfo.InvariantCulture) +
        ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool +
        "\",\"arguments\":" + arguments + "}}";

    /// <summary>One <c>resources/read</c> message.</summary>
    private static string Read(int id, string uri) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(CultureInfo.InvariantCulture) +
        ",\"method\":\"resources/read\",\"params\":{\"uri\":\"" + uri + "\"}}";

}
