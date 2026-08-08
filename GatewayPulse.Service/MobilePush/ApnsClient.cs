using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

public sealed class ApnsSendResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string Outcome { get; init; } = "";
    public bool TokenInvalid { get; init; }
    public bool TransientFailure { get; init; }
}

public interface IApnsClient
{
    Task<ApnsSendResult> SendAsync(
        MobileDeviceRecord device,
        MobileAlertEvent alertEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// APNs HTTP/2 client using built-in HttpClient. Fail-soft; never logs tokens/JWT/.p8.
/// </summary>
public sealed class ApnsClient : IApnsClient, IDisposable
{
    private readonly IOptionsMonitor<ApplePushOptions> _options;
    private readonly ApnsJwtProvider _jwtProvider;
    private readonly ILogger<ApnsClient> _logger;
    private readonly HttpClient _httpClient;

    public ApnsClient(
        IOptionsMonitor<ApplePushOptions> options,
        ApnsJwtProvider jwtProvider,
        ILogger<ApnsClient> logger,
        HttpMessageHandler? handler = null)
    {
        _options = options;
        _jwtProvider = jwtProvider;
        _logger = logger;
        _httpClient = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestVersion = HttpVersion.Version20;
        _httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
    }

    public async Task<ApnsSendResult> SendAsync(
        MobileDeviceRecord device,
        MobileAlertEvent alertEvent,
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return new ApnsSendResult { Success = false, Outcome = "disabled", StatusCode = 0 };

        if (string.IsNullOrWhiteSpace(device.ApnsToken) || device.TokenStale)
            return new ApnsSendResult { Success = false, Outcome = "token_missing", StatusCode = 0 };

        if (!_jwtProvider.TryGetToken(out var jwt, out var jwtError))
            return new ApnsSendResult { Success = false, Outcome = jwtError ?? "token_create_failed", StatusCode = 0 };

        var host = ApnsEnvironments.Host(device.Environment);
        var pathToken = device.ApnsToken.Trim();
        var url = $"https://{host}/3/device/{pathToken}";
        var payload = BuildPayload(alertEvent);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
            request.Headers.TryAddWithoutValidation("apns-topic", options.BundleId.Trim());
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", "10");
            request.Headers.TryAddWithoutValidation("apns-expiration", "0");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new ApnsSendResult { Success = true, StatusCode = status, Outcome = "sent" };
            }

            var reason = await ReadReasonAsync(response, cancellationToken);
            if (status == 410 || string.Equals(reason, "Unregistered", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "APNs reported unregistered deviceId={DeviceId} alertType={AlertType} environment={Environment} status={Status} reason={Reason} category=unregistered",
                    device.DeviceId,
                    alertEvent.Type,
                    ApnsEnvironments.Normalize(device.Environment),
                    status,
                    reason);
                return new ApnsSendResult
                {
                    Success = false,
                    StatusCode = status,
                    Outcome = "unregistered",
                    TokenInvalid = true
                };
            }

            if (status is 429 or >= 500)
            {
                _logger.LogWarning(
                    "APNs transient failure deviceId={DeviceId} alertType={AlertType} environment={Environment} status={Status} reason={Reason} category=transient",
                    device.DeviceId,
                    alertEvent.Type,
                    ApnsEnvironments.Normalize(device.Environment),
                    status,
                    reason);
                return new ApnsSendResult
                {
                    Success = false,
                    StatusCode = status,
                    Outcome = "transient",
                    TransientFailure = true
                };
            }

            if (status is 400 or 403)
            {
                _logger.LogWarning(
                    "APNs rejected push deviceId={DeviceId} alertType={AlertType} environment={Environment} status={Status} reason={Reason} category=rejected",
                    device.DeviceId,
                    alertEvent.Type,
                    ApnsEnvironments.Normalize(device.Environment),
                    status,
                    reason);
                return new ApnsSendResult
                {
                    Success = false,
                    StatusCode = status,
                    Outcome = reason,
                    TokenInvalid = string.Equals(reason, "BadDeviceToken", StringComparison.OrdinalIgnoreCase)
                };
            }

            _logger.LogWarning(
                "APNs unexpected status deviceId={DeviceId} alertType={AlertType} environment={Environment} status={Status} reason={Reason} category=unexpected",
                device.DeviceId,
                alertEvent.Type,
                ApnsEnvironments.Normalize(device.Environment),
                status,
                reason);
            return new ApnsSendResult { Success = false, StatusCode = status, Outcome = reason };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "APNs timeout deviceId={DeviceId} alertType={AlertType} environment={Environment} category=timeout",
                device.DeviceId,
                alertEvent.Type,
                ApnsEnvironments.Normalize(device.Environment));
            return new ApnsSendResult
            {
                Success = false,
                StatusCode = 0,
                Outcome = "timeout",
                TransientFailure = true
            };
        }
        catch (Exception ex)
        {
            // Never LogWarning(ex, ...): HttpRequestException / nested text can embed RequestUri
            // with /3/device/{token}. Log type/category only.
            _logger.LogWarning(
                "APNs send failed deviceId={DeviceId} alertType={AlertType} environment={Environment} category={Category} error={Error}",
                device.DeviceId,
                alertEvent.Type,
                ApnsEnvironments.Normalize(device.Environment),
                ApnsLogSanitizer.Category(ex),
                ApnsLogSanitizer.Describe(ex));
            return new ApnsSendResult
            {
                Success = false,
                StatusCode = 0,
                Outcome = "error",
                TransientFailure = true
            };
        }
    }

    public static string BuildPayload(MobileAlertEvent alertEvent)
    {
        var doc = new Dictionary<string, object?>
        {
            ["aps"] = new Dictionary<string, object?>
            {
                ["alert"] = new Dictionary<string, object?>
                {
                    ["title"] = alertEvent.Title,
                    ["body"] = alertEvent.Message
                },
                ["sound"] = "default"
            },
            ["gatewayPulse"] = new Dictionary<string, object?>
            {
                ["id"] = alertEvent.Id,
                ["type"] = alertEvent.Type,
                ["severity"] = alertEvent.Severity,
                ["source"] = alertEvent.Source,
                ["isRecovery"] = alertEvent.IsRecovery,
                ["occurredAt"] = alertEvent.OccurredAt.ToString("O"),
                ["measuredValue"] = alertEvent.MeasuredValue,
                ["threshold"] = alertEvent.Threshold,
                ["unit"] = alertEvent.Unit,
                ["frequencyKhz"] = alertEvent.FrequencyKhz,
                ["gatewayName"] = alertEvent.GatewayName
            }
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    public static string ResolveEndpointHost(string environment) => ApnsEnvironments.Host(environment);

    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return response.ReasonPhrase ?? "error";

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("reason", out var reason))
                return reason.GetString() ?? "error";
            return "error";
        }
        catch
        {
            return response.ReasonPhrase ?? "error";
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
