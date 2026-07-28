using System.Net;
using System.Net.Sockets;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookDestinationValidator
{
    public async Task<WebhookDestinationValidationResult> ValidateAsync(
        string? webhookUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (uri.IsDefaultPort ? false : uri.Port != 443))
        {
            return WebhookDestinationValidationResult.Fail(
                "WebhookUrl must be an absolute HTTPS URL without embedded credentials.");
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
            return addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address))
                ? WebhookDestinationValidationResult.Fail("WebhookUrl must resolve only to public internet addresses.")
                : WebhookDestinationValidationResult.Valid;
        }
        catch (SocketException)
        {
            return WebhookDestinationValidationResult.Fail("WebhookUrl host could not be resolved.");
        }
    }

    public static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint
            ?? throw new HttpRequestException("Webhook destination endpoint is invalid.");
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException("Webhook destination resolved to a non-public address.");
        }

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, endpoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastError = ex;
            }
        }

        throw new HttpRequestException("Webhook destination could not be connected.", lastError);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                _ => true
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xfe) != 0xfc
                && !(bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
                && bytes[0] != 0xff;
        }

        return false;
    }
}

public sealed record WebhookDestinationValidationResult(bool IsValid, string Error)
{
    public static WebhookDestinationValidationResult Valid { get; } = new(true, string.Empty);

    public static WebhookDestinationValidationResult Fail(string error) => new(false, error);
}
