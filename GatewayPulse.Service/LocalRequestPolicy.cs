using System.Net;

namespace GatewayPulse.ServiceHosting;

public static class LocalRequestPolicy
{
    public static bool IsAllowed(IPAddress? remoteAddress) =>
        remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
}
