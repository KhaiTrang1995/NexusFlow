namespace FlowX.Runtime;

/// <summary>
/// The runtime's only source of wall-clock time.
/// </summary>
/// <remarks>
/// Exists so that deadline behaviour is testable without sleeping, and so that a
/// durable flow can replay against journaled time rather than against whatever the
/// machine says now. Code that reads <see cref="DateTimeOffset.UtcNow"/> directly is
/// code that cannot be replayed — which is why the determinism analyzers
/// (FLOWX1007) reject it inside a durable flow.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant, in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The clock a running host uses.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
