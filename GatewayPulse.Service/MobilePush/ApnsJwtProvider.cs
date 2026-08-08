using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// ES256 provider token for APNs using built-in .NET crypto only.
/// Cached and refreshed before expiry. Never logs .p8 contents or JWT values.
/// </summary>
public sealed class ApnsJwtProvider : IDisposable
{
    private readonly IOptionsMonitor<ApplePushOptions> _options;
    private readonly object _lock = new();
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private string? _loadedKeyPath;
    private ECDsa? _ecdsa;

    public ApnsJwtProvider(IOptionsMonitor<ApplePushOptions> options)
    {
        _options = options;
    }

    public bool TryGetToken(out string token, out string? error)
    {
        token = "";
        error = null;
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            error = "disabled";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.TeamId) ||
            string.IsNullOrWhiteSpace(options.KeyId) ||
            string.IsNullOrWhiteSpace(options.PrivateKeyPath) ||
            string.IsNullOrWhiteSpace(options.BundleId))
        {
            error = "not_configured";
            return false;
        }

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(_cachedToken) && now < _expiresAt.AddMinutes(-5))
            {
                token = _cachedToken;
                return true;
            }

            try
            {
                EnsureKeyLoaded(options.PrivateKeyPath);
                if (_ecdsa is null)
                {
                    error = "key_unavailable";
                    return false;
                }

                var issuedAt = now;
                // APNs accepts tokens for up to 1 hour; refresh early.
                var expires = issuedAt.AddMinutes(50);

                var headerJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["alg"] = "ES256",
                    ["kid"] = options.KeyId.Trim()
                });
                var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["iss"] = options.TeamId.Trim(),
                    ["iat"] = issuedAt.ToUnixTimeSeconds()
                });

                var header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
                var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
                var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
                // IEEE P1363 (r||s) is what JWT ES256 expects.
                var signature = _ecdsa.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                _cachedToken = $"{header}.{payload}.{Base64UrlEncode(signature)}";
                _expiresAt = expires;
                token = _cachedToken;
                return true;
            }
            catch (Exception)
            {
                // Never include key/JWT material in the error string.
                error = "token_create_failed";
                InvalidateUnlocked();
                return false;
            }
        }
    }

    public void Invalidate()
    {
        lock (_lock)
            InvalidateUnlocked();
    }

    internal static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryCreateTokenForTests(
        ECDsa ecdsa,
        string teamId,
        string keyId,
        DateTimeOffset issuedAt,
        out string token)
    {
        var headerJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["alg"] = "ES256",
            ["kid"] = keyId
        });
        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = teamId,
            ["iat"] = issuedAt.ToUnixTimeSeconds()
        });
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var signature = ecdsa.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        token = $"{header}.{payload}.{Base64UrlEncode(signature)}";
        return true;
    }

    private void EnsureKeyLoaded(string privateKeyPath)
    {
        var path = Path.GetFullPath(privateKeyPath);
        if (_ecdsa is not null && string.Equals(_loadedKeyPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        InvalidateUnlocked();
        if (!File.Exists(path))
            throw new FileNotFoundException("APNs private key file was not found.");

        var pem = File.ReadAllText(path);
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        _ecdsa = ecdsa;
        _loadedKeyPath = path;
    }

    private void InvalidateUnlocked()
    {
        _cachedToken = null;
        _expiresAt = DateTimeOffset.MinValue;
        _ecdsa?.Dispose();
        _ecdsa = null;
        _loadedKeyPath = null;
    }

    public void Dispose()
    {
        lock (_lock)
            InvalidateUnlocked();
    }
}
