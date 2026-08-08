using System.Net;
using Microsoft.AspNetCore.Http;

namespace GatewayPulse.ServiceHosting;

public static class LocalRequestPolicy
{
    public static bool IsAllowed(ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (IsLoopbackAddress(connection.RemoteIpAddress))
            return true;

        // Some hosts leave RemoteIpAddress unset for accepted local sockets.
        // If we only bound/accepted on loopback, treat the caller as local.
        if (connection.RemoteIpAddress is null && IsLoopbackAddress(connection.LocalIpAddress))
            return true;

        return false;
    }

    public static bool IsAllowed(IPAddress? remoteAddress) => IsLoopbackAddress(remoteAddress);

    /// <summary>
    /// True for 127.0.0.0/8, ::1, and IPv4-mapped loopback (::ffff:127.x.x.x).
    /// <see cref="IPAddress.IsLoopback"/> alone rejects IPv4-mapped loopback, which can
    /// make local tray/dashboard health checks look like "service not responding" (401).
    /// </summary>
    public static bool IsLoopbackAddress(IPAddress? address)
    {
        if (address is null)
            return false;

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.IsIPv4MappedToIPv6)
            return IPAddress.IsLoopback(address.MapToIPv4());

        return false;
    }
}
