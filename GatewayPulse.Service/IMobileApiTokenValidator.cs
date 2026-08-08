namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Validates mobile API bearer tokens. Kept as an interface so the auth mechanism
/// can be replaced (e.g. rotated secrets, IdP) without rewriting middleware.
/// </summary>
public interface IMobileApiTokenValidator
{
    /// <summary>True when a non-empty API token is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Constant-time comparison of the presented bearer token against configuration.
    /// Returns false when the server token is missing/empty or the presented value is invalid.
    /// </summary>
    bool IsValid(string? bearerToken);
}
