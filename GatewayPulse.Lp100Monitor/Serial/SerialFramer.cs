using System.Text;
using GatewayPulse.Lp100Monitor.Protocol;

namespace GatewayPulse.Lp100Monitor.Serial;

public sealed class SerialFramer
{
    private readonly StringBuilder _buffer = new();

    public void Append(string chunk)
    {
        if (!string.IsNullOrEmpty(chunk))
            _buffer.Append(chunk);

        // Bound memory if noise fills the buffer without delimiters.
        if (_buffer.Length > 4096)
            _buffer.Remove(0, _buffer.Length - 2048);
    }

    public IReadOnlyList<Lp100Frame> DrainFrames()
    {
        var bodies = Lp100FrameParser.ExtractFrameBodies(_buffer);
        var frames = new List<Lp100Frame>(bodies.Count);
        foreach (var body in bodies)
        {
            if (Lp100FrameParser.TryParse(body, out var frame))
                frames.Add(frame);
        }
        return frames;
    }

    public void Reset() => _buffer.Clear();
}
