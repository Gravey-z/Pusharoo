using System.Net;

namespace backend.Services;

public sealed class FaucetClientIpResolver
{
    private const string CapturedIpKey = "Pusharoo.Faucet.ClientIp";

    // https://www.cloudflare.com/ips/ — review these ranges when deploying behind Cloudflare.
    private static readonly System.Net.IPNetwork[] CloudflareNetworks =
    [
        System.Net.IPNetwork.Parse("173.245.48.0/20"),
        System.Net.IPNetwork.Parse("103.21.244.0/22"),
        System.Net.IPNetwork.Parse("103.22.200.0/22"),
        System.Net.IPNetwork.Parse("103.31.4.0/22"),
        System.Net.IPNetwork.Parse("141.101.64.0/18"),
        System.Net.IPNetwork.Parse("108.162.192.0/18"),
        System.Net.IPNetwork.Parse("190.93.240.0/20"),
        System.Net.IPNetwork.Parse("188.114.96.0/20"),
        System.Net.IPNetwork.Parse("197.234.240.0/22"),
        System.Net.IPNetwork.Parse("198.41.128.0/17"),
        System.Net.IPNetwork.Parse("162.158.0.0/15"),
        System.Net.IPNetwork.Parse("104.16.0.0/13"),
        System.Net.IPNetwork.Parse("104.24.0.0/14"),
        System.Net.IPNetwork.Parse("172.64.0.0/13"),
        System.Net.IPNetwork.Parse("131.0.72.0/22"),
        System.Net.IPNetwork.Parse("2400:cb00::/32"),
        System.Net.IPNetwork.Parse("2606:4700::/32"),
        System.Net.IPNetwork.Parse("2803:f800::/32"),
        System.Net.IPNetwork.Parse("2405:b500::/32"),
        System.Net.IPNetwork.Parse("2405:8100::/32"),
        System.Net.IPNetwork.Parse("2a06:98c0::/29"),
        System.Net.IPNetwork.Parse("2c0f:f248::/32")
    ];

    public void CaptureDirectCloudflareIp(HttpContext context)
    {
        if (TryGetCloudflareClientIp(context, context.Connection.RemoteIpAddress, out var clientIp))
            context.Items[CapturedIpKey] = clientIp;
    }

    public IPAddress? Resolve(HttpContext context)
    {
        if (context.Items.TryGetValue(CapturedIpKey, out var captured) && captured is IPAddress clientIp)
            return clientIp;
        if (TryGetCloudflareClientIp(context, context.Connection.RemoteIpAddress, out clientIp))
            return clientIp;
        return Normalize(context.Connection.RemoteIpAddress);
    }

    private static bool TryGetCloudflareClientIp(HttpContext context, IPAddress? peer, out IPAddress clientIp)
    {
        clientIp = IPAddress.None;
        peer = Normalize(peer);
        if (peer is null || !CloudflareNetworks.Any(network => network.Contains(peer))) return false;

        return TryReadSingleIp(context, "CF-Connecting-IP", out clientIp);
    }

    private static bool TryReadSingleIp(HttpContext context, string headerName, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (!context.Request.Headers.TryGetValue(headerName, out var values) || values.Count != 1)
            return false;
        var raw = values[0];
        if (raw is null || raw.Contains(',') || !IPAddress.TryParse(raw, out var parsed)) return false;
        ip = Normalize(parsed)!;
        return !IPAddress.Any.Equals(ip) && !IPAddress.IPv6Any.Equals(ip);
    }

    private static IPAddress? Normalize(IPAddress? ip) => ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4() : ip;
}
