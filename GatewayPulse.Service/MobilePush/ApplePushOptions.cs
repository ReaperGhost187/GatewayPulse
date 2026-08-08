namespace GatewayPulse.ServiceHosting;

public sealed class ApplePushOptions
{
    public const string SectionName = "ApplePush";

    /// <summary>Master switch. Default false — fail closed until Apple credentials are configured.</summary>
    public bool Enabled { get; set; }

    public string TeamId { get; set; } = "";
    public string KeyId { get; set; } = "";

    /// <summary>Path to AuthKey_XXXXX.p8 (conceptually under C:\PWM\keys\).</summary>
    public string PrivateKeyPath { get; set; } = "";

    public string BundleId { get; set; } = "";

    /// <summary>Device registry JSON path.</summary>
    public string DeviceRegistryPath { get; set; } = @"C:\PWM\MobilePushDevices.json";

    /// <summary>Bounded push delivery history path (no tokens stored).</summary>
    public string HistoryPath { get; set; } = @"C:\PWM\MobilePushHistory.json";

    public int HistoryCapacity { get; set; } = 200;

    /// <summary>
    /// Soft max registered devices (new deviceIds rejected at cap; token refresh for existing ids still allowed).
    /// Clamped to 25–100 at use; default 50.
    /// </summary>
    public int MaxDevices { get; set; } = 50;

    /// <summary>
    /// Bounded APNs dispatch queue capacity. Overflow drops oldest pending alert.
    /// Clamped to 8–512 at use; default 64.
    /// </summary>
    public int DispatchQueueCapacity { get; set; } = 64;
}

public static class ApnsEnvironments
{
    public const string Sandbox = "sandbox";
    public const string Production = "production";

    public static string Normalize(string? value) =>
        string.Equals(value, Production, StringComparison.OrdinalIgnoreCase)
            ? Production
            : Sandbox;

    public static string Host(string environment) =>
        Normalize(environment) == Production
            ? "api.push.apple.com"
            : "api.sandbox.push.apple.com";
}
