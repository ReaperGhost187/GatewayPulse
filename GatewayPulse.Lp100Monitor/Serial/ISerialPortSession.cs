namespace GatewayPulse.Lp100Monitor.Serial;

public interface ISerialPortSession : IAsyncDisposable
{
    string PortName { get; }
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task WriteAsync(string text, CancellationToken cancellationToken);
    Task<string> ReadAvailableAsync(CancellationToken cancellationToken);
    Task CloseAsync();
}
