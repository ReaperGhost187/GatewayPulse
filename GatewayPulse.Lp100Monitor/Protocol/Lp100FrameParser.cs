using System.Globalization;
using System.Text;

namespace GatewayPulse.Lp100Monitor.Protocol;

public static class Lp100FrameParser
{
    public const char PollCommand = 'P';
    public const char FrameDelimiter = ';';
    private const int MinFields = 9;

    /// <summary>
    /// Extracts complete ';'… frames from a rolling serial buffer.
    /// Official frames have no CR/LF; a following ';' starts the next frame.
    /// </summary>
    public static IReadOnlyList<string> ExtractFrameBodies(StringBuilder buffer)
    {
        var bodies = new List<string>();
        while (true)
        {
            var text = buffer.ToString();
            var start = text.IndexOf(FrameDelimiter);
            if (start < 0)
            {
                if (text.Length > 512)
                    buffer.Clear();
                return bodies;
            }

            if (start > 0)
            {
                buffer.Remove(0, start);
                text = buffer.ToString();
            }

            var next = text.IndexOf(FrameDelimiter, 1);
            string candidate;
            int consume;
            if (next >= 0)
            {
                candidate = text[1..next];
                consume = next;
            }
            else
            {
                candidate = text[1..];
                if (!HasMinimumFields(candidate))
                    return bodies;
                consume = text.Length;
            }

            if (HasMinimumFields(candidate))
            {
                bodies.Add(candidate);
                buffer.Remove(0, consume);
                continue;
            }

            if (next >= 0)
            {
                // Skip malformed segment and continue.
                buffer.Remove(0, next);
                continue;
            }

            return bodies;
        }
    }

    public static bool TryParse(string frameBody, out Lp100Frame frame)
    {
        frame = new Lp100Frame();
        if (string.IsNullOrWhiteSpace(frameBody))
            return false;

        var fields = frameBody.Split(',');
        if (fields.Length < MinFields)
            return false;

        if (!TryDecimal(fields[0], out var power)) return false;
        if (!TryDecimal(fields[1], out var z)) return false;
        if (!TryDecimal(fields[2], out var phase)) return false;
        TryInt(fields[3], out var alarm);
        TryInt(fields[5], out var range);
        TryInt(fields[6], out var mode);
        TryDecimal(fields[7], out var dbm);
        if (!TryDecimal(fields[8], out var swr)) return false;

        if (power < 0m || z < 0m || swr < 1m || swr > 99m)
            return false;

        frame = new Lp100Frame
        {
            ForwardPowerWatts = power,
            ImpedanceOhms = z,
            PhaseDegrees = phase,
            AlarmIndex = alarm,
            Callsign = fields[4].Trim(),
            PowerRange = range,
            MeterMode = mode,
            Dbm = dbm,
            Swr = swr,
            RawBody = frameBody
        };
        return true;
    }

    private static bool HasMinimumFields(string body) =>
        body.Split(',').Length >= MinFields;

    private static bool TryDecimal(string s, out decimal value) =>
        decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
