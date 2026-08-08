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
