using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

public static class MobilePushEndpoints
{
    public static IEndpointRouteBuilder MapMobilePushEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/mobile/devices/register", (MobileDeviceRegisterRequest request, MobileDeviceRegistry registry) =>
        {
            try
            {
                var device = registry.Register(request);
                return Results.Json(new
                {
                    ok = true,
                    device = MobileDevicePublicView.From(device)
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            catch (MobileDeviceRegistryFullException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message, maxDevices = ex.MaxDevices }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception)
            {
                return Results.Json(new { ok = false, error = "Registration failed." }, statusCode: 500);
            }
        });

        app.MapDelete("/api/mobile/devices/{deviceId}", (string deviceId, MobileDeviceRegistry registry) =>
        {
            var removed = registry.Remove(deviceId);
            return removed
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { ok = false, error = "Device not found." });
        });

        app.MapGet("/api/mobile/devices/{deviceId}/preferences", (string deviceId, MobileDeviceRegistry registry) =>
        {
            var device = registry.Get(deviceId);
            if (device is null)
                return Results.NotFound(new { ok = false, error = "Device not found." });

            return Results.Json(new
            {
                ok = true,
                deviceId = device.DeviceId,
                preferences = MobilePushPreferences.Normalize(device.Preferences)
            });
        });

        app.MapPut("/api/mobile/devices/{deviceId}/preferences", (
            string deviceId,
            MobilePushPreferences preferences,
            MobileDeviceRegistry registry) =>
        {
            var device = registry.UpdatePreferences(deviceId, preferences);
            if (device is null)
                return Results.NotFound(new { ok = false, error = "Device not found." });

            return Results.Json(new
            {
                ok = true,
                deviceId = device.DeviceId,
                preferences = MobilePushPreferences.Normalize(device.Preferences)
            });
        });

        app.MapPost("/api/mobile/devices/{deviceId}/test-notification", async (
            string deviceId,
            MobileDeviceRegistry registry,
            IApnsClient apnsClient,
            IOptionsMonitor<ApplePushOptions> applePush,
            IOptionsMonitor<GatewayPulseOptions> gatewayOptions) =>
        {
            var device = registry.Get(deviceId);
            if (device is null)
                return Results.NotFound(new { ok = false, error = "Device not found." });
            if (!applePush.CurrentValue.Enabled)
                return Results.Json(new { ok = false, error = "ApplePush is disabled." });
            if (device.TokenStale || string.IsNullOrWhiteSpace(device.ApnsToken))
                return Results.Json(new { ok = false, error = "Device has no active APNs token." });

            var alert = new MobileAlertEvent
            {
                Type = "test.notification",
                Source = "test",
                Severity = MobileAlertSeverity.Info,
                Title = "GatewayPulse Test Notification",
                Message = "This is a test push from GatewayPulse.",
                GatewayName = gatewayOptions.CurrentValue.GatewayName
            };

            // Direct send to the target device (bypasses preference filters).
            var send = await apnsClient.SendAsync(device, alert);
            if (send.TokenInvalid)
            {
                try { registry.MarkTokenStale(deviceId); } catch { /* fail-soft */ }
            }

            return Results.Json(new
            {
                ok = send.Success,
                outcome = send.Outcome,
                statusCode = send.StatusCode
            });
        });

        app.MapGet("/api/mobile/push/status", (
            IOptionsMonitor<ApplePushOptions> applePush,
            MobileDeviceRegistry registry,
            MobilePushHistory history) =>
        {
            var options = applePush.CurrentValue;
            var devices = registry.GetAll();
            var configured =
                !string.IsNullOrWhiteSpace(options.TeamId) &&
                !string.IsNullOrWhiteSpace(options.KeyId) &&
                !string.IsNullOrWhiteSpace(options.PrivateKeyPath) &&
                !string.IsNullOrWhiteSpace(options.BundleId) &&
                File.Exists(options.PrivateKeyPath);

            return Results.Json(new
            {
                ok = true,
                enabled = options.Enabled,
                configured,
                bundleIdConfigured = !string.IsNullOrWhiteSpace(options.BundleId),
                keyFilePresent = !string.IsNullOrWhiteSpace(options.PrivateKeyPath) && File.Exists(options.PrivateKeyPath),
                deviceCount = devices.Count,
                maxDevices = registry.MaxDevices,
                activeTokenCount = devices.Count(d => !d.TokenStale && !string.IsNullOrWhiteSpace(d.ApnsToken)),
                staleTokenCount = devices.Count(d => d.TokenStale || string.IsNullOrWhiteSpace(d.ApnsToken)),
                recent = history.GetRecent(20).Select(e => new
                {
                    e.Id,
                    e.Type,
                    e.Severity,
                    e.Title,
                    e.OccurredAt,
                    e.SentAt,
                    e.DeviceCount,
                    e.SuccessCount,
                    e.FailureCount,
                    e.DeviceIds
                })
                // Intentionally omit TeamId/KeyId/PrivateKeyPath and all secrets/tokens.
            });
        });

        return app;
    }
}

public static class MobilePushServiceCollectionExtensions
{
    public static IServiceCollection AddMobilePush(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ApplePushOptions>(configuration.GetSection(ApplePushOptions.SectionName));
        services.AddSingleton<MobileDeviceRegistry>();
        services.AddSingleton<MobilePushHistory>();
        services.AddSingleton<MobilePushDeliveryTracker>();
        services.AddSingleton<MobilePushDispatchQueue>();
        services.AddSingleton<ApnsJwtProvider>();
        services.AddSingleton<IApnsClient, ApnsClient>();
        services.AddSingleton<MobileAlertRouter>();
        services.AddSingleton<IMobileAlertPublisher, MobileAlertPublisher>();
        services.AddHostedService<MobilePushDispatcher>();
        services.AddHostedService<PowerAlertMonitor>();
        return services;
    }
}
