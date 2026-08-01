using System.Globalization;
using System.Net.Sockets;
using System.Text;
using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Minimal Hamlib rigctld client: sends <c>\get_freq</c> and parses Hz.
/// </summary>
public sealed class RadioCatFrequencyClient(IOptionsMonitor<GatewayPulseOptions> options)
{
    public async Task<(decimal? FrequencyKhz, DateTimeOffset? UpdatedAt)> TryGetFrequencyAsync(
        CancellationToken cancellationToken)
    {
        var cat = options.CurrentValue.RadioCat ?? new RadioCatOptions();
        if (!cat.Enabled || cat.Port <= 0 || string.IsNullOrWhiteSpace(cat.Host))
            return (null, null);

        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Math.Clamp(cat.TimeoutMs, 100, 2000));
            await client.ConnectAsync(cat.Host.Trim(), cat.Port, timeoutCts.Token);
            await using var stream = client.GetStream();
            var command = Encoding.ASCII.GetBytes("\\get_freq\n");
            await stream.WriteAsync(command, timeoutCts.Token);
            var buffer = new byte[128];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token);
            if (read <= 0)
                return (null, null);

            var text = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            // Responses look like "14100000" or "Frequency: 14100000"
            var token = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(t => t.Any(char.IsDigit));
            if (token is null)
                return (null, null);
            token = new string(token.Where(c => char.IsDigit(c) || c == '.').ToArray());
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) || hz <= 0)
                return (null, null);

            // rigctld returns Hz; accept kHz if value looks too small for HF Hz.
            var khz = hz >= 1000 ? (decimal)(hz / 1000.0) : (decimal)hz;
            return (khz, DateTimeOffset.UtcNow);
        }
        catch
        {
            return (null, null);
        }
    }
}
