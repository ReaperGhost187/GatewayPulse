using GatewayPulse.Core;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Per-device push preferences. Affect delivery only — never change alert engines.
/// </summary>
public sealed class MobilePushPreferences
{
    public bool MasterEnabled { get; set; } = true;

    public bool Gateway { get; set; } = true;
    public bool Rf { get; set; } = true;
    public bool Power { get; set; } = true;

    public bool GatewayRelayOffline { get; set; } = true;
    public bool GatewayTrimodeOffline { get; set; } = true;
    public bool GatewayScannerStopped { get; set; } = true;
    public bool GatewayRecovery { get; set; } = true;
    public bool GatewayStationConnected { get; set; } = true;

    public bool RfDisconnected { get; set; } = true;
    public bool RfStale { get; set; } = true;
    public bool RfRecovery { get; set; } = true;
    public bool RfSwrWarning { get; set; } = true;
    public bool RfSwrCritical { get; set; } = true;
    public bool RfReflected { get; set; } = true;
    public bool RfHighPower { get; set; } = true;
    public bool RfCleared { get; set; } = true;

    /// <summary>Default OFF on phones — SOC warning band is noisy.</summary>
    public bool PowerWarning { get; set; }

    public bool PowerCritical { get; set; } = true;
    public bool PowerAlarm { get; set; } = true;
    public bool PowerDeviceDisconnected { get; set; } = true;
    public bool PowerDeviceStale { get; set; } = true;
    public bool PowerOutputOff { get; set; } = true;
    public bool PowerRecovery { get; set; } = true;

    public static MobilePushPreferences CreateDefaults() => new();

    public static MobilePushPreferences Normalize(MobilePushPreferences? preferences) =>
        preferences ?? CreateDefaults();

    public bool Allows(string alertType)
    {
        if (!MasterEnabled || string.IsNullOrWhiteSpace(alertType))
            return false;

        return alertType switch
        {
            MobileAlertTypes.GatewayRelayOffline => Gateway && GatewayRelayOffline,
            MobileAlertTypes.GatewayTrimodeOffline => Gateway && GatewayTrimodeOffline,
            MobileAlertTypes.GatewayScannerStopped => Gateway && GatewayScannerStopped,
            MobileAlertTypes.GatewayRecovery => Gateway && GatewayRecovery,
            MobileAlertTypes.GatewayStationConnected => Gateway && GatewayStationConnected,

            MobileAlertTypes.RfDisconnected => Rf && RfDisconnected,
            MobileAlertTypes.RfStale => Rf && RfStale,
            MobileAlertTypes.RfRecovery => Rf && RfRecovery,
            MobileAlertTypes.RfSwrWarning => Rf && RfSwrWarning,
            MobileAlertTypes.RfSwrCritical => Rf && RfSwrCritical,
            MobileAlertTypes.RfReflected => Rf && RfReflected,
            MobileAlertTypes.RfHighPower => Rf && RfHighPower,
            MobileAlertTypes.RfCleared => Rf && RfCleared,

            MobileAlertTypes.PowerWarning => Power && PowerWarning,
            MobileAlertTypes.PowerCritical => Power && PowerCritical,
            MobileAlertTypes.PowerAlarm => Power && PowerAlarm,
            MobileAlertTypes.PowerDeviceDisconnected => Power && PowerDeviceDisconnected,
            MobileAlertTypes.PowerDeviceStale => Power && PowerDeviceStale,
            MobileAlertTypes.PowerOutputOff => Power && PowerOutputOff,
            MobileAlertTypes.PowerRecovery => Power && PowerRecovery,

            _ => false
        };
    }
}
