using System.Net.Http.Json;
using backend.Options;
using Microsoft.Extensions.Options;

namespace backend.Services;

public sealed class RelayGatewayService(HttpClient client, IOptions<RelayGatewayOptions> options)
{
    public async Task<RelayGatewayResult> SendAsync(string network, HttpMethod method, string path,
        object? body, WalletSessionIdentity actor, CancellationToken cancellationToken)
    {
        var target = network switch
        {
            "testnet" => options.Value.Testnet,
            "mainnet" => options.Value.Mainnet,
            _ => null
        };
        if (target is null) return new(400, "{\"error\":\"Unsupported Relay network.\"}");
        if (!Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(target.ServiceToken) || target.ServiceToken.Length < 32
            || string.Equals(options.Value.Testnet.ServiceToken, options.Value.Mainnet.ServiceToken, StringComparison.Ordinal))
        {
            return new(503, "{\"error\":\"Relay gateway is not configured.\"}");
        }

        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Add("X-Pusharoo-Service-Token", target.ServiceToken);
        request.Headers.Add("X-Pusharoo-Actor-Address", actor.Address);
        request.Headers.Add("X-Pusharoo-Actor-Script-Hash", actor.ScriptHash);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new((int)response.StatusCode, content);
        }
        catch (HttpRequestException)
        {
            return new(502, "{\"error\":\"Relay is unavailable.\"}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(504, "{\"error\":\"Relay did not respond in time.\"}");
        }
    }
}

public sealed record RelayGatewayResult(int StatusCode, string Content);
