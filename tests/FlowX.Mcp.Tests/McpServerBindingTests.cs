using System.Text.Json;
using FlowX.Generated;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Mcp.Tests;

/// <summary>
/// The invariant that keeps <c>tools/list</c> and <c>tools/call</c> describing one surface:
/// the manifest's agent tools and the host's bindings are the same set, checked when the
/// server is built.
/// </summary>
/// <remarks>
/// Both halves fail at start-up rather than on a call. A tool advertised and unserved is an
/// agent told it can do something that will fail when it tries; a flow served under a name
/// the manifest does not carry is a transport that has escaped the document describing it.
/// Neither is a state a running process should be able to be in, which is the trade
/// <c>FlowBusSubscriptionRegistration</c> makes for a broker it cannot serve.
/// </remarks>
public sealed class McpServerBindingTests
{
    /// <summary>A published tool that nothing binds refuses to start.</summary>
    [Fact]
    public void APublishedToolWithNoBindingIsAStartUpFailure()
    {
        var failure = Should.Throw<InvalidOperationException>(() =>
            new McpServer(new McpManifest(FlowXManifest.Json), []));

        failure.Message.ShouldContain("booking.book");
        failure.Message.ShouldContain("tools/list would advertise a tool");
    }

    /// <summary>A binding the manifest does not publish refuses to start.</summary>
    [Fact]
    public void ABindingWithNoPublishedToolIsAStartUpFailure()
    {
        var failure = Should.Throw<InvalidOperationException>(() =>
            new McpServer(
                new McpManifest(FlowXManifest.Json),
                [
                    new StubTool("booking.book"),
                    new StubTool("booking.quote"),

                    // Declared in this assembly, and deliberately without [AgentTrigger].
                    new StubTool("booking.audit"),
                ]));

        failure.Message.ShouldContain("booking.audit");
    }

    /// <summary>The matched set starts, which is what the two failures above are measured against.</summary>
    [Fact]
    public void TheMatchedSetBinds()
    {
        var server = new McpServer(
            new McpManifest(FlowXManifest.Json),
            [new StubTool("booking.book"), new StubTool("booking.quote")]);

        server.Catalog.Tools.Select(static t => t.Name)
            .ShouldBe(["booking_book", "booking_quote"], ignoreOrder: true);
    }

    /// <summary>
    /// Two flow ids that collide under the tool-name substitution refuse to start.
    /// </summary>
    /// <remarks>
    /// <c>booking.book</c> and <c>booking_book</c> are two flows and one MCP tool name.
    /// Serving both would dispatch every call to whichever sorted first — an agent silently
    /// running a flow it did not ask for, which is the worst thing this package can do.
    /// </remarks>
    [Fact]
    public void TwoFlowsCollidingOnOneToolNameAreRefused()
    {
        var collided = FlowXManifest.Json.Replace(
            @"""id"": ""booking.quote"",
      ""version"": ""1.0.0"",
      ""profile""",
            @"""id"": ""booking_book"",
      ""version"": ""1.0.0"",
      ""profile""",
            StringComparison.Ordinal);

        collided.ShouldNotBe(
            FlowXManifest.Json,
            "The manifest's shape has changed and this fixture no longer collides anything.");

        Should.Throw<ArgumentException>(() => McpToolCatalog.From(collided))
            .Message.ShouldContain("booking_book");
    }

    /// <summary>A manifest that is not JSON is refused with an explanation.</summary>
    [Fact]
    public void AManifestThatIsNotJsonIsRefused() =>
        Should.Throw<ArgumentException>(() => McpToolCatalog.From("{\"flows\":"))
            .Message.ShouldContain("FlowXManifest.Json");

    /// <summary>A binding that runs nothing, so the set arithmetic can be tested on its own.</summary>
    private sealed class StubTool(string flowId) : IFlowAgentTool
    {
        public string FlowId { get; } = flowId;

        public ValueTask<McpToolOutcome> InvokeAsync(
            string name,
            JsonElement? arguments,
            FlowInvocation invocation,
            IServiceProvider services,
            CancellationToken ct) =>
            throw new NotSupportedException("This binding exists to be counted, not called.");
    }
}
