using System.Globalization;
using Microsoft.Extensions.Options;

namespace GatewayPulse.Core;

public sealed class PushoverService
{
    private readonly PushoverOptions _options;

    public int CooldownMinutes => Math.Max(_options.CooldownMinutes, 1);
    public bool SendRecoveryAlerts => _options.SendRecoveryAlerts;

    public PushoverService(IOptions<PushoverOptions> options)
    {
        _options = options.Value;
    }

    public async Task<bool> SendAsync(string title, string message)
    {
        if (!_options.Enabled)
            return false;

        if (string.IsNullOrWhiteSpace(_options.UserKey) || string.IsNullOrWhiteSpace(_options.ApiToken))
            return false;

        using var client = new HttpClient();

        var values = new Dictionary<string, string>
        {
            { "token", _options.ApiToken },
            { "user", _options.UserKey },
            { "title", title },
            { "message", message },
            { "priority", _options.Priority.ToString(CultureInfo.InvariantCulture) }
        };

        if (!string.IsNullOrWhiteSpace(_options.Device))
            values["device"] = _options.Device;

        try
        {
            var response = await client.PostAsync(
                "https://api.pushover.net/1/messages.json",
                new FormUrlEncodedContent(values));

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
