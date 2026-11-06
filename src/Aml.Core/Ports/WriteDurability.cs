namespace Aml.Core.Ports;

/// <summary>
/// Whether the writes made in the current flow must survive a power loss once they return (the default), or may be lost
/// with the newest writes and are then replayed by their job (ADR-0026). The store syncs the log for every durable write,
/// and that sync also keeps every write committed before it, since the log is replayed in commit order.
/// </summary>
public static class WriteDurability
{
    private static readonly AsyncLocal<bool> ReplayableFlow = new();

    public static bool IsReplayable => ReplayableFlow.Value;

    /// <summary>Marks the writes of the current async flow as replayable until the returned scope is disposed.</summary>
    public static IDisposable Replayable()
    {
        var outer = ReplayableFlow.Value;
        ReplayableFlow.Value = true;
        return new Scope(outer);
    }

    private sealed class Scope(bool outer) : IDisposable
    {
        public void Dispose() => ReplayableFlow.Value = outer;
    }
}
