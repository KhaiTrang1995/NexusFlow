using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace FlowX.Hosting;

/// <summary>
/// Refuses to start a node that declared an address nothing on it serves.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure this converts.</strong> Before it, a host that forgot
/// <c>AddFlowXSubscriptions()</c> started cleanly, reported healthy and ran no subscription — and
/// the only visible symptom was that messages did not arrive, which is indistinguishable from a
/// broker that is not delivering. The same holds for change feeds, schedules and streams. Now the
/// pod does not become ready, and the message names the flow, the attribute and the call that is
/// missing.
/// </para>
/// <para>
/// <strong>Why it runs as a hosted service and not inside <c>AddFlowX</c>.</strong> The
/// registrations happen after <c>Build()</c> — <c>app.Services.AddFlowXSubscriptions()</c> is a
/// call on the built provider, because a subscription needs a resolved dispatcher. So the earliest
/// moment the catalogues are complete is the start of the host, which is here.
/// </para>
/// <para>
/// <strong>HTTP is not checked.</strong> A missing <c>MapFlowX()</c> is a 404 on every route,
/// which is loud at the first request; the four kinds checked here fail by staying quiet for
/// ever, which is the property that earns a start-up refusal.
/// </para>
/// </remarks>
public sealed class FlowXStartupValidation : IHostedService
{
    private readonly FlowXDeclaredTriggers? _declared;
    private readonly FlowBusCatalog _bus;
    private readonly FlowChangeCatalog _change;
    private readonly FlowScheduleCatalog _schedule;
    private readonly FlowStreamCatalog _stream;

    /// <summary>Creates the check.</summary>
    /// <param name="declared">What the flows declared, or null when nothing was generated.</param>
    /// <param name="bus">The bus subscriptions this node serves.</param>
    /// <param name="change">The change subscriptions this node serves.</param>
    /// <param name="schedule">The schedules this node serves.</param>
    /// <param name="stream">The stream subscriptions this node serves.</param>
    /// <exception cref="System.ArgumentNullException">A catalogue is null.</exception>
    public FlowXStartupValidation(
        FlowXDeclaredTriggers? declared,
        FlowBusCatalog bus,
        FlowChangeCatalog change,
        FlowScheduleCatalog schedule,
        FlowStreamCatalog stream)
    {
        System.ArgumentNullException.ThrowIfNull(bus);
        System.ArgumentNullException.ThrowIfNull(change);
        System.ArgumentNullException.ThrowIfNull(schedule);
        System.ArgumentNullException.ThrowIfNull(stream);

        _declared = declared;
        _bus = bus;
        _change = change;
        _schedule = schedule;
        _stream = stream;
    }

    /// <inheritdoc />
    /// <exception cref="System.InvalidOperationException">
    /// A flow declares an address this node does not serve.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_declared is null)
        {
            // Nothing was generated, so nothing was declared. A host composed by hand is a
            // legitimate shape and this check has no opinion about it.
            return Task.CompletedTask;
        }

        var unserved = _declared.All.Where(IsUnserved).ToList();

        if (unserved.Count > 0)
        {
            throw new System.InvalidOperationException(Explain(unserved));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string Explain(List<DeclaredTrigger> unserved)
    {
        var lines = unserved.Select(static d =>
            "  " + d.FlowId + "@" + d.Version + " declares a " + d.Kind + " trigger on '" +
            d.Address + "', and " + CallFor(d.Kind) + " was not called.");

        return
            "This node declares " + unserved.Count +
            (unserved.Count == 1 ? " address" : " addresses") + " nothing on it serves:" +
            System.Environment.NewLine +
            string.Join(System.Environment.NewLine, lines) +
            System.Environment.NewLine +
            "A declared trigger with no registration compiles, is published and never runs, which " +
            "looks exactly like a transport that is not delivering. Register it on the built " +
            "provider after Build(), or remove the declaration.";
    }

    private static string CallFor(string kind) => kind switch
    {
        "Bus" => "AddFlowXSubscriptions()",
        "Change" => "AddFlowXChangeSubscriptions()",
        "Schedule" => "AddFlowXSchedules()",
        "Stream" => "AddFlowXStreamSubscriptions()",
        _ => "the matching registration",
    };

    private bool IsUnserved(DeclaredTrigger declaration) => declaration.Kind switch
    {
        "Bus" => !_bus.Registrations.Any(r => Names(r.Flow.Plan, declaration)),
        "Change" => !_change.Registrations.Any(r => Names(r.Flow.Plan, declaration)),
        "Schedule" => !_schedule.Registrations.Any(r => Names(r.Flow.Plan, declaration)),
        "Stream" => !_stream.Registrations.Any(r => Names(r.Flow.Plan, declaration)),

        // Http, Agent, Manual and Cli are reached without a catalogue entry, and a mistake in any
        // of them is visible at the first call rather than never.
        _ => false,
    };

    private static bool Names(ExecutionPlan plan, DeclaredTrigger declaration) =>
        string.Equals(plan.Flow.Id, declaration.FlowId, System.StringComparison.Ordinal)
        && string.Equals(plan.Flow.Version, declaration.Version, System.StringComparison.Ordinal);
}
