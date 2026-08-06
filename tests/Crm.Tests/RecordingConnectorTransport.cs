using Crm;

namespace Crm.Tests;

/// <summary>
/// The far end of a connector, recorded instead of reached.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one stand-in in this project, and it stands in for the one thing that cannot be
/// real.</strong> Every other claim these tests make is checked against a PostgreSQL that is
/// actually running; a connector's endpoint is a network somebody else owns, and a test that
/// needed one would fail whenever their gateway was slow. What is under test is everything
/// around the send: the claim, the accounting, the status and the error an administrator reads.
/// </para>
/// <para>
/// <strong>It can also refuse</strong>, because the interesting half of a delivery is what
/// happens when the far end says no.
/// </para>
/// </remarks>
internal sealed class RecordingConnectorTransport : IConnectorTransport
{
    private readonly List<PendingDelivery> _sent = [];

    /// <summary>What to answer with, or null to accept. Set by a test before the sweep.</summary>
    public string? Refusal { get; set; }

    /// <summary>Everything this transport was asked to send, in the order it was asked.</summary>
    public IReadOnlyList<PendingDelivery> Sent => _sent;

    /// <inheritdoc />
    public ValueTask<string?> SendAsync(PendingDelivery delivery, CancellationToken cancellationToken)
    {
        _sent.Add(delivery);

        return ValueTask.FromResult(Refusal);
    }
}
