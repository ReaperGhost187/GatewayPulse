using GatewayPulse.VictronMonitor.CommandLine;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class MonitorOptionsTests
{
    [Fact]
    public void ParseMock_UsesFiveSecondDefaultAndSupportsOneShot()
    {
        var options = MonitorOptions.Parse(["--mock", "--once", "--output", "C:/data/PowerTelemetry.json"]);

        Assert.Equal(MonitorMode.Mock, options.Mode);
        Assert.True(options.Once);
        Assert.False(options.ForceDemo);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Interval);
        Assert.EndsWith("PowerTelemetry.json", options.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseMock_RejectsProtectedProductionOutputWithoutForceDemo()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MonitorOptions.Parse(["--mock", "--once", "--output", @"C:\PWM\PowerTelemetry.json"]));
        Assert.Contains("C:\\PWM", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseMock_AllowsProtectedProductionOutputWithForceDemo()
    {
        var options = MonitorOptions.Parse([
            "--mock", "--once", "--force-demo", "--output", @"C:\PWM\PowerTelemetry.json"]);

        Assert.True(options.ForceDemo);
        Assert.EndsWith("PowerTelemetry.json", options.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDevice_RejectsMissingAddress()
    {
        var error = Assert.Throws<ArgumentException>(() => MonitorOptions.Parse(["--device"]));
        Assert.Contains("address", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDevice_AcceptsAddressAndKeyFile()
    {
        var keyFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(keyFile, "fac570d66380b797a5b7543758be00e4");
            var options = MonitorOptions.Parse([
                "--device", "--address", "AA:BB:CC:DD:EE:FF", "--key-file", keyFile]);

            Assert.Equal("AA:BB:CC:DD:EE:FF", options.Address);
            Assert.Equal(16, options.AdvertisementKey!.Length);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void Parse_RejectsUnknownOptions()
    {
        var error = Assert.Throws<ArgumentException>(() => MonitorOptions.Parse(["--mock", "--ouput", "wrong.json"]));
        Assert.Contains("unknown", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDevice_RejectsInvalidBluetoothAddress()
    {
        var error = Assert.Throws<ArgumentException>(() => MonitorOptions.Parse(["--device", "--address", "not-an-address"]));
        Assert.Contains("Bluetooth", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsAdvertisementKeyOnCommandLine()
    {
        var error = Assert.Throws<ArgumentException>(() => MonitorOptions.Parse([
            "--device", "--address", "AA:BB:CC:DD:EE:FF", "--key", new string('0', 32)]));
        Assert.Contains("unknown", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDuplicateOptions()
    {
        var error = Assert.Throws<ArgumentException>(() => MonitorOptions.Parse([
            "--mock", "--output", "first.json", "--output", "second.json"]));
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDeviceOnlyOptionsInMockMode()
    {
        var error = Assert.Throws<ArgumentException>(() => MonitorOptions.Parse([
            "--mock", "--address", "AA:BB:CC:DD:EE:FF"]));
        Assert.Contains("device", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseMock_IgnoresVictronKeyEnvironmentVariable()
    {
        var original = Environment.GetEnvironmentVariable("GATEWAYPULSE_VICTRON_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GATEWAYPULSE_VICTRON_KEY", "not-a-valid-key");
            var options = MonitorOptions.Parse(["--mock", "--once"]);

            Assert.Null(options.AdvertisementKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GATEWAYPULSE_VICTRON_KEY", original);
        }
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    [InlineData("1e300")]
    public void Parse_RejectsNonFiniteOrOverflowingInterval(string interval)
    {
        Assert.Throws<ArgumentException>(() => MonitorOptions.Parse(["--mock", "--interval", interval]));
    }

    [Theory]
    [InlineData("--output", "unused.json")]
    [InlineData("--interval", "5")]
    public void ParseScan_RejectsOptionsThatScanModeDoesNotUse(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => MonitorOptions.Parse(["--scan", option, value]));
    }

    [Fact]
    public void ParseMultiDevice_UsesConfigurationFileWithoutLoadingKeysIntoCommandLineOptions()
    {
        var configuration = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configuration, "{\"VictronMonitor\":{\"Devices\":[]}}");

            var options = MonitorOptions.Parse([
                "--multi-device", "--config", configuration,
                "--output", "C:/PWM/PowerTelemetry.json"]);

            Assert.Equal(MonitorMode.MultiDevice, options.Mode);
            Assert.Equal(Path.GetFullPath(configuration), options.ConfigurationPath);
            Assert.Null(options.AdvertisementKey);
            Assert.Null(options.Address);
        }
        finally
        {
            File.Delete(configuration);
        }
    }
}
