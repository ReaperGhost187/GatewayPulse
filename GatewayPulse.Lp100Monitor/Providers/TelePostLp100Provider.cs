using GatewayPulse.Lp100Monitor.Protocol;
using GatewayPulse.Lp100Monitor.Serial;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.Lp100Monitor.Providers;

/// <summary>
/// Read-only LP-100A provider. Sends only the documented poll command 'P'.
/// Never sends A/M/F (those change meter configuration / display).
/// </summary>
public sealed class TelePostLp100Provider : IRfMonitor
{
    private readonly Func<string, int, ISerialPortSession> _portFactory;
    private readonly string? _preferredPort;
    private readonly bool _autoDetect;
    private readonly int _baudRate;
    private readonly decimal _txThresholdWatts;
    private readonly SerialFramer _framer = new();
    private readonly List<RfEvent> _events = [];
    private ISerialPortSession? _session;
    private string? _activePort;
    private decimal _sessionPeak;
    private decimal? _lastPeak;
    private DateTimeOffset _lastGood = DateTimeOffset.MinValue;
    private string? _lastError;
    private int _consecutiveFailures;

    public TelePostLp100Provider(
        string? preferredPort,
        bool autoDetect,
        int baudRate,
        decimal txThresholdWatts = 0.05m,
        Func<string, int, ISerialPortSession>? portFactory = null)
    {
        _preferredPort = string.IsNullOrWhiteSpace(preferredPort) ? null : preferredPort.Trim();
        _autoDetect = autoDetect;
        _baudRate = baudRate;
        _txThresholdWatts = txThresholdWatts;
        _portFactory = portFactory ?? ((port, baud) => new SerialPortSession(port, baud));
    }

    public bool IsConnected => _session?.IsOpen == true && _consecutiveFailures < 5;
    public string DeviceName => "TelePost LP-100A";

    public async Task<bool> ConnectAsync()
    {
        await EnsureConnectedAsync(CancellationToken.None);
        return IsConnected;
    }

    public async Task DisconnectAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
        _framer.Reset();
        _activePort = null;
    }

    public async Task<RfTelemetry> GetTelemetryAsync()
    {
        try
        {
            await EnsureConnectedAsync(CancellationToken.None);
            if (_session is null)
                return BuildDisconnected(_lastError ?? "LP-100A serial port is not open.");

            // Documented poll — required to request a telemetry frame (manual p.20).
            await _session.WriteAsync(Lp100FrameParser.PollCommand.ToString(), CancellationToken.None);
            await Task.Delay(40);

            var chunk = await _session.ReadAvailableAsync(CancellationToken.None);
            _framer.Append(chunk);
            // Brief second read for slow adapters.
            if (chunk.Length == 0)
            {
                await Task.Delay(60);
                _framer.Append(await _session.ReadAvailableAsync(CancellationToken.None));
            }

            var frames = _framer.DrainFrames();
            if (frames.Count == 0)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 5)
                {
                    _lastError = "No valid LP-100A frames received.";
                    await SoftReconnectAsync();
                    return BuildDisconnected(_lastError);
                }

                return BuildDisconnected("Waiting for LP-100A poll response. Keep the meter on the Watts screen.");
            }

            var frame = frames[^1];
            _consecutiveFailures = 0;
            _lastError = null;
            _lastGood = DateTimeOffset.UtcNow;
            return BuildFromFrame(frame);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _lastError = ex.Message;
            await SoftReconnectAsync();
            return BuildDisconnected(ex.Message);
        }
    }

    public async Task<RfTelemetry> TestConnectionAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        await EnsureConnectedAsync(cancellationToken);
        if (_session is null)
            return BuildDisconnected(_lastError ?? "Unable to open a serial port.");

        await _session.WriteAsync(Lp100FrameParser.PollCommand.ToString(), cancellationToken);
        await Task.Delay(80, cancellationToken);
        _framer.Append(await _session.ReadAvailableAsync(cancellationToken));
        await Task.Delay(80, cancellationToken);
        _framer.Append(await _session.ReadAvailableAsync(cancellationToken));
        var frames = _framer.DrainFrames();
        if (frames.Count == 0)
            return BuildDisconnected("Port opened but no LP-100A frame was received. Confirm COM port and Watts screen.");

        return BuildFromFrame(frames[^1]);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_session?.IsOpen == true)
            return;

        var candidates = EnumeratePorts();
        Exception? last = null;
        foreach (var port in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var session = _portFactory(port, _baudRate);
                await session.OpenAsync(cancellationToken);
                _session = session;
                _activePort = port;
                _framer.Reset();
                _consecutiveFailures = 0;
                AddEvent("Connected", $"Opened {port} @ {_baudRate} 8N1");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        _lastError = last?.Message ?? "No COM ports available for LP-100A.";
    }

    private IEnumerable<string> EnumeratePorts()
    {
        if (!_autoDetect && _preferredPort is not null)
            return [_preferredPort];

        var ports = SerialPortSession.GetPortNames()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_preferredPort is not null)
        {
            ports.RemoveAll(p => p.Equals(_preferredPort, StringComparison.OrdinalIgnoreCase));
            ports.Insert(0, _preferredPort);
        }
        return ports;
    }

    private async Task SoftReconnectAsync()
    {
        AddEvent("Reconnect", _lastError ?? "Serial session reset");
        await DisconnectAsync();
    }

    private RfTelemetry BuildFromFrame(Lp100Frame frame)
    {
        var transmitting = frame.ForwardPowerWatts > _txThresholdWatts;
        if (transmitting)
        {
            if (frame.ForwardPowerWatts > _sessionPeak)
                _sessionPeak = frame.ForwardPowerWatts;
        }
        else if (_sessionPeak > 0m)
        {
            _lastPeak = _sessionPeak;
            _sessionPeak = 0m;
            AddEvent("TxEnd", $"Last peak {_lastPeak:0.###} W");
        }

        var reflected = RfDerivedMetrics.ReflectedPowerWatts(frame.ForwardPowerWatts, frame.Swr);
        var rl = RfDerivedMetrics.ReturnLossDb(frame.Swr);
        var r = RfDerivedMetrics.ResistanceOhms(frame.ImpedanceOhms, frame.PhaseDegrees);
        var x = RfDerivedMetrics.ReactanceOhms(frame.ImpedanceOhms, frame.PhaseDegrees);
        var now = DateTimeOffset.UtcNow;

        return new RfTelemetry
        {
            SchemaVersion = 1,
            UpdatedAt = now,
            LastUpdate = now,
            Connected = true,
            Provider = "telepost-lp100a",
            Device = DeviceName,
            ConnectionState = RfConnectionStates.Connected,
            ProtocolStatus = "OK",
            Transmitting = transmitting,
            ForwardPowerWatts = transmitting ? frame.ForwardPowerWatts : 0m,
            PeakForwardPowerWatts = transmitting ? _sessionPeak : null,
            LastPeakForwardPowerWatts = _lastPeak,
            ReflectedPowerWatts = transmitting ? reflected : 0m,
            Swr = transmitting ? frame.Swr : null,
            ReturnLossDb = transmitting ? rl : null,
            Dbm = transmitting ? frame.Dbm : null,
            ImpedanceOhms = transmitting ? frame.ImpedanceOhms : null,
            PhaseDegrees = transmitting ? frame.PhaseDegrees : null,
            ResistanceOhms = transmitting ? r : null,
            ReactanceOhms = transmitting ? x : null,
            PowerRange = RfDerivedMetrics.PowerRangeText(frame.PowerRange),
            MeterMode = RfDerivedMetrics.MeterModeText(frame.MeterMode),
            MeterAlarmSetpoint = RfDerivedMetrics.AlarmSetpointText(frame.AlarmIndex),
            Callsign = string.IsNullOrWhiteSpace(frame.Callsign) ? null : frame.Callsign,
            ComPort = _activePort,
            BaudRate = _baudRate,
            Events = _events.TakeLast(20).ToList()
        };
    }

    private RfTelemetry BuildDisconnected(string error) => new()
    {
        SchemaVersion = 1,
        UpdatedAt = DateTimeOffset.UtcNow,
        LastUpdate = _lastGood == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : _lastGood,
        Connected = false,
        Provider = "telepost-lp100a",
        Device = DeviceName,
        ConnectionState = RfConnectionStates.Disconnected,
        ProtocolStatus = "Error",
        Error = error,
        ComPort = _activePort ?? _preferredPort,
        BaudRate = _baudRate,
        LastPeakForwardPowerWatts = _lastPeak,
        Events = _events.TakeLast(20).ToList()
    };

    private void AddEvent(string type, string detail)
    {
        _events.Add(new RfEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Type = type,
            Detail = detail
        });
        if (_events.Count > 50)
            _events.RemoveRange(0, _events.Count - 50);
    }
}
