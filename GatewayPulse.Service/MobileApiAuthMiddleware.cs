using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Requires a valid Bearer token for remote (non-loopback) GET access to read-only
/// telemetry APIs. Loopback requests always bypass so the local Windows dashboard
/// and internal GatewayPulse components keep working without a token.
/// </summary>
public sealed class MobileApiAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<MobileApiAuthMiddleware> _logger;

    public MobileApiAuthMiddleware(RequestDelegate next, ILogger<MobileApiAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IMobileApiTokenValidator tokenValidator)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsProtectedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (LocalRequestPolicy.IsAllowed(context.Connection.RemoteIpAddress))
        {
            await _next(context);
            return;
        }

        var reason = EvaluateRemoteAuth(context, tokenValidator);
        if (reason is null)
        {
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "Mobile API auth rejected: path={Path} ip={Ip} reason={Reason}",
            context.Request.Path.Value,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            reason);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
    }

    /// <summary>
    /// Read-only telemetry/mobile paths that require Bearer auth when the caller is not loopback.
    /// Sensitive write/settings routes stay loopback-only via <see cref="LocalRequestPolicy"/>.
    /// </summary>
    public static bool IsProtectedPath(PathString path) =>
        path.StartsWithSegments("/api/status") ||
        path.StartsWithSegments("/api/live-radio") ||
        path.StartsWithSegments("/api/power") ||
        path.StartsWithSegments("/api/rf") ||
        path.StartsWithSegments("/api/preferences") ||
        path.StartsWithSegments("/api/network-map") ||
        path.StartsWithSegments("/api/mobile");

    public static string? EvaluateRemoteAuth(HttpContext context, IMobileApiTokenValidator tokenValidator)
    {
        if (!tokenValidator.IsConfigured)
            return "not_configured";

        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return "missing";

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "invalid";

        var presented = header[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(presented))
            return "missing";

        if (!tokenValidator.IsValid(presented))
            return "invalid";

        return null;
    }
}

public static class MobileApiAuthExtensions
{
    public static IServiceCollection AddMobileApiAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MobileApiOptions>(configuration.GetSection(MobileApiOptions.SectionName));
        services.AddSingleton<IMobileApiTokenValidator, MobileApiTokenValidator>();
        return services;
    }

    public static IApplicationBuilder UseMobileApiAuth(this IApplicationBuilder app) =>
        app.UseMiddleware<MobileApiAuthMiddleware>();
}
