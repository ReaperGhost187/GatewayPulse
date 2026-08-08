using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class ScannerStateTruthfulnessTests
{
    [Fact]
    public void TrimodeClosedWithCivActiveReportsOffline()
    {
        var service = CreateService(radioCatEnabled: true);
        var status = new GatewayStatus { TrimodeSeen = false };

        service.ApplyProbeDisabledScannerStatus(status);

        Assert.False(status.ScannerEnabled);
        Assert.Equal("Trimode offline", status.ScannerStatus);
        Assert.False(GatewayPulseService.IsAuthoritativeScannerStopped(status));
    }

    [Fact]
    public void TrimodeRunningWithoutProbeReportsScannerUnknown()
    {
        var service = CreateService(radioCatEnabled: false);
        var status = new GatewayStatus { TrimodeSeen = true };

        service.ApplyProbeDisabledScannerStatus(status);

        Assert.Null(status.ScannerEnabled);
        Assert.Equal("Not probed", status.ScannerStatus);
    }

    [Fact]
    public void LiveSnapshotPreservesAuthoritativeScannerStoppedState()
    {
        var status = new GatewayStatus
        {
            TrimodeSeen = true,
            ScannerEnabled = false,
            ScannerStatus = "Stopped"
        };

        var snapshot = GatewayPulseService.SnapshotLiveRadio(status);

        Assert.False(snapshot.ScannerEnabled);
        Assert.Equal("Stopped", snapshot.ScannerStatus);
        Assert.True(GatewayPulseService.IsAuthoritativeScannerStopped(snapshot));
    }

    [Fact]
    public void CivActiveDoesNotImplyScannerEnabled()
    {
        var service = CreateService(radioCatEnabled: true);
        var status = new GatewayStatus { TrimodeSeen = true };

        service.ApplyProbeDisabledScannerStatus(status);

        Assert.Null(status.ScannerEnabled);
        Assert.Equal("Not probed", status.ScannerStatus);
    }

    [Fact]
    public void UnknownScannerStateDoesNotTriggerStoppedAlert()
    {
        var status = new GatewayStatus
        {
            TrimodeSeen = true,
            ScannerEnabled = null,
            ScannerStatus = "Not probed"
        };

        Assert.False(GatewayPulseService.IsAuthoritativeScannerStopped(status));
    }

    [Fact]
    public void ScanningSuspendedLogReportsAuthoritativeStop()
    {
        var sessionStart = new DateTime(2026, 8, 6, 0, 7, 27);
        var observation = GatewayPulseService.FindLatestScannerLogObservation(
        [
            "2026-08-06 00:07:51 1.4.2.0 *** Scanner thread started",
            "2026-08-06 00:07:53 1.4.2.0 *** Scanning suspended"
        ],
        sessionStart);
        var status = new GatewayStatus { TrimodeSeen = true };

        GatewayPulseService.ApplyLogScannerObservation(status, observation);

        Assert.False(status.ScannerEnabled);
        Assert.Equal("Stopped", status.ScannerStatus);
        Assert.Equal("Scanning suspended", observation?.Source);
        Assert.True(GatewayPulseService.IsAuthoritativeScannerStopped(status));
    }

    [Fact]
    public void ConfirmedScannerThreadStartedLogReportsScanningAndClearsStop()
    {
        var sessionStart = new DateTime(2026, 8, 6, 0, 7, 27);
        var observation = GatewayPulseService.FindLatestScannerLogObservation(
        [
            "2026-08-06 00:07:53 1.4.2.0 *** Scanning suspended",
            "2026-08-06 00:08:12 1.4.2.0 *** Scanner thread started"
        ],
        sessionStart);
        var status = new GatewayStatus { TrimodeSeen = true };

        GatewayPulseService.ApplyLogScannerObservation(status, observation);

        Assert.True(status.ScannerEnabled);
        Assert.Equal("Scanning", status.ScannerStatus);
        Assert.Equal("Scanner thread started", observation?.Source);
        Assert.False(GatewayPulseService.IsAuthoritativeScannerStopped(status));
    }

    [Fact]
    public void RunningWithoutExplicitScannerLogEventRemainsUnknown()
    {
        var sessionStart = new DateTime(2026, 8, 6, 0, 7, 27);
        var observation = GatewayPulseService.FindLatestScannerLogObservation(
        [
            "2026-08-06 00:07:27 1.4.2.0 *** Program RMS Trimode 1.4.2.0 started ***",
            "2026-08-06 00:07:52 1.4.2.0 *** Pactor Driver started"
        ],
        sessionStart);
        var status = new GatewayStatus { TrimodeSeen = true };

        GatewayPulseService.ApplyLogScannerObservation(status, observation);

        Assert.Null(status.ScannerEnabled);
        Assert.Equal("Not probed", status.ScannerStatus);
        Assert.False(GatewayPulseService.IsAuthoritativeScannerStopped(status));
    }

    [Fact]
    public void ScannerEventFromPreviousTrimodeSessionIsIgnored()
    {
        var currentSessionStart = new DateTime(2026, 8, 6, 0, 7, 27);

        var observation = GatewayPulseService.FindLatestScannerLogObservation(
        [
            "2026-08-06 00:00:35 1.4.2.0 *** Scanner thread started",
            "2026-08-06 00:05:04 1.4.2.0 *** Scanning suspended"
        ],
        currentSessionStart);

        Assert.Null(observation);
    }

    [Theory]
    [InlineData(
        "2026-08-06 00:08:00 1.4.2.0 *** Scanner thread started",
        "2026-08-06 00:08:01 1.4.2.0 *** Scanning suspended",
        false)]
    [InlineData(
        "2026-08-06 00:08:00 1.4.2.0 *** Scanning suspended",
        "2026-08-06 00:08:01 1.4.2.0 *** Scanner thread started",
        true)]
    public void LatestExplicitScannerEventWins(string first, string second, bool expected)
    {
        var observation = GatewayPulseService.FindLatestScannerLogObservation(
            [first, second],
            new DateTime(2026, 8, 6, 0, 7, 27));

        Assert.Equal(expected, observation?.Enabled);
    }

    private static GatewayPulseService CreateService(bool radioCatEnabled)
    {
        var gatewayOptions = new GatewayPulseOptions
        {
            RelayLogs = Path.GetTempPath(),
            TrimodeLogs = Path.GetTempPath(),
            TrimodeIni = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini"),
            RadioCat = new RadioCatOptions
            {
                Enabled = radioCatEnabled,
                Mode = "CivCom"
            },
            TrimodeProbe = new TrimodeProbeOptions
            {
                CommandPortEnabled = false,
                MemoryReadEnabled = false
            }
        };

        var pushover = new PushoverService(
            new StaticOptionsMonitor<PushoverOptions>(new PushoverOptions { Enabled = false }));

        return new GatewayPulseService(
            new StaticOptionsMonitor<GatewayPulseOptions>(gatewayOptions),
            new StaticOptionsMonitor<AlertOptions>(new AlertOptions
            {
                RelayOffline = false,
                TrimodeOffline = false,
                ScannerStopped = false,
                Recovery = false,
                StationConnected = false
            }),
            pushover);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
