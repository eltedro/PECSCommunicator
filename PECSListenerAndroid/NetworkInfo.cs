using Android.Content;
using Android.Net;

namespace PECSListenerAndroid;

/// <summary>Finds the phone's Wi-Fi IPv4 address for display ("http://ip:5050/pecs").</summary>
public static class NetworkInfo
{
    /// <summary>
    /// A non-loopback site-local IPv4 address (192.168.x / 10.x / 172.16-31.x), preferring wlan0.
    /// Falls back to the active network's LinkProperties. Null if none found.
    /// </summary>
    public static string? GetWifiIp(Context context)
    {
        try
        {
            var candidates = new List<(string Name, string Ip)>();
            var nics = Java.Net.NetworkInterface.NetworkInterfaces;
            while (nics != null && nics.HasMoreElements)
            {
                if (nics.NextElement() is not Java.Net.NetworkInterface nic || !nic.IsUp || nic.IsLoopback) continue;
                var addrs = nic.InetAddresses;
                while (addrs != null && addrs.HasMoreElements)
                {
                    if (addrs.NextElement() is Java.Net.Inet4Address a && a.IsSiteLocalAddress && a.HostAddress != null)
                        candidates.Add((nic.Name ?? "", a.HostAddress));
                }
            }
            var best = candidates.FirstOrDefault(c => c.Name == "wlan0");
            if (best.Ip != null) return best.Ip;
            if (candidates.Count > 0) return candidates[0].Ip;
        }
        catch { /* fall through to ConnectivityManager */ }

        try
        {
            var cm = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
            var props = cm?.GetLinkProperties(cm.ActiveNetwork);
            return props?.LinkAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a is Java.Net.Inet4Address)?.HostAddress;
        }
        catch { return null; }
    }

    /// <summary>The URL to put in the Communicator, or just the port if no Wi-Fi address is known.</summary>
    public static string ListenUrl(Context context)
    {
        var ip = GetWifiIp(context);
        return ip == null ? $"port {PecsHttpServer.Port}" : $"http://{ip}:{PecsHttpServer.Port}/pecs";
    }
}
