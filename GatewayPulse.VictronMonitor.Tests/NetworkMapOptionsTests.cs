using GatewayPulse.ServiceHosting;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class NetworkMapOptionsTests
{
    [Fact]
    public void CreateDefaults_UsesCmsGatewayMapAndRememberEnabled()
    {
        var options = NetworkMapOptions.CreateDefaults();

        Assert.Equal(NetworkMapOptions.DefaultMapUrl, options.MapUrl);
        Assert.Contains("cms.winlink.org", options.MapUrl, StringComparison.OrdinalIgnoreCase);
        Assert.True(options.RememberServiceCode);
        Assert.True(options.AutoRefresh);
        Assert.Equal(15, options.AutoRefreshMinutes);
        Assert.Equal("", options.ServiceCode);
    }

    [Fact]
    public void Normalize_UppercasesServiceCodeAndClampsRefresh()
    {
        var options = NetworkMapOptions.Normalize(new NetworkMapOptions
        {
            ServiceCode = " shares ",
            AutoRefreshMinutes = 0,
            MapUrl = ""
        });

        Assert.Equal("SHARES", options.ServiceCode);
        Assert.Equal(1, options.AutoRefreshMinutes);
        Assert.Equal(NetworkMapOptions.DefaultMapUrl, options.MapUrl);
    }

    [Fact]
    public void Normalize_RewritesLegacyDrupalMapUrl()
    {
        var options = NetworkMapOptions.Normalize(new NetworkMapOptions
        {
            MapUrl = NetworkMapOptions.LegacyDrupalMapUrl
        });

        Assert.Equal(NetworkMapOptions.DefaultMapUrl, options.MapUrl);
    }

    [Fact]
    public void ForPersistence_ClearsServiceCodeWhenRememberDisabled()
    {
        var options = NetworkMapOptions.ForPersistence(new NetworkMapOptions
        {
            ServiceCode = "SHARES",
            RememberServiceCode = false
        });

        Assert.False(options.RememberServiceCode);
        Assert.Equal("", options.ServiceCode);
    }

    [Fact]
    public void BuildMapUrl_AppliesLowercaseServicecodesOnCmsMap()
    {
        var url = NetworkMapOptions.BuildMapUrl(
            NetworkMapOptions.LegacyDrupalMapUrl,
            "SHARES");

        Assert.Contains("servicecodes=SHARES", url, StringComparison.Ordinal);
        Assert.DoesNotContain("serviceCodes=", url, StringComparison.Ordinal);
        Assert.StartsWith("https://cms.winlink.org:444/maps/WinlinkGateways.aspx", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMapUrl_OmitsServiceCodeWhenEmpty()
    {
        var url = NetworkMapOptions.BuildMapUrl(
            "https://cms.winlink.org:444/maps/WinlinkGateways.aspx?servicecodes=OLD",
            "");

        Assert.DoesNotContain("servicecodes=", url, StringComparison.OrdinalIgnoreCase);
    }
}
