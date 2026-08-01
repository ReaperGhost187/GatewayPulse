using Microsoft.AspNetCore.WebUtilities;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Display settings for the Winlink RMS Channels Network Map tab.
/// Does not affect gateway monitoring or telemetry collection.
/// </summary>
public sealed class NetworkMapOptions
{
    /// <summary>
    /// Direct CMS gateway map. WinlinkGateways.js reads query key <c>servicecodes</c> (lowercase)
    /// and fills the Service Code(s) box. The Drupal page at winlink.org/RMSChannels only iframes
    /// this map without forwarding query parameters.
    /// </summary>
    public const string DefaultMapUrl = "https://cms.winlink.org:444/maps/WinlinkGateways.aspx";

    public const string LegacyDrupalMapUrl = "https://winlink.org/RMSChannels";

    public string ServiceCode { get; set; } = "";
    public bool RememberServiceCode { get; set; } = true;
    public bool AutoRefresh { get; set; } = true;
    public int AutoRefreshMinutes { get; set; } = 15;
    public bool AutoOpenInBrowser { get; set; } = true;
    public string MapUrl { get; set; } = DefaultMapUrl;

    public static NetworkMapOptions CreateDefaults() => Normalize(new NetworkMapOptions());

    public static NetworkMapOptions Normalize(NetworkMapOptions? options)
    {
        options ??= new NetworkMapOptions();
        options.ServiceCode = (options.ServiceCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(options.MapUrl) || IsLegacyDrupalMapUrl(options.MapUrl))
            options.MapUrl = DefaultMapUrl;
        if (options.AutoRefreshMinutes < 1)
            options.AutoRefreshMinutes = 1;
        if (options.AutoRefreshMinutes > 180)
            options.AutoRefreshMinutes = 180;
        return options;
    }

    public static NetworkMapOptions ForPersistence(NetworkMapOptions? options)
    {
        var normalized = Normalize(options);
        if (!normalized.RememberServiceCode)
            normalized.ServiceCode = "";
        return normalized;
    }

    /// <summary>
    /// Builds the CMS Winlink Gateways map URL with <c>servicecodes</c> applied for autofill.
    /// </summary>
    public static string BuildMapUrl(string? mapUrl, string? serviceCode)
    {
        var baseUrl = string.IsNullOrWhiteSpace(mapUrl) || IsLegacyDrupalMapUrl(mapUrl)
            ? DefaultMapUrl
            : mapUrl.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            baseUrl = DefaultMapUrl;
            uri = new Uri(DefaultMapUrl);
        }
        else
        {
            // Strip prior service-code query keys so reloads stay clean.
            var filtered = QueryHelpers.ParseQuery(uri.Query)
                .Where(pair => !pair.Key.Equals("servicecodes", StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)));
            baseUrl = QueryHelpers.AddQueryString(uri.GetLeftPart(UriPartial.Path), filtered);
        }

        var code = (serviceCode ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(code))
            return baseUrl;

        // WinlinkGateways.js uses args["servicecodes"] with case-sensitive key lookup.
        return QueryHelpers.AddQueryString(baseUrl, "servicecodes", code);
    }

    private static bool IsLegacyDrupalMapUrl(string? mapUrl)
    {
        if (string.IsNullOrWhiteSpace(mapUrl))
            return false;
        if (!Uri.TryCreate(mapUrl.Trim(), UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals("winlink.org", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.TrimEnd('/').Equals("/RMSChannels", StringComparison.OrdinalIgnoreCase);
    }
}
