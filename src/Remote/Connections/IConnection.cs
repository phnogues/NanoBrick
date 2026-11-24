namespace Remote.Connections;

/// <summary>A way to talk to the transmitter: lines of text in both directions.</summary>
public interface IConnection : IAsyncDisposable
{
    /// <summary>A line received from the transmitter ("OK a+ 1", "STATUS …"). May be raised on any thread.</summary>
    event Action<string>? LineReceived;

    /// <summary>The connection dropped by itself (cable unplugged, Bluetooth lost).</summary>
    event Action? Lost;

    /// <summary>Human-readable description, for the status bar.</summary>
    string Description { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task SendAsync(string line);
}
