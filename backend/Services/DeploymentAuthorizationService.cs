using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using backend.Models;

namespace backend.Services;

public sealed class DeploymentAuthorizationService(
    NeoWalletSignatureVerifier signatureVerifier,
    WalletSignatureRequestValidator signatureRequestValidator,
    ProjectAuthorizationService authorization)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    public DeploymentAuthorizationContext CreateContext(
        ProjectDocument project,
        ArtifactDocument artifact,
        IReadOnlyList<DeploymentDocument> deployments,
        string network,
        string deployedBy,
        string? notes)
    {
        var confirmedForNetwork = deployments
            .Where(item => string.Equals(item.Network, network.Trim(), StringComparison.Ordinal)
                && string.Equals(item.Status, "confirmed", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(item.ContractHash))
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
        var target = confirmedForNetwork.FirstOrDefault()?.ContractHash;
        var operation = string.IsNullOrWhiteSpace(target) ? "deploy" : "update";
        var manifestJson = JsonSerializer.Serialize(artifact.Manifest, ManifestJsonOptions);
        return new DeploymentAuthorizationContext(
            project.Id,
            artifact.Id,
            artifact.Nef,
            manifestJson,
            network.Trim(),
            operation,
            target,
            confirmedForNetwork.LongLength,
            deployedBy.Trim(),
            string.IsNullOrWhiteSpace(notes) ? string.Empty : notes.Trim());
    }

    public string BuildStartMessage(DeploymentAuthorizationContext context, DeploymentAuthorizationChallengeRequest request)
    {
        return string.Join('\n', new[]
        {
            "Pusharoo deployment authorization",
            "Schema: pusharoo.deployment.v1",
            "Action: deployment.start",
            $"Project ID: {context.ProjectId}",
            $"Artifact ID: {context.ArtifactId}",
            $"NEF SHA-256: {Sha256Hex(context.Nef)}",
            $"Manifest SHA-256: {Sha256Hex(context.ManifestJson)}",
            $"Network: {context.Network}",
            $"Operation: {context.Operation}",
            $"Expected target contract: {context.ExpectedTargetContractHash ?? string.Empty}",
            $"Expected deployment revision: {context.ExpectedDeploymentRevision}",
            $"Wallet: {context.DeployedBy}",
            $"Notes SHA-256: {Sha256Hex(context.Notes)}",
            $"Collaborator grant revision: {{resolved-on-authorization}}",
            $"Audience: {request.Audience.Trim()}",
            $"Origin: {request.Origin.Trim()}",
            $"Issued at UTC: {request.IssuedAtUtc.Trim()}",
            $"Nonce: {request.Nonce.Trim()}"
        });
    }

    public DeploymentAuthorizationValidationResult ValidateStart(
        ProjectDocument project,
        ArtifactDocument artifact,
        IReadOnlyList<DeploymentDocument> deployments,
        StartDeploymentAttemptRequest request)
    {
        var signature = request.Authorization;
        if (signature is null)
        {
            return Fail(StatusCodes.Status401Unauthorized, "A fresh deployment authorization signature is required.");
        }
        if (string.IsNullOrWhiteSpace(request.DeployedBy)
            || !string.Equals(request.DeployedBy.Trim(), signature.Address.Trim(), StringComparison.Ordinal))
        {
            return Fail(StatusCodes.Status401Unauthorized, "Deployment wallet does not match the authorization signature.");
        }
        if (!string.Equals(request.Network.Trim(), signature.Network.Trim(), StringComparison.Ordinal))
        {
            return Fail(StatusCodes.Status400BadRequest, "Deployment network must match the signing wallet network.");
        }
        var signatureError = signatureRequestValidator.Validate(signature);
        if (signatureError is not null)
        {
            return Fail(StatusCodes.Status401Unauthorized, signatureError);
        }

        var permission = authorization.CanDeployToNetwork(project, signature.Address, request.Network);
        if (!permission.IsAllowed)
        {
            return Fail(permission.StatusCode, permission.Error);
        }

        var context = CreateContext(project, artifact, deployments, request.Network, request.DeployedBy, request.Notes);
        var challenge = new DeploymentAuthorizationChallengeRequest(
            request.ArtifactId,
            request.Network,
            request.DeployedBy,
            request.Notes,
            signature.Origin,
            signature.Audience,
            signature.IssuedAtUtc,
            signature.Nonce);
        var message = BuildStartMessage(context, challenge).Replace(
            "{resolved-on-authorization}",
            permission.GrantRevision?.ToString() ?? "owner",
            StringComparison.Ordinal);
        if (!string.Equals(signature.Message, message, StringComparison.Ordinal))
        {
            return Fail(StatusCodes.Status401Unauthorized, "Wallet signature message does not match the deployment authorization.");
        }
        var verification = signatureVerifier.Verify(signature, message);
        if (!verification.IsValid)
        {
            return Fail(StatusCodes.Status401Unauthorized, verification.Error);
        }

        // The existing RPC inspector cannot prove the selected NEF and manifest were
        // the deployed invocation arguments. Do not enable collaborator broadcasts
        // until the prepared-intent workflow enforces that binding.
        if (!permission.IsOwner)
        {
            return Fail(StatusCodes.Status409Conflict,
                "Collaborator deployment requires a prepared transaction intent. Pusharoo cannot yet prove an arbitrary submitted transaction used the authorized artifact.");
        }

        var snapshot = new DeploymentAuthorizationSnapshot
        {
            InitiatorWalletAddress = verification.Address ?? signature.Address.Trim(),
            InitiatorScriptHash = NormalizeScriptHash(verification.ScriptHash ?? signature.ScriptHash),
            GrantRevision = permission.GrantRevision,
            ExpectedDeploymentRevision = context.ExpectedDeploymentRevision,
            ExpectedTargetContractHash = context.ExpectedTargetContractHash,
            ArtifactNefSha256 = Sha256Hex(context.Nef),
            ArtifactManifestSha256 = Sha256Hex(context.ManifestJson),
            AuthorizationMessageHash = Sha256Hex(message),
            AuthorizedAtUtc = DateTime.UtcNow
        };
        return new DeploymentAuthorizationValidationResult(true, StatusCodes.Status204NoContent, string.Empty, context, snapshot);
    }

    public static string CreateAttemptCapability()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string HashAttemptCapability(string capability)
        => Sha256Hex(capability.Trim());

    public static bool IsValidCapability(DeploymentDocument attempt, string? capability)
    {
        if (string.IsNullOrWhiteSpace(capability)
            || string.IsNullOrWhiteSpace(attempt.AttemptCapabilityHash)
            || attempt.AttemptCapabilityExpiresAtUtc is null
            || attempt.AttemptCapabilityExpiresAtUtc <= DateTime.UtcNow)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(HashAttemptCapability(capability)),
            Convert.FromHexString(attempt.AttemptCapabilityHash));
    }

    private static DeploymentAuthorizationValidationResult Fail(int statusCode, string error)
        => new(false, statusCode, error, null, null);

    private static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));

    private static string Sha256Hex(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string NormalizeScriptHash(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? $"0x{normalized[2..].ToLowerInvariant()}"
            : $"0x{normalized.ToLowerInvariant()}";
    }
}

public sealed record DeploymentAuthorizationContext(
    string ProjectId,
    string ArtifactId,
    byte[] Nef,
    string ManifestJson,
    string Network,
    string Operation,
    string? ExpectedTargetContractHash,
    long ExpectedDeploymentRevision,
    string DeployedBy,
    string Notes);

public sealed record DeploymentAuthorizationValidationResult(
    bool IsValid,
    int StatusCode,
    string Error,
    DeploymentAuthorizationContext? Context,
    DeploymentAuthorizationSnapshot? Snapshot);
