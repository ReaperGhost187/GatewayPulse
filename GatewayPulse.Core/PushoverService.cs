using System.Globalization;
using Microsoft.Extensions.Options;

namespace GatewayPulse.Core;

public sealed class PushoverService
{
    private readonly IOptionsMonitor<PushoverOptions> _options;

    public int CooldownMinutes => Math.Max(_options.CurrentValue.CooldownMinutes, 1);

    public PushoverService(IOptionsMonitor<PushoverOptions> options)
    {
        _options = options;
    }

    public async Task<bool> SendAsync(string title, string message)
    {
        return await SendAsync(title, message, _options.CurrentValue);
    }

    public async Task<bool> SendAsync(string title, string message, PushoverOptions options)
    {
        if (!options.Enabled)
            return false;

        if (string.IsNullOrWhiteSpace(options.UserKey) || string.IsNullOrWhiteSpace(options.ApiToken))
            return false;

        using var client = new HttpClient();

        var values = new Dictionary<string, string>
        {
            { "token", options.ApiToken },
            { "user", options.UserKey },
            { "title", title },
            { "message", message },
            { "priority", options.Priority.ToString(CultureInfo.InvariantCulture) }
        };

        if (!string.IsNullOrWhiteSpace(options.Device))
            values["device"] = options.Device;

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
