using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class ProjectAccessClient(
    HttpClient httpClient,
    IOptions<PusharooApiOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PusharooApiOptions _options = options.Value;

    public async Task<ProjectAccessResult> ValidateAsync(
        string projectId,
        string operation,
        string requestHash,
        WalletSignatureRequest? signature,
        CancellationToken cancellationToken)
    {
        var endpoint = _options.Endpoint.TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
        {
            return ProjectAccessResult.Fail("Webhook access validation is not configured.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, $"/api/projects/{Uri.EscapeDataString(projectId)}/webhook-access/validate"))
        {
            Content = JsonContent.Create(new { operation, requestHash, signature }, options: JsonOptions)
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return ProjectAccessResult.Allowed;
        }

        var error = await ReadErrorAsync(response, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => ProjectAccessResult.NotFound(error),
            HttpStatusCode.Forbidden => ProjectAccessResult.Forbidden(error),
            _ => ProjectAccessResult.Invalid(error)
        };
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, cancellationToken);
            return string.IsNullOrWhiteSpace(payload?.Error) ? "Webhook authorization failed." : payload.Error;
        }
        catch (JsonException)
        {
            return "Webhook authorization failed.";
        }
    }

    private sealed record ErrorResponse(string? Error);
}

public sealed record ProjectAccessResult(bool IsAllowed, int StatusCode, string Error)
{
    public static ProjectAccessResult Allowed { get; } = new(true, StatusCodes.Status204NoContent, string.Empty);

    public static ProjectAccessResult NotFound(string error) => new(false, StatusCodes.Status404NotFound, error);

    public static ProjectAccessResult Forbidden(string error) => new(false, StatusCodes.Status403Forbidden, error);

    public static ProjectAccessResult Invalid(string error) => new(false, StatusCodes.Status400BadRequest, error);

    public static ProjectAccessResult Fail(string error) => new(false, StatusCodes.Status503ServiceUnavailable, error);
}
