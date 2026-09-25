using System.Globalization;
using System.Text.RegularExpressions;

namespace GatewayPulse.Core;

/// <summary>
/// Reads individual RMS Trimode ADIF sessions. Field lengths, rather than line breaks,
/// delimit values; one record ends at &lt;EOR&gt;. QSO_DATE and TIME_ON are gateway-local
/// in the gateway's Trimode exports, matching its Relay log clock.
/// </summary>
public static class TrimodeAdifLog
{
    private static readonly Regex FieldHeader = new(
        @"^([A-Z0-9_]+):([0-9]+)(?::[^>]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Callsign = new(
        @"^[A-Z0-9]+(?:-[0-9]{1,2})?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IEnumerable<StationHistoryContact> Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < text.Length)
        {
            var open = text.IndexOf('<', offset);
            if (open < 0) break;
            var close = text.IndexOf('>', open + 1);
            if (close < 0) break;

            var header = text[(open + 1)..close].Trim();
            offset = close + 1;
            if (header.Equals("EOR", StringComparison.OrdinalIgnoreCase))
            {
                if (TryContact(fields, out var contact)) yield return contact;
                fields.Clear();
                continue;
            }
            if (header.Equals("EOH", StringComparison.OrdinalIgnoreCase))
            {
                fields.Clear();
                continue;
            }

            var match = FieldHeader.Match(header);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out var length) ||
                length < 0 || length > text.Length - offset)
                continue;

            fields[match.Groups[1].Value] = text.Substring(offset, length).Trim();
            offset += length;
        }
    }

    private static bool TryContact(IReadOnlyDictionary<string, string> fields, out StationHistoryContact contact)
    {
        contact = default;
        if (!fields.TryGetValue("CALL", out var call) ||
            !fields.TryGetValue("QSO_DATE", out var date) ||
            !fields.TryGetValue("TIME_ON", out var time))
            return false;

        var station = call.Trim().ToUpperInvariant();
        if (!Callsign.IsMatch(station) || (time.Length != 4 && time.Length != 6))
            return false;

        if (!DateTime.TryParseExact(date + time.PadRight(6, '0'), "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime))
            return false;

        contact = new StationHistoryContact(localTime, station, "Trimode");
        return true;
    }
}
