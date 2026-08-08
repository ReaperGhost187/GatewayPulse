namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Bearer-token credentials for remote mobile/read-only API access.
/// Configure via <c>MobileApi:ApiToken</c> or env <c>MobileApi__ApiToken</c>.
/// Leave empty so remote callers fail closed (401); loopback never requires a token.
/// </summary>
public sealed class MobileApiOptions
{
    public const string SectionName = "MobileApi";

    /// <summary>
    /// Shared secret presented as <c>Authorization: Bearer &lt;token&gt;</c> for non-loopback
    /// requests to protected read APIs. Never hardcode a production value.
    /// </summary>
    public string ApiToken { get; set; } = "";
}
