using System.Text.Json.Serialization;

namespace GatewayPulse.ServiceHosting;

public sealed class MobileDeviceRecord
{
    /// <summary>App-generated UUID. Stable identity; APNs token may rotate.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Never returned from API responses. Never logged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string ApnsToken { get; set; } = "";

    /// <summary>sandbox | production</summary>
    public string Environment { get; set; } = ApnsEnvironments.Sandbox;

    public string Platform { get; set; } = "ios";
    public string? AppVersion { get; set; }
    public string? DeviceName { get; set; }
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set on APNs 410; device kept so preferences survive token refresh.</summary>
    public bool TokenStale { get; set; }

    public MobilePushPreferences Preferences { get; set; } = MobilePushPreferences.CreateDefaults();
}

public sealed class MobileDeviceRegistryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<MobileDeviceRecord> Devices { get; set; } = [];
}

public sealed class MobileDeviceRegisterRequest
{
    public string DeviceId { get; set; } = "";
    public string ApnsToken { get; set; } = "";
    public string? Environment { get; set; }
    public string? Platform { get; set; }
    public string? AppVersion { get; set; }
    public string? DeviceName { get; set; }
}

/// <summary>Safe API projection — never includes APNs token.</summary>
public sealed class MobileDevicePublicView
{
    public string DeviceId { get; set; } = "";
    public string Environment { get; set; } = ApnsEnvironments.Sandbox;
    public string Platform { get; set; } = "ios";
    public string? AppVersion { get; set; }
    public string? DeviceName { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool TokenStale { get; set; }
    public bool HasToken { get; set; }
    public MobilePushPreferences Preferences { get; set; } = MobilePushPreferences.CreateDefaults();

    public static MobileDevicePublicView From(MobileDeviceRecord device) => new()
    {
        DeviceId = device.DeviceId,
        Environment = ApnsEnvironments.Normalize(device.Environment),
        Platform = string.IsNullOrWhiteSpace(device.Platform) ? "ios" : device.Platform,
        AppVersion = device.AppVersion,
        DeviceName = device.DeviceName,
        RegisteredAt = device.RegisteredAt,
        UpdatedAt = device.UpdatedAt,
        TokenStale = device.TokenStale,
        HasToken = !string.IsNullOrWhiteSpace(device.ApnsToken),
        Preferences = MobilePushPreferences.Normalize(device.Preferences)
    };
}
