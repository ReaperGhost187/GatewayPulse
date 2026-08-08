using System.Net.Http;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Builds log-safe APNs failure descriptions. Never includes RequestUri, device tokens,
/// JWT, .p8 material, or raw exception messages (which can embed /3/device/{token}).
/// </summary>
public static class ApnsLogSanitizer
{
    public static string Category(Exception ex) => ex switch
    {
        OperationCanceledException => "canceled",
        HttpRequestException => "http_request",
        IOException => "io",
        InvalidOperationException => "invalid_operation",
        _ => "error"
    };

    /// <summary>Safe one-line description: exception type + category only.</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is AggregateException agg)
        {
            var inner = agg.Flatten().InnerExceptions.FirstOrDefault() ?? agg;
            return $"{inner.GetType().Name}/{Category(inner)}";
        }

        return $"{ex.GetType().Name}/{Category(ex)}";
    }

    /// <summary>
    /// Returns true if text looks like it may contain an APNs device path or token-like secret.
    /// Used by tests and defensive checks — never log strings that fail this.
    /// </summary>
    public static bool LooksUnsafe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        if (text.Contains("/3/device/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Contains("api.push.apple.com", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Contains("api.sandbox.push.apple.com", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
