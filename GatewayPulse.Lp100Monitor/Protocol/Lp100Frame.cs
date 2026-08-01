namespace GatewayPulse.Lp100Monitor.Protocol;

/// <summary>
/// One decoded LP-100A poll response (firmware ≥ 1.2.0.0).
/// Official format (TelePost LP-100A Op Manual, Software section):
/// send ASCII 'P'; response starts with ';' (no CR/LF):
/// Power,Z,Phase,AlarmIdx,Callsign,PowerRange,MeterMode,dBm,SWR
/// </summary>
public sealed class Lp100Frame
{
    public decimal ForwardPowerWatts { get; init; }
    public decimal ImpedanceOhms { get; init; }
    public decimal PhaseDegrees { get; init; }
    public int AlarmIndex { get; init; }
    public string Callsign { get; init; } = "";
    public int PowerRange { get; init; }
    public int MeterMode { get; init; }
    public decimal Dbm { get; init; }
    public decimal Swr { get; init; }
    public string RawBody { get; init; } = "";
}
