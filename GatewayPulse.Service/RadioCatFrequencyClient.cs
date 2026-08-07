using System.Globalization;
using System.Net.Sockets;
using System.Text;
using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Radio frequency client: CI-V COM (preferred) or Hamlib rigctld.
/// </summary>
public sealed class RadioCatFrequencyClient(
    IOptionsMonitor<GatewayPulseOptions> options,
    IcomCivSerialFrequencyClient civClient)
{
    public async Task<(decimal? FrequencyKhz, string Source, string Status)> TryGetFrequencyAsync(
        CancellationToken cancellationToken)
    {
        var cat = options.CurrentValue.RadioCat ?? new RadioCatOptions();
        if (!cat.Enabled)
            return (null, "Unknown", "Disabled");

        var mode = (cat.Mode ?? "CivCom").Trim();
        if (mode.Equals("Rigctld", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("Hamlib", StringComparison.OrdinalIgnoreCase))
        {
            var (khz, status) = await TryGetRigctldAsync(cat, cancellationToken);
            return (khz, FrequencySources.ManualCat, status);
        }

        var (civKhz, civStatus) = await civClient.TryGetFrequencyAsync(cancellationToken);
        return (civKhz, "CI-V", civStatus);
    }

    private static async Task<(decimal? FrequencyKhz, string Status)> TryGetRigctldAsync(
        RadioCatOptions cat,
        CancellationToken cancellationToken)
    {
        if (cat.Port <= 0 || string.IsNullOrWhiteSpace(cat.Host))
            return (null, "rigctld host/port not set");

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
                return (null, "Empty rigctld response");

            var text = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            var token = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(t => t.Any(char.IsDigit));
            if (token is null)
                return (null, "Unparsed rigctld response");
            token = new string(token.Where(c => char.IsDigit(c) || c == '.').ToArray());
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) || hz <= 0)
                return (null, "Invalid rigctld frequency");

            var khz = hz >= 1000 ? (decimal)(hz / 1000.0) : (decimal)hz;
            return (khz, "OK rigctld");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (null, "rigctld unreachable");
        }
    }
}
