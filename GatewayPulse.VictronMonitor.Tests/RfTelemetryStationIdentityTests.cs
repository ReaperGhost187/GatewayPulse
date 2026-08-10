using GatewayPulse.RfMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class RfTelemetryStationIdentityTests
{
    [Fact]
    public void ApplyGatewayCallsign_UsesConfiguredStationCallsign()
    {
        var telemetry = new RfTelemetry { Callsign = "N8LP" };

        RfTelemetryStationIdentity.ApplyGatewayCallsign(telemetry, " nnd0wa ");

        Assert.Equal("NND0WA", telemetry.Callsign);
    }

    [Fact]
    public void ApplyGatewayCallsign_ClearsMeterCallsignWhenGatewayCallsignEmpty()
    {
        var telemetry = new RfTelemetry { Callsign = "N8LP" };

        RfTelemetryStationIdentity.ApplyGatewayCallsign(telemetry, "");
        Assert.Null(telemetry.Callsign);

        telemetry.Callsign = "N8LP";
        RfTelemetryStationIdentity.ApplyGatewayCallsign(telemetry, null);
        Assert.Null(telemetry.Callsign);
    }

    [Fact]
    public void ApplyGatewayCallsign_NeverFallsBackToMeterProgrammedValue()
    {
        var telemetry = new RfTelemetry { Callsign = "PERSONAL" };

        RfTelemetryStationIdentity.ApplyGatewayCallsign(telemetry, "   ");

        Assert.Null(telemetry.Callsign);
        Assert.NotEqual("PERSONAL", telemetry.Callsign);
    }
}
