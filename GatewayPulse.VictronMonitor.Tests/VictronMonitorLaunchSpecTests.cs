using GatewayPulse.ServiceHosting;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class VictronMonitorLaunchSpecTests
{
    [Fact]
    public void Create_UsesArgumentListForSensitiveAndUserSuppliedValues()
    {
        var options = new VictronMonitorOptions
        {
            Enabled = true,
            ExecutablePath = @"C:\Program Files\Gateway Pulse\Service\VictronMonitor\GatewayPulse.VictronMonitor.exe",
            Address = "D5:11:30:C1:55:16",
            KeyFile = @"C:\PWM\victron.key",
            OutputPath = @"C:\PWM\PowerTelemetry.json",
            LogsPath = @"C:\PWM\logs",
            IntervalSeconds = 5
        };

        var startInfo = VictronMonitorLaunchSpec.Create(options, @"C:\Program Files\Gateway Pulse\Service");

        Assert.Empty(startInfo.Arguments);
        Assert.Equal(options.ExecutablePath, startInfo.FileName);
        Assert.Equal(
            [
                "--device", "--address", options.Address,
                "--key-file", options.KeyFile,
                "--output", options.OutputPath,
                "--logs", options.LogsPath,
                "--interval", "5"
            ],
            startInfo.ArgumentList);
        Assert.True(startInfo.CreateNoWindow);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void Create_RejectsMalformedBluetoothAddress()
    {
        var options = new VictronMonitorOptions
        {
            Enabled = true,
            ExecutablePath = "collector.exe",
            Address = "not-an-address",
            KeyFile = @"C:\PWM\victron.key",
            OutputPath = @"C:\PWM\PowerTelemetry.json",
            LogsPath = @"C:\PWM\logs"
        };

        Assert.Throws<InvalidOperationException>(
            () => VictronMonitorLaunchSpec.Create(options, @"C:\GatewayPulse"));
    }

    [Fact]
    public void Create_MultiDeviceConfiguration_PassesOnlyConfigurationPathAndSharedOutputArguments()
    {
        var options = new VictronMonitorOptions
        {
            Enabled = true,
            ExecutablePath = @"C:\Program Files\Gateway Pulse\Service\VictronMonitor\GatewayPulse.VictronMonitor.exe",
            ConfigurationPath = @"C:\Program Files\Gateway Pulse\Service\appsettings.json",
            OutputPath = @"C:\PWM\PowerTelemetry.json",
            LogsPath = @"C:\PWM\logs",
            IntervalSeconds = 5,
            Devices =
            [
                new VictronDeviceOptions
                {
                    Type = "BatteryProtect",
                    Address = "D5:11:30:C1:55:16",
                    KeyFile = @"C:\PWM\victron.key",
                    Enabled = true
                },
                new VictronDeviceOptions
                {
                    Type = "SmartShunt",
                    Address = "",
                    KeyFile = @"C:\PWM\smartshunt.key",
                    Enabled = false
                }
            ]
        };

        var startInfo = VictronMonitorLaunchSpec.Create(options, @"C:\Program Files\Gateway Pulse\Service");

        Assert.Equal(
            [
                "--multi-device", "--config", options.ConfigurationPath,
                "--output", options.OutputPath,
                "--logs", options.LogsPath,
                "--interval", "5"
            ],
            startInfo.ArgumentList);
        Assert.DoesNotContain(options.Devices[0].KeyFile, startInfo.ArgumentList);
        Assert.DoesNotContain(options.Devices[1].KeyFile, startInfo.ArgumentList);
    }

    [Fact]
    public void Create_DemoMode_LaunchesMockWithForceDemo()
    {
        var options = new VictronMonitorOptions
        {
            Enabled = false,
            ExecutablePath = @"C:\Program Files\Gateway Pulse\Service\VictronMonitor\GatewayPulse.VictronMonitor.exe",
            OutputPath = @"C:\PWM\PowerTelemetry.json",
            LogsPath = @"C:\PWM\logs",
            IntervalSeconds = 5
        };

        var startInfo = VictronMonitorLaunchSpec.Create(
            options,
            @"C:\Program Files\Gateway Pulse\Service",
            demoMode: true);

        Assert.Equal(
            [
                "--mock", "--force-demo",
                "--output", options.OutputPath,
                "--logs", options.LogsPath,
                "--interval", "5"
            ],
            startInfo.ArgumentList);
    }
}