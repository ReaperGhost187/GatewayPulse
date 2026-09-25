using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Requires a valid Bearer token for remote (non-loopback) access to protected APIs.
/// Loopback requests always bypass so the local Windows dashboard and internal
/// GatewayPulse components keep working without a token.
///
/// Auth method policy (documented choice):
/// - <c>/api/mobile/*</c>: ALL methods require Bearer when remote (device registration writes).
/// - Other telemetry paths (<c>/api/status</c>, power, rf, …): GET only (unchanged).
/// - Admin routes (<c>/api/settings</c>, <c>/api/testalert</c>, <c>/api/radiocat</c>,
///   <c>/api/rf/test-connection</c>) stay loopback-only via separate middleware — not opened here.
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
        if (!RequiresAuth(context.Request.Method, context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (LocalRequestPolicy.IsAllowed(context))
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
            "Mobile API auth rejected: path={Path} method={Method} ip={Ip} reason={Reason}",
            context.Request.Path.Value,
            context.Request.Method,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            reason);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
    }

    /// <summary>
    /// Paths that require Bearer auth when the caller is not loopback.
    /// Sensitive write/settings routes stay loopback-only via <see cref="LocalRequestPolicy"/>.
    /// </summary>
    public static bool IsProtectedPath(PathString path) =>
        path.StartsWithSegments("/api/status") ||
        path.StartsWithSegments("/api/live-radio") ||
        path.StartsWithSegments("/api/power") ||
        path.StartsWithSegments("/api/rf") ||
        path.StartsWithSegments("/api/preferences") ||
        path.StartsWithSegments("/api/network-map") ||
        path.StartsWithSegments("/api/stations") ||
        path.StartsWithSegments("/api/mobile");

    public static bool IsMobileApiPath(PathString path) =>
        path.StartsWithSegments("/api/mobile");

    /// <summary>
    /// Mobile device APIs authenticate all methods remotely; other telemetry paths stay GET-only.
    /// </summary>
    public static bool RequiresAuth(string method, PathString path)
    {
        if (IsMobileApiPath(path))
            return true;

        return HttpMethods.IsGet(method) && IsProtectedPath(path);
    }

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
