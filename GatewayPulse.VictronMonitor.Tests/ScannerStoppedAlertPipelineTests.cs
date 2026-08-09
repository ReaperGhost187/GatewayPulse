using GatewayPulse.Core;
using GatewayPulse.ServiceHosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GatewayPulse.VictronMonitor.Tests;

/// <summary>
/// Scanner-stopped alert pipeline only. Does not exercise CAT/CI-V/frequency detection.
/// </summary>
public sealed class ScannerStoppedAlertPipelineTests
{
    [Fact]
    public void ScanningToStopped_GeneratesExactlyOneScannerStoppedAlert()
    {
        var mobile = new RecordingMobilePublisher();
        var (_, service) = CreateService(mobile);

        PrimeHealthy(service);
        service.EvaluateAlerts(Stopped("GW1"));
        service.EvaluateAlerts(Stopped("GW1"));
        service.EvaluateAlerts(Stopped("GW1"));

        Assert.Equal(
            [MobileAlertTypes.GatewayScannerStopped],
            mobile.Types);
        Assert.Equal("Scanner Stopped", mobile.Events[0].Title);
        Assert.Equal("gateway", mobile.Events[0].Source);
        Assert.Equal(MobileAlertSeverity.Warning, mobile.Events[0].Severity);
    }

    [Fact]
    public void RemainingStopped_DoesNotSpamAlerts()
    {
        var mobile = new RecordingMobilePublisher();
        var (_, service) = CreateService(mobile);

        PrimeHealthy(service);
        service.EvaluateAlerts(Stopped());
        var afterFirst = mobile.Events.Count;

        for (var i = 0; i < 10; i++)
            service.EvaluateAlerts(Stopped());

        Assert.Equal(afterFirst, mobile.Events.Count);
        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayScannerStopped));
    }

    [Fact]
    public void StoppedToScanning_ClearsAndRearms_SecondStopAlertsAgain()
    {
        var mobile = new RecordingMobilePublisher();
        var (alerts, service) = CreateService(mobile, recovery: false);

        PrimeHealthy(service);
        alerts.CurrentValue.Recovery = true;

        service.EvaluateAlerts(Stopped());
        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayScannerStopped));

        service.EvaluateAlerts(Scanning());
        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayRecovery));

        // Production path: Recovery must not permanently suppress the next stop.
        service.EvaluateAlerts(Stopped());

        Assert.Equal(2, mobile.Count(MobileAlertTypes.GatewayScannerStopped));
        Assert.Equal(
            [
                MobileAlertTypes.GatewayScannerStopped,
                MobileAlertTypes.GatewayRecovery,
                MobileAlertTypes.GatewayScannerStopped
            ],
            mobile.Types);
    }

    [Fact]
    public void RecoveryDuringProblemCooldown_RearmsAndAllowsImmediateNextStop()
    {
        var mobile = new RecordingMobilePublisher();
        var (alerts, service) = CreateService(mobile, recovery: false);

        PrimeHealthy(service);
        alerts.CurrentValue.Recovery = true;

        service.EvaluateAlerts(Stopped());
        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayScannerStopped));

        // Still inside the shared problem cooldown from the stop alert — matches
        // production CAT resume shortly after a stop.
        service.SetLastAlertSentUtcForTests(DateTime.UtcNow);
        service.EvaluateAlerts(Scanning());
        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayRecovery));

        service.EvaluateAlerts(Stopped());
        Assert.Equal(2, mobile.Count(MobileAlertTypes.GatewayScannerStopped));
    }

    [Fact]
    public void ProblemToProblemDuringCooldown_DoesNotAdoptKeyWithoutPublish()
    {
        var mobile = new RecordingMobilePublisher();
        var (alerts, service) = CreateService(mobile);

        PrimeHealthy(service);
        alerts.CurrentValue.RelayOffline = true;
        alerts.CurrentValue.ScannerStopped = true;

        service.EvaluateAlerts(Stopped());
        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayScannerStopped));

        service.SetLastAlertSentUtcForTests(DateTime.UtcNow);

        // Stay unhealthy but change problem set during cooldown — must not swallow forever.
        service.EvaluateAlerts(new GatewayStatus
        {
            GatewayName = "GW1",
            RelayRunning = false,
            TrimodeSeen = true,
            ScannerEnabled = false,
            ScannerStatus = "Stopped"
        });
        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayScannerStopped));
        Assert.Equal(0, mobile.Count(MobileAlertTypes.GatewayRelayOffline));

        service.SetLastAlertSentUtcForTests(DateTime.UtcNow.AddMinutes(-10));
        service.EvaluateAlerts(new GatewayStatus
        {
            GatewayName = "GW1",
            RelayRunning = false,
            TrimodeSeen = true,
            ScannerEnabled = false,
            ScannerStatus = "Stopped"
        });

        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayRelayOffline));
        Assert.Equal(1, mobile.Count(MobileAlertTypes.GatewayScannerStopped));
    }

    [Fact]
    public void NotProbedDoesNotAlert_AuthoritativeStopDoes()
    {
        var mobile = new RecordingMobilePublisher();
        var (_, service) = CreateService(mobile);

        PrimeHealthy(service);
        service.EvaluateAlerts(NotProbed());
        Assert.Empty(mobile.Events);

        service.EvaluateAlerts(Stopped());
        Assert.Equal([MobileAlertTypes.GatewayScannerStopped], mobile.Types);
    }

    [Fact]
    public void TrimodeOffline_DoesNotGenerateScannerStopped()
    {
        var mobile = new RecordingMobilePublisher();
        var (_, service) = CreateService(mobile);

        PrimeHealthy(service);
        service.EvaluateAlerts(new GatewayStatus
        {
            GatewayName = "GW1",
            RelayRunning = true,
            TrimodeSeen = false,
            ScannerEnabled = false,
            ScannerStatus = "Trimode offline"
        });

        Assert.Empty(mobile.Events);
        Assert.False(GatewayPulseService.IsAuthoritativeScannerStopped(new GatewayStatus
        {
            TrimodeSeen = false,
            ScannerEnabled = false
        }));
    }

    [Fact]
    public void AuthoritativeStopPredicate_IsTrueForProductionStoppedShape()
    {
        var status = Stopped();
        Assert.True(status.TrimodeSeen);
        Assert.False(status.ScannerEnabled);
        Assert.Equal("Stopped", status.ScannerStatus);
        Assert.True(GatewayPulseService.IsAuthoritativeScannerStopped(status));
    }

    [Fact]
    public async Task ScannerStopped_PassesEnabledMobilePreferencesToApns()
    {
        using var dir = new TempDir();
        var registry = CreateRegistry(dir.Path);
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "scanner-on",
            ApnsToken = "tok-on",
            Environment = "sandbox"
        });
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "scanner-off",
            ApnsToken = "tok-off",
            Environment = "sandbox"
        });
        registry.UpdatePreferences("scanner-off", new MobilePushPreferences
        {
            MasterEnabled = true,
            Gateway = true,
            GatewayScannerStopped = false
        });

        var fake = new FakeApnsClient();
        var router = CreateRouter(registry, fake, dir.Path, enabled: true);

        var mobile = new RecordingMobilePublisher();
        var (_, service) = CreateService(mobile);
        PrimeHealthy(service);
        service.EvaluateAlerts(Stopped());

        Assert.Single(mobile.Events);
        Assert.Equal(MobileAlertTypes.GatewayScannerStopped, mobile.Events[0].Type);
        Assert.True(MobilePushPreferences.CreateDefaults().Allows(MobileAlertTypes.GatewayScannerStopped));

        var result = await router.RouteAsync(mobile.Events[0]);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(["scanner-on"], fake.SentDeviceIds);
    }

    private static void PrimeHealthy(GatewayPulseService service)
    {
        service.ResetAlertStateForTests();
        service.EvaluateAlerts(Scanning());
    }

    private static GatewayStatus Scanning(string gatewayName = "GW1") => new()
    {
        GatewayName = gatewayName,
        RelayRunning = true,
        TrimodeSeen = true,
        ScannerEnabled = true,
        ScannerStatus = "Scanning"
    };

    private static GatewayStatus Stopped(string gatewayName = "GW1") => new()
    {
        GatewayName = gatewayName,
        RelayRunning = true,
        TrimodeSeen = true,
        ScannerEnabled = false,
        ScannerStatus = "Stopped"
    };

    private static GatewayStatus NotProbed(string gatewayName = "GW1") => new()
    {
        GatewayName = gatewayName,
        RelayRunning = true,
        TrimodeSeen = true,
        ScannerEnabled = null,
        ScannerStatus = "Not probed"
    };

    private static (MutableOptionsMonitor<AlertOptions> Alerts, GatewayPulseService Service) CreateService(
        RecordingMobilePublisher mobile,
        bool recovery = false)
    {
        var gatewayOptions = new GatewayPulseOptions
        {
            RelayLogs = Path.GetTempPath(),
            TrimodeLogs = Path.GetTempPath(),
            TrimodeIni = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini"),
            RadioCat = new RadioCatOptions { Enabled = false },
            TrimodeProbe = new TrimodeProbeOptions
            {
                CommandPortEnabled = false,
                MemoryReadEnabled = false
            }
        };

        var pushover = new PushoverService(
            new StaticOptionsMonitor<PushoverOptions>(new PushoverOptions
            {
                Enabled = false,
                CooldownMinutes = 5
            }));

        var alerts = new MutableOptionsMonitor<AlertOptions>(new AlertOptions
        {
            RelayOffline = false,
            TrimodeOffline = false,
            ScannerStopped = true,
            Recovery = recovery,
            StationConnected = false
        });

        var service = new GatewayPulseService(
            new StaticOptionsMonitor<GatewayPulseOptions>(gatewayOptions),
            alerts,
            pushover,
            mobile);

        // Constructor GetStatus() may have primed HEALTHY; start each scenario clean.
        service.ResetAlertStateForTests();
        return (alerts, service);
    }

    private static MobileDeviceRegistry CreateRegistry(string dir)
    {
        var options = new ApplePushOptions
        {
            DeviceRegistryPath = Path.Combine(dir, "MobilePushDevices.json")
        };
        return new MobileDeviceRegistry(new StaticAppleOptions(options));
    }

    private static MobileAlertRouter CreateRouter(
        MobileDeviceRegistry registry,
        IApnsClient client,
        string dir,
        bool enabled)
    {
        var options = new ApplePushOptions
        {
            Enabled = enabled,
            BundleId = "com.example.gp",
            DeviceRegistryPath = Path.Combine(dir, "MobilePushDevices.json"),
            HistoryPath = Path.Combine(dir, "history.json")
        };
        var monitor = new StaticAppleOptions(options);
        return new MobileAlertRouter(
            registry,
            client,
            new MobilePushHistory(monitor),
            monitor,
            NullLogger<MobileAlertRouter>.Instance);
    }

    private sealed class RecordingMobilePublisher : IMobileAlertPublisher
    {
        public List<MobileAlertEvent> Events { get; } = [];

        public IReadOnlyList<string> Types => Events.Select(e => e.Type).ToList();

        public int Count(string type) => Events.Count(e => e.Type == type);

        public Task<MobilePublishAck> PublishAsync(
            MobileAlertEvent alertEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(alertEvent);
            return Task.FromResult(new MobilePublishAck
            {
                Accepted = true,
                Outcome = MobilePublishOutcomes.Queued
            });
        }
    }

    private sealed class FakeApnsClient : IApnsClient
    {
        public ApnsSendResult NextResult { get; set; } = new()
        {
            Success = true,
            StatusCode = 200,
            Outcome = "sent"
        };

        public List<string> SentDeviceIds { get; } = [];

        public Task<ApnsSendResult> SendAsync(
            MobileDeviceRecord device,
            MobileAlertEvent alertEvent,
            CancellationToken cancellationToken = default)
        {
            SentDeviceIds.Add(device.DeviceId);
            return Task.FromResult(NextResult);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gp-scanner-alert-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* ignore */ }
        }
    }

    private sealed class StaticAppleOptions(ApplePushOptions currentValue) : IOptionsMonitor<ApplePushOptions>
    {
        public ApplePushOptions CurrentValue { get; } = currentValue;
        public ApplePushOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ApplePushOptions, string?> listener) => null;
    }

    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
