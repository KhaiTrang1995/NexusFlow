using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Model;
using FlowX.Functions.Emit;
using FlowX.Functions.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FlowX.Functions;

/// <summary>
/// Turns the trigger attributes a flow already declares into Azure Functions entry points.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The defect this closes.</strong> A trigger is read once, by
/// <see cref="TriggerReader"/>, and becomes an endpoint registration and a manifest entry. It
/// became no Functions binding, so a serverless deployment hand-wrote entry points for
/// knowledge the compiler already had — and a hand-written one is a second copy of a route, a
/// topic and a group, each of which is a term the runtime derives an instance id from. Two
/// copies of a topic do not mislead a reader; they make one message start two flows.
/// </para>
/// <para>
/// <strong>The reading is <see cref="TriggerReader"/>'s and is not repeated.</strong> The
/// project file records why the source is linked rather than referenced. What this generator
/// adds is the mapping from a kind to a platform binding, which is its own subject: the
/// compiler decides what a trigger <em>is</em>, and this decides what a host that scales to
/// zero can <em>serve</em>.
/// </para>
/// <para>
/// <strong>It does nothing at all unless the project opted in</strong> — see
/// <see cref="WorkerAttributeName"/>.
/// </para>
/// </remarks>
[Generator]
public sealed class FlowFunctionGenerator : IIncrementalGenerator
{
    /// <summary>
    /// The type whose presence in the compilation's references is the opt-in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A reference, not an MSBuild property, and the choice is not arbitrary.</strong>
    /// A property would be a second thing to set: a project that references the worker SDK and
    /// forgot the property builds, deploys and serves nothing, with no error anywhere — the
    /// silent-failure shape this repository refuses elsewhere. The reference is the one fact
    /// that cannot be true by accident, because the emitted file does not compile without it:
    /// every attribute it names lives in this assembly. So opting in and being able to consume
    /// what opting in produces are the same act.
    /// </para>
    /// <para>
    /// It is also what <c>FlowPlanGenerator</c> does for the five bindings it already gates —
    /// <c>httpAvailable</c>, <c>busAvailable</c> and their siblings are each one
    /// <c>GetTypeByMetadataName</c> — so a reader who knows one knows this.
    /// </para>
    /// </remarks>
    public const string WorkerAttributeName = "Microsoft.Azure.Functions.Worker.FunctionAttribute";

    private const string FlowAttributeName = "FlowX.FlowAttribute";

    private const string SeamsName = "FlowX.Hosting.FlowPushSeams";

    private const string JsonSerializableAttributeName =
        "System.Text.Json.Serialization.JsonSerializableAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var flows = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FlowAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => Read(ctx.TargetSymbol as INamedTypeSymbol))
            .Where(static declaration => declaration is not null);

        // Both halves of the opt-in as one bool, for FlowPlanGenerator's reason: nothing
        // downstream re-runs when an unrelated reference changes. The seam is checked beside the
        // worker because a project with the worker SDK and no FlowX.Hosting would get a file
        // naming a type it cannot see, which is a compile error in generated code — the least
        // actionable kind there is.
        var available = context.CompilationProvider.Select(
            static (compilation, _) =>
                compilation.GetTypeByMetadataName(WorkerAttributeName) is not null &&
                compilation.GetTypeByMetadataName(SeamsName) is not null);

        // The serialiser context an HTTP entry point runs both contracts through. Read from
        // [JsonSerializable] rather than from what System.Text.Json's own generator produces,
        // for FlowPlanGenerator's reason: the attribute is the author's declaration and is
        // readable now, and the properties are another generator's output.
        var jsonContexts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                JsonSerializableAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => ctx.TargetSymbol.ToDisplayString())
            .Collect();

        var application = context.CompilationProvider.Select(
            static (compilation, _) => compilation.AssemblyName ?? "FlowX");

        context.RegisterSourceOutput(
            flows.Collect().Combine(jsonContexts).Combine(available).Combine(application),
            static (production, data) =>
            {
                var (((declared, contexts), opted), assembly) = data;
                Produce(production, declared, contexts, opted, assembly);
            });
    }

    private static void Produce(
        SourceProductionContext production,
        ImmutableArray<FlowDeclaration?> declarations,
        ImmutableArray<string> jsonContexts,
        bool available,
        string assemblyName)
    {
        if (!available)
        {
            return;
        }

        var json = jsonContexts.Distinct().OrderBy(name => name, System.StringComparer.Ordinal)
            .FirstOrDefault();

        var functions = new List<FunctionModel>();
        var unbound = new List<UnboundTrigger>();
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        var scheduled = new List<string>();
        var observed = new List<string>();

        var flows = declarations
            .Where(static d => d is not null)
            .Select(static d => d!)
            .OrderBy(static d => d.FlowId, System.StringComparer.Ordinal);

        foreach (var flow in flows)
        {
            foreach (var trigger in flow.Triggers)
            {
                Bind(flow, trigger, json, functions, unbound, names, scheduled, observed);
            }
        }

        var timers = new List<TimerFunctionModel>();

        if (scheduled.Count > 0)
        {
            timers.Add(new TimerFunctionModel(TimerPass.Schedule, scheduled));
        }

        if (observed.Count > 0)
        {
            timers.Add(new TimerFunctionModel(TimerPass.Change, observed));
        }

        // A compilation whose flows declare no trigger at all produces no file. Not an empty
        // class: an assembly of manually started flows has no serverless surface, and emitting
        // a type with no members would put a name in the worker's assembly that means nothing.
        if (functions.Count == 0 && timers.Count == 0 && unbound.Count == 0)
        {
            return;
        }

        production.AddSource(
            FunctionEmitter.FileName,
            SourceText.From(
                FunctionEmitter.Emit(assemblyName, functions, timers, unbound), Encoding.UTF8));
    }

    /// <summary>
    /// Which binding one declared trigger becomes, or why it becomes none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapping WP-141 specifies, and the two places it declines are the interesting ones.
    /// </para>
    /// <para>
    /// <strong><c>Change</c> becomes a timer rather than a listener.</strong> The change feed is
    /// a cursor over the outbox, and WP-142 accelerates it with <c>LISTEN</c> on a dedicated
    /// connection. A host that scales to zero holds no connection between invocations and
    /// cannot listen at all, so it reads the cursor on the platform's clock — the correctness
    /// backstop running alone, at that interval's latency.
    /// </para>
    /// <para>
    /// <strong><c>Stream</c> becomes nothing, and this diverges from what the package was
    /// planned to do.</strong> An Event Hubs binding is the easy half; the hard half is that a
    /// window is wider than an invocation. <c>FlowStreamScan.AdmitAsync</c> takes a
    /// <em>closed</em> window, and closing one needs the open windows, the watermark and the
    /// checkpoint that the scan keeps for the life of a process — which a host that scales to
    /// zero does not have. Binding it anyway would mean either a window per batch, which is not
    /// the declared window and would aggregate the wrong records, or resident state on a host
    /// that has none. So the declaration is recorded in the generated file with its reason and
    /// the ASP.NET host continues to serve it.
    /// </para>
    /// </remarks>
    private static void Bind(
        FlowDeclaration flow,
        TriggerModel trigger,
        string? json,
        List<FunctionModel> functions,
        List<UnboundTrigger> unbound,
        HashSet<string> names,
        List<string> scheduled,
        List<string> observed)
    {
        switch (trigger.Kind)
        {
            case "Http" when trigger.Route is not null && flow.CanBeCalled:
            case "Agent" when flow.CanBeCalled:
                if (json is null)
                {
                    unbound.Add(new UnboundTrigger(
                        flow.FlowId,
                        trigger.Kind,
                        "This assembly declares no [JsonSerializable] context, and a worker " +
                        "publishes trimmed — so there is nothing to serialise the contracts " +
                        "through that would survive the publish. Declare a JsonSerializerContext."));

                    return;
                }

                functions.Add(new FunctionModel(
                    NameFor(flow.TypeName, names),
                    FunctionBinding.Http,
                    flow.FlowId,
                    flow.FullTypeName,
                    flow.InputTypeName,
                    flow.OutputTypeName,

                    // An [AgentTrigger] declares no address, so it takes the one every agent
                    // tool is already reached at: a POST whose route is the flow's own id
                    // (FlowX.Mcp serves tools/call into the same FlowEngine.ExecuteAsync).
                    trigger.Method ?? "POST",
                    Route(trigger.Route ?? "agent/" + flow.FlowId),

                    // Off the trigger the manifest published, so the header rule the endpoint
                    // enforces and the rule the contract advertises are one declaration.
                    Idempotent: trigger.Idempotent ?? false,
                    JsonContextName: json));

                return;

            case "Bus" when trigger.Topic is not null && trigger.Group is not null:
                functions.Add(new FunctionModel(
                    NameFor(flow.TypeName, names),
                    FunctionBinding.ServiceBus,
                    flow.FlowId,
                    flow.FullTypeName,
                    Topic: trigger.Topic,
                    Group: trigger.Group));

                return;

            // Both of these join a pass rather than becoming an entry point of their own —
            // TimerFunctionModel says why one timer serves every declaration a sweep fires.
            // The declared expression is deliberately not carried across: it is fired by the
            // sweep, from the model this generator read.
            case "Schedule" when trigger.Cron is not null:
                if (!scheduled.Contains(flow.FlowId))
                {
                    scheduled.Add(flow.FlowId);
                }

                return;

            case "Change" when trigger.Topic is not null:
                if (!observed.Contains(flow.FlowId))
                {
                    observed.Add(flow.FlowId);
                }

                return;

            case "Stream":
                unbound.Add(new UnboundTrigger(
                    flow.FlowId,
                    trigger.Kind,
                    "A window is wider than an invocation. Closing one needs the open windows, " +
                    "the watermark and the checkpoint FlowStreamScan keeps for the life of a " +
                    "process, and a host that scales to zero keeps none of them — so a binding " +
                    "here would aggregate a batch instead of the declared window. Run this " +
                    "flow on the ASP.NET host, which serves the same declaration."));

                return;

            case "Manual":
            case "Cli":
                unbound.Add(new UnboundTrigger(
                    flow.FlowId,
                    trigger.Kind,
                    "There is no address for a platform to bind. This flow is started by code " +
                    "that already holds it, which needs no entry point."));

                return;

            default:
                // A kind with no address this build could read, or one a plugin declared that
                // this build has no shape for. Recorded rather than dropped, for the reason
                // FunctionEmitter.EmitUnbound gives: silence and "the generator did not run"
                // look identical from inside the file.
                unbound.Add(new UnboundTrigger(
                    flow.FlowId,
                    trigger.Kind,
                    "This build read no address off the declaration, so there is nothing to " +
                    "bind a platform trigger to. A plugin's trigger attribute reaches the " +
                    "manifest with its kind and nothing else — see TriggerReader."));

                return;
        }
    }

    /// <summary>
    /// The function's name, which is what the platform addresses it by.
    /// </summary>
    /// <remarks>
    /// The flow's type name, de-duplicated with a suffix. A flow declaring two triggers gets two
    /// entry points and the platform refuses two functions with one name, so the collision is
    /// resolved here rather than at deployment — which is where it would otherwise surface, as a
    /// host that starts and serves neither.
    /// </remarks>
    private static string NameFor(string typeName, HashSet<string> taken)
    {
        if (taken.Add(typeName))
        {
            return typeName;
        }

        // Bounded by the number of names already taken plus one, which is the most any
        // collision can need — an unbounded loop here would be one a defect could hang in.
        for (var suffix = 2; suffix <= taken.Count + 2; suffix++)
        {
            var candidate = typeName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (taken.Add(candidate))
            {
                return candidate;
            }
        }

        return typeName + "_" + taken.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The declared route as the platform wants it, which is without a leading slash.</summary>
    private static string Route(string route) => route.TrimStart('/');

    /// <summary>What this generator needs off a flow's class, and nothing more.</summary>
    /// <remarks>
    /// Deliberately not <c>FlowModel</c>. That model comes from walking the <c>Define</c> chain,
    /// which is the expensive half of the compiler and answers questions no entry point asks:
    /// an entry point needs the identity, the two contracts and the triggers, all of which are
    /// on the class itself. Reading only those keeps this generator's incremental cost to the
    /// class declaration rather than to the flow body.
    /// </remarks>
    private static FlowDeclaration? Read(INamedTypeSymbol? flow)
    {
        if (flow is null)
        {
            return null;
        }

        var identity = flow.GetAttributes()
            .Where(attribute => attribute.AttributeClass?.ToDisplayString() == FlowAttributeName)
            .Select(attribute => attribute.ConstructorArguments.Length > 0
                ? attribute.ConstructorArguments[0].Value as string
                : null)
            .FirstOrDefault();

        if (identity is null)
        {
            return null;
        }

        var triggers = TriggerReader.Read(flow);

        if (triggers.Count == 0)
        {
            // The acceptance criterion stated as a return: a flow with no trigger attribute
            // generates nothing. Not an entry point that would never be reached, and not a
            // comment either — an untriggered flow has declared nothing this package is about.
            return null;
        }

        var contracts = ContractsOf(flow);

        return new FlowDeclaration(
            identity,
            flow.Name,
            flow.ToDisplayString(),
            contracts.Input,
            contracts.Output,
            triggers);
    }

    /// <summary>The two contracts, off <c>Flow&lt;TIn, TOut&gt;</c>.</summary>
    /// <remarks>
    /// Read from the base type rather than from the <c>Define</c> signature, because they are
    /// the same two types and the base is readable without a semantic walk. A flow that does not
    /// derive from the generic base — there is none the abstraction ships — reaches the
    /// unbindable path rather than a guess.
    /// </remarks>
    private static (string? Input, string? Output) ContractsOf(INamedTypeSymbol flow)
    {
        for (var type = flow.BaseType; type is not null; type = type.BaseType)
        {
            if (type.TypeArguments.Length == 2 && type.Name == "Flow")
            {
                return (
                    type.TypeArguments[0].ToDisplayString(),
                    type.TypeArguments[1].ToDisplayString());
            }
        }

        return (null, null);
    }

    /// <summary>One flow's class, as this generator reads it.</summary>
    private sealed record FlowDeclaration(
        string FlowId,
        string TypeName,
        string FullTypeName,
        string? InputTypeName,
        string? OutputTypeName,
        IReadOnlyList<TriggerModel> Triggers)
    {
        /// <summary>Whether an HTTP entry point could hand this flow an input and read a result.</summary>
        public bool CanBeCalled => InputTypeName is not null && OutputTypeName is not null;
    }
}
