using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using backend.Models;

namespace backend.Services;

public sealed class DeploymentAuthorizationService(
    NeoWalletSignatureVerifier signatureVerifier,
    WalletSignatureRequestValidator signatureRequestValidator,
    ProjectAuthorizationService authorization,
    DeploymentDataService deploymentData)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new BsonValueJsonConverter(), new BsonDocumentJsonConverter() }
    };

    public DeploymentAuthorizationContext CreateContext(
        ProjectDocument project,
        ArtifactDocument artifact,
        IReadOnlyList<DeploymentDocument> deployments,
        string network,
        string deployedBy,
        string? notes,
        NormalizedDeploymentData? normalizedData = null)
    {
        var confirmedForNetwork = deployments
            .Where(item => string.Equals(item.Network, network.Trim(), StringComparison.Ordinal)
                && string.Equals(item.Status, "confirmed", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(item.ContractHash))
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
        var target = confirmedForNetwork.FirstOrDefault()?.ContractHash;
        var operation = string.IsNullOrWhiteSpace(target) ? "deploy" : "update";
        var manifestJson = SerializeManifest(artifact.Manifest);
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
            string.IsNullOrWhiteSpace(notes) ? string.Empty : notes.Trim(),
            normalizedData ?? deploymentData.Normalize(null));
    }

    public static string SerializeManifest(NeoContractManifest manifest)
        => JsonSerializer.Serialize(manifest, ManifestJsonOptions);

    public DeploymentReviewResponse CreateSessionReview(DeploymentAuthorizationContext context)
        => new(context.ArtifactId, context.Network,
            context.Operation, context.ExpectedTargetContractHash, context.ExpectedDeploymentRevision,
            context.DeploymentData.Value, context.DeploymentData.Sha256, context.DeploymentData.FormatVersion,
            Sha256Hex(context.Nef), Sha256Hex(context.ManifestJson), Sha256Hex(context.Notes));

    public DeploymentAuthorizationValidationResult ValidateSessionStart(
        ProjectDocument project,
        ArtifactDocument artifact,
        IReadOnlyList<DeploymentDocument> deployments,
        StartSessionDeploymentAttemptRequest request,
        WalletSessionIdentity actor)
    {
        var permission = authorization.CanDeployToNetwork(project, actor.Address, request.Network);
        if (!permission.IsAllowed) return Fail(permission.StatusCode, permission.Error);

        NormalizedDeploymentData normalizedData;
        try { normalizedData = deploymentData.Normalize(request.DeploymentData); }
        catch (DeploymentDataValidationException exception)
        { return Fail(StatusCodes.Status400BadRequest, exception.Message); }

        var context = CreateContext(project, artifact, deployments, request.Network, actor.Address,
            request.Notes, normalizedData);
        if (context.Operation == "update" && request.DeploymentData is not null)
            return Fail(StatusCodes.Status400BadRequest,
                "Deployment initialization data can only be supplied for a new contract deployment.");

        var review = CreateSessionReview(context);
        if (!string.Equals(request.ExpectedOperation, review.Operation, StringComparison.Ordinal)
            || !string.Equals(request.ExpectedTargetContractHash, review.ExpectedTargetContractHash, StringComparison.OrdinalIgnoreCase)
            || request.ExpectedDeploymentRevision != review.ExpectedDeploymentRevision
            || !string.Equals(request.ArtifactNefSha256, review.ArtifactNefSha256, StringComparison.Ordinal)
            || !string.Equals(request.ArtifactManifestSha256, review.ArtifactManifestSha256, StringComparison.Ordinal)
            || !string.Equals(request.NotesSha256, review.NotesSha256, StringComparison.Ordinal)
            || !string.Equals(request.DeploymentDataSha256, review.DeploymentDataSha256, StringComparison.Ordinal))
        {
            return Fail(StatusCodes.Status409Conflict,
                "The release changed after review. Review it again before opening the wallet.");
        }

        var snapshot = new DeploymentAuthorizationSnapshot
        {
            InitiatorWalletAddress = actor.Address,
            InitiatorScriptHash = NormalizeScriptHash(actor.ScriptHash),
            ExpectedDeploymentRevision = context.ExpectedDeploymentRevision,
            ExpectedTargetContractHash = context.ExpectedTargetContractHash,
            ArtifactNefSha256 = review.ArtifactNefSha256,
            ArtifactManifestSha256 = review.ArtifactManifestSha256,
            AuthorizedAtUtc = DateTime.UtcNow,
            AuthorizationSchemaVersion = 3,
            AuthorizationMethod = "wallet-session",
            SessionReference = actor.SessionHash[..Math.Min(32, actor.SessionHash.Length)],
            ProjectId = context.ProjectId,
            ArtifactId = context.ArtifactId,
            Network = context.Network,
            Operation = context.Operation,
            DeploymentData = normalizedData.Value,
            DeploymentDataSha256 = normalizedData.Sha256,
            DeploymentDataFormatVersion = normalizedData.FormatVersion
        };
        return new DeploymentAuthorizationValidationResult(true, StatusCodes.Status204NoContent,
            string.Empty, context, snapshot, permission.IsOwner);
    }

    public string BuildStartMessage(DeploymentAuthorizationContext context, DeploymentAuthorizationChallengeRequest request)
    {
        var isDataBoundSchema = request.DeploymentData is not null;
        var message = new List<string>
        {
            "Pusharoo deployment authorization",
            $"Schema: {(isDataBoundSchema ? "pusharoo.deployment.v2" : "pusharoo.deployment.v1")}",
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
            $"Audience: {request.Audience.Trim()}",
            $"Origin: {request.Origin.Trim()}",
            $"Issued at UTC: {request.IssuedAtUtc.Trim()}",
            $"Nonce: {request.Nonce.Trim()}"
        };
        if (isDataBoundSchema)
        {
            var audienceIndex = message.FindIndex(line => line.StartsWith("Audience:", StringComparison.Ordinal));
            message.InsertRange(audienceIndex, new[]
            {
                $"Deployment data format: {context.DeploymentData.FormatVersion}",
                $"Deployment data SHA-256: {context.DeploymentData.Sha256}"
            });
        }
        return string.Join('\n', message);
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

        NormalizedDeploymentData normalizedData;
        try
        {
            normalizedData = deploymentData.Normalize(request.DeploymentData);
        }
        catch (DeploymentDataValidationException exception)
        {
            return Fail(StatusCodes.Status400BadRequest, exception.Message);
        }

        var context = CreateContext(project, artifact, deployments, request.Network, request.DeployedBy, request.Notes, normalizedData);
        if (context.Operation == "update" && request.DeploymentData is not null)
        {
            return Fail(StatusCodes.Status400BadRequest, "Deployment initialization data can only be supplied for a new contract deployment.");
        }
        var challenge = new DeploymentAuthorizationChallengeRequest(
            request.ArtifactId,
            request.Network,
            request.DeployedBy,
            request.Notes,
            signature.Origin,
            signature.Audience,
            signature.IssuedAtUtc,
            signature.Nonce,
            request.DeploymentData);
        var message = BuildStartMessage(context, challenge);
        if (!string.Equals(signature.Message, message, StringComparison.Ordinal))
        {
            return Fail(StatusCodes.Status401Unauthorized, "Wallet signature message does not match the deployment authorization.");
        }
        var verification = signatureVerifier.Verify(signature, message);
        if (!verification.IsValid)
        {
            return Fail(StatusCodes.Status401Unauthorized, verification.Error);
        }

        var snapshot = new DeploymentAuthorizationSnapshot
        {
            InitiatorWalletAddress = verification.Address ?? signature.Address.Trim(),
            InitiatorScriptHash = NormalizeScriptHash(verification.ScriptHash ?? signature.ScriptHash),
            ExpectedDeploymentRevision = context.ExpectedDeploymentRevision,
            ExpectedTargetContractHash = context.ExpectedTargetContractHash,
            ArtifactNefSha256 = Sha256Hex(context.Nef),
            ArtifactManifestSha256 = Sha256Hex(context.ManifestJson),
            AuthorizationMessageHash = Sha256Hex(message),
            AuthorizedAtUtc = DateTime.UtcNow,
            AuthorizationSchemaVersion = request.DeploymentData is null ? 1 : 2,
            DeploymentData = request.DeploymentData is null ? null : normalizedData.Value,
            DeploymentDataSha256 = request.DeploymentData is null ? null : normalizedData.Sha256,
            DeploymentDataFormatVersion = request.DeploymentData is null ? null : normalizedData.FormatVersion
        };
        return new DeploymentAuthorizationValidationResult(
            true,
            StatusCodes.Status204NoContent,
            string.Empty,
            context,
            snapshot,
            permission.IsOwner);
    }

    public static string CreateAttemptCapability()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string HashAttemptCapability(string capability)
        => Sha256Hex(capability.Trim());

    public static bool IsValidCapability(DeploymentDocument attempt, string? capability)
    {
        return attempt.AttemptCapabilityExpiresAtUtc > DateTime.UtcNow
            && MatchesCapabilityHash(attempt, capability);
    }

    public static bool MatchesCapabilityHash(DeploymentDocument attempt, string? capability)
    {
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(attempt.AttemptCapabilityHash))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(HashAttemptCapability(capability)),
            Convert.FromHexString(attempt.AttemptCapabilityHash));
    }

    private static DeploymentAuthorizationValidationResult Fail(int statusCode, string error)
        => new(false, statusCode, error, null, null, false);

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
    string Notes,
    NormalizedDeploymentData DeploymentData);

public sealed record DeploymentAuthorizationValidationResult(
    bool IsValid,
    int StatusCode,
    string Error,
    DeploymentAuthorizationContext? Context,
    DeploymentAuthorizationSnapshot? Snapshot,
    bool IsOwner);
