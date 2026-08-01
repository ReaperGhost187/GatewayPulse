using GatewayPulse.RfMonitoring;

namespace GatewayPulse.Lp100Monitor.Providers;

public sealed class MockRfProvider : IRfMonitor
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private decimal _sessionPeak;
    private decimal? _lastPeak;

    public bool IsConnected => true;
    public string DeviceName => "Mock LP-100A";

    public Task<bool> ConnectAsync() => Task.FromResult(true);
    public Task DisconnectAsync() => Task.CompletedTask;

    public Task<RfTelemetry> GetTelemetryAsync()
    {
        var elapsed = (DateTimeOffset.UtcNow - _started).TotalSeconds;
        var phase = (int)(elapsed / 20.0) % 7;
        var now = DateTimeOffset.UtcNow;

        RfTelemetry telemetry = phase switch
        {
            0 => BuildIdle(now),
            1 => BuildTx(now, (decimal)(85 + 10 * Math.Sin(elapsed)), 1.15m),
            2 => BuildTx(now, (decimal)(40 + 30 * Math.Abs(Math.Sin(elapsed * 2))), 1.25m),
            3 => BuildTx(now, 100m, 2.4m),
            4 => BuildTx(now, 120m, 3.2m),
            5 => BuildDisconnected(now, "Mock disconnect"),
            _ => BuildStale(now)
        };
        return Task.FromResult(telemetry);
    }

    private RfTelemetry BuildIdle(DateTimeOffset now)
    {
        if (_sessionPeak > 0)
        {
            _lastPeak = _sessionPeak;
            _sessionPeak = 0;
        }

        return new RfTelemetry
        {
            SchemaVersion = 1,
            UpdatedAt = now,
            LastUpdate = now,
            Connected = true,
            Provider = "mock",
            Device = DeviceName,
            ConnectionState = RfConnectionStates.Connected,
            ProtocolStatus = "OK",
            Transmitting = false,
            ForwardPowerWatts = 0m,
            ReflectedPowerWatts = 0m,
            PeakForwardPowerWatts = null,
            LastPeakForwardPowerWatts = _lastPeak,
            PowerRange = "High",
            MeterMode = "Average",
            MeterAlarmSetpoint = "2.0",
            Callsign = "MOCK",
            ComPort = "MOCK",
            BaudRate = 115200
        };
    }

    private RfTelemetry BuildTx(DateTimeOffset now, decimal forward, decimal swr)
    {
        if (forward > _sessionPeak)
            _sessionPeak = forward;
        var reflected = RfDerivedMetrics.ReflectedPowerWatts(forward, swr);
        return new RfTelemetry
        {
            SchemaVersion = 1,
            UpdatedAt = now,
            LastUpdate = now,
            Connected = true,
            Provider = "mock",
            Device = DeviceName,
            ConnectionState = RfConnectionStates.Connected,
            ProtocolStatus = "OK",
            Transmitting = true,
            ForwardPowerWatts = forward,
            PeakForwardPowerWatts = _sessionPeak,
            LastPeakForwardPowerWatts = _lastPeak,
            ReflectedPowerWatts = reflected,
            Swr = swr,
            ReturnLossDb = RfDerivedMetrics.ReturnLossDb(swr),
            Dbm = 10m * (decimal)Math.Log10((double)Math.Max(forward, 0.001m)) + 30m,
            ImpedanceOhms = 50m,
            PhaseDegrees = swr > 2m ? 25m : 5m,
            ResistanceOhms = RfDerivedMetrics.ResistanceOhms(50m, swr > 2m ? 25m : 5m),
            ReactanceOhms = RfDerivedMetrics.ReactanceOhms(50m, swr > 2m ? 25m : 5m),
            PowerRange = forward > 500 ? "High" : "Mid",
            MeterMode = "Peak",
            MeterAlarmSetpoint = "2.0",
            Callsign = "MOCK",
            ComPort = "MOCK",
            BaudRate = 115200
        };
    }

    private RfTelemetry BuildDisconnected(DateTimeOffset now, string error) => new()
    {
        SchemaVersion = 1,
        UpdatedAt = now,
        LastUpdate = now.AddSeconds(-2),
        Connected = false,
        Provider = "mock",
        Device = DeviceName,
        ConnectionState = RfConnectionStates.Disconnected,
        ProtocolStatus = "Error",
        Error = error,
        LastPeakForwardPowerWatts = _lastPeak,
        ComPort = "MOCK",
        BaudRate = 115200
    };

    private RfTelemetry BuildStale(DateTimeOffset now) => new()
    {
        SchemaVersion = 1,
        UpdatedAt = now,
        LastUpdate = now.AddSeconds(-60),
        Connected = true,
        Provider = "mock",
        Device = DeviceName,
        ConnectionState = RfConnectionStates.Stale,
        ProtocolStatus = "Stale",
        Stale = true,
        Error = "Mock stale telemetry",
        Transmitting = true,
        ForwardPowerWatts = 75m,
        PeakForwardPowerWatts = 90m,
        ReflectedPowerWatts = 5m,
        Swr = 1.5m,
        LastPeakForwardPowerWatts = _lastPeak,
        ComPort = "MOCK",
        BaudRate = 115200
    };
}
