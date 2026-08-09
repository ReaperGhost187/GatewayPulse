namespace GatewayPulse.Core;

/// <summary>
/// Converts between the PACTOR center/carrier frequencies stored by RMS Trimode
/// and the radio dial frequencies reported by CAT/CI-V.
/// </summary>
public static class PactorFrequency
{
    public const int CenterToDialOffsetHz = 1_500;
    public const decimal CenterToDialOffsetKhz = CenterToDialOffsetHz / 1000m;

    public static int CenterToDialHz(int centerHz) => centerHz - CenterToDialOffsetHz;

    public static decimal CenterToDialKhz(decimal centerKhz) => centerKhz - CenterToDialOffsetKhz;

    public static int DialToCenterHz(int dialHz) => dialHz + CenterToDialOffsetHz;

    public static decimal DialToCenterKhz(decimal dialKhz) => dialKhz + CenterToDialOffsetKhz;
}
