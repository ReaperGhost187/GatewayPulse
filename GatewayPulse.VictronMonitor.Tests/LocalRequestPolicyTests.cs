using System.Net;
using GatewayPulse.ServiceHosting;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class LocalRequestPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.25", false)]
    [InlineData("10.0.0.8", false)]
    public void AllowsOnlyLoopbackAddresses(string address, bool expected)
    {
        Assert.Equal(expected, LocalRequestPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Fact]
    public void MissingRemoteAddress_IsDenied()
    {
        Assert.False(LocalRequestPolicy.IsAllowed(null));
    }
}
