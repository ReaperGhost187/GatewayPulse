using System.Net;
using GatewayPulse.ServiceHosting;
using Microsoft.AspNetCore.Http;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class LocalRequestPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:127.1.2.3", true)]
    [InlineData("192.168.1.25", false)]
    [InlineData("10.0.0.8", false)]
    [InlineData("::ffff:192.168.1.25", false)]
    public void AllowsOnlyLoopbackAddresses(string address, bool expected)
    {
        Assert.Equal(expected, LocalRequestPolicy.IsAllowed(IPAddress.Parse(address)));
        Assert.Equal(expected, LocalRequestPolicy.IsLoopbackAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void MissingRemoteAddress_IsDenied()
    {
        Assert.False(LocalRequestPolicy.IsAllowed((IPAddress?)null));
        Assert.False(LocalRequestPolicy.IsLoopbackAddress(null));
    }

    [Fact]
    public void Connection_AllowsNullRemoteWhenLocalEndpointIsLoopback()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;
        context.Connection.LocalIpAddress = IPAddress.Loopback;

        Assert.True(LocalRequestPolicy.IsAllowed(context.Connection));
    }

    [Fact]
    public void Connection_DeniesNullRemoteWhenLocalEndpointIsLan()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;
        context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.10");

        Assert.False(LocalRequestPolicy.IsAllowed(context.Connection));
    }

    [Fact]
    public void Connection_AllowsMappedLoopbackRemote()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
        context.Connection.LocalIpAddress = IPAddress.Parse("0.0.0.0");

        Assert.True(LocalRequestPolicy.IsAllowed(context.Connection));
    }
}
