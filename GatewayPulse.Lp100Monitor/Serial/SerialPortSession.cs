using System.IO.Ports;
using System.Text;

namespace GatewayPulse.Lp100Monitor.Serial;

public sealed class SerialPortSession : ISerialPortSession
{
    private readonly SerialPort _port;

    public SerialPortSession(string portName, int baudRate)
    {
        PortName = portName;
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 200,
            WriteTimeout = 500,
            Encoding = Encoding.ASCII,
            NewLine = "\n",
            DtrEnable = true,
            RtsEnable = true
        };
    }

    public string PortName { get; }
    public bool IsOpen => _port.IsOpen;

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_port.IsOpen)
            _port.Open();
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
        return Task.CompletedTask;
    }

    public Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port.Write(text);
        return Task.CompletedTask;
    }

    public Task<string> ReadAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_port.IsOpen || _port.BytesToRead <= 0)
            return Task.FromResult(string.Empty);
        var bytes = new byte[_port.BytesToRead];
        var read = _port.Read(bytes, 0, bytes.Length);
        return Task.FromResult(Encoding.ASCII.GetString(bytes, 0, read));
    }

    public Task CloseAsync()
    {
        if (_port.IsOpen)
            _port.Close();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        catch
        {
        }
        _port.Dispose();
        return ValueTask.CompletedTask;
    }

    public static string[] GetPortNames() => SerialPort.GetPortNames();
}
