using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

public sealed class MobileApiTokenValidator : IMobileApiTokenValidator
{
    private readonly IOptionsMonitor<MobileApiOptions> _options;

    public MobileApiTokenValidator(IOptionsMonitor<MobileApiOptions> options)
    {
        _options = options;
    }

    public bool IsConfigured
    {
        get
        {
            var token = _options.CurrentValue.ApiToken;
            return !string.IsNullOrWhiteSpace(token);
        }
    }

    public bool IsValid(string? bearerToken)
    {
        var configured = _options.CurrentValue.ApiToken ?? "";
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrEmpty(bearerToken))
            return false;

        return FixedTimeEqualsUtf8(configured, bearerToken);
    }

    /// <summary>
    /// Compares UTF-8 encodings in constant time via SHA-256 digests so unequal lengths
    /// do not short-circuit before a fixed-time compare.
    /// </summary>
    public static bool FixedTimeEqualsUtf8(string expected, string actual)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}
