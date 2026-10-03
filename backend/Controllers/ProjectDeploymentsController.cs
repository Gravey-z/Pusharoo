using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/deployments")]
public sealed class ProjectDeploymentsController(
    DeploymentService deploymentService,
    DeploymentWorkflowService deploymentWorkflow,
    NeoDeploymentVerificationService deploymentVerification,
    ArtifactService artifactService,
    DeploymentAuthorizationService deploymentAuthorization,
    DeploymentDataService deploymentData,
    ProjectAuthorizationService projectAuthorization,
    NeoWalletSignatureVerifier signatureVerifier,
    WalletSignatureRequestValidator signatureRequestValidator,
    SignatureNonceService nonceService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DeploymentResponse>> CreateAsync(
        string projectId,
        CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        return Conflict(new { error = "Direct deployment recording is disabled. Start an authorized deployment attempt instead." });
    }

    [HttpPost("authorization-challenge")]
    public async Task<ActionResult<DeploymentAuthorizationChallengeResponse>> CreateAuthorizationChallengeAsync(
        string projectId,
        DeploymentAuthorizationChallengeRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasValidAuthorizationRequest(request.ArtifactId, request.Network, request.DeployedBy, request.Notes)
            || string.IsNullOrWhiteSpace(request.Origin) || string.IsNullOrWhiteSpace(request.Audience)
            || string.IsNullOrWhiteSpace(request.IssuedAtUtc) || string.IsNullOrWhiteSpace(request.Nonce))
        {
            return BadRequest(new { error = "Deployment authorization challenge fields are invalid." });
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }

        var permission = projectAuthorization.CanDeployToNetwork(projectResult.Value, request.DeployedBy, request.Network);
        if (!permission.IsAllowed) return StatusCode(permission.StatusCode, new { error = permission.Error });
        NormalizedDeploymentData normalizedData;
        try
        {
            normalizedData = deploymentData.Normalize(request.DeploymentData);
        }
        catch (DeploymentDataValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var context = deploymentAuthorization.CreateContext(
            projectResult.Value,
            artifact,
            deployments,
            request.Network,
            request.DeployedBy,
            request.Notes,
            normalizedData);
        if (context.Operation == "update" && request.DeploymentData is not null)
        {
            return BadRequest(new { error = "Deployment initialization data can only be supplied for a new contract deployment." });
        }
        var message = deploymentAuthorization.BuildStartMessage(context, request);
        return Ok(new DeploymentAuthorizationChallengeResponse(
            message,
            context.Operation,
            context.ExpectedTargetContractHash,
            context.ExpectedDeploymentRevision,
            normalizedData.Value,
            normalizedData.Sha256,
            normalizedData.FormatVersion));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeploymentResponse>>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!project.IsSuccess) return WorkflowFailure(project);

        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var response = deployments.Select(deployment => deployment.ToResponse()).ToArray();

        return Ok(response);
    }

    [HttpPost("attempts")]
    public async Task<ActionResult<DeploymentResponse>> StartAttemptAsync(
        string projectId,
        StartDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasValidAuthorizationRequest(request.ArtifactId, request.Network, request.DeployedBy, request.Notes))
        {
            return BadRequest(new { error = "Artifact, network, and wallet address are required to start a deployment." });
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }
        var existingDeployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var authorization = deploymentAuthorization.ValidateStart(projectResult.Value, artifact, existingDeployments, request);
        if (!authorization.IsValid)
        {
            return StatusCode(authorization.StatusCode, new { error = authorization.Error });
        }
        if (!await nonceService.TryConsumeAsync(request.Authorization!, cancellationToken))
        {
            return Conflict(new { error = "This deployment authorization signature has already been used." });
        }

        var attemptCapability = DeploymentAuthorizationService.CreateAttemptCapability();
        try
        {
            var attempt = await deploymentService.StartAttemptAsync(
                projectId,
                artifact,
                request,
                authorization.Context!.Operation,
                authorization.Snapshot!,
                attemptCapability,
                cancellationToken);
            return Created(
                $"/api/projects/{projectId}/deployments/{attempt.Id}",
                attempt.ToResponse() with { AttemptCapability = attemptCapability });
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(new { error = "Another deployment attempt is already active for this project network." });
        }
    }

    [HttpPost("{deploymentId}/submitted")]
    public async Task<ActionResult<DeploymentResponse>> MarkSubmittedAsync(
        string projectId,
        string deploymentId,
        SubmitDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TransactionId))
        {
            return BadRequest(new { error = "Transaction ID is required." });
        }

        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability, requireCurrentAccess: true, cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var attempt = context.Value.Attempt;
        var snapshotError = await ValidateAttemptSnapshotAsync(context.Value.Project, attempt, cancellationToken);
        if (snapshotError is not null)
        {
            return Conflict(new { error = snapshotError });
        }

        if (attempt.Status == "submitted" || attempt.Status == "confirmed")
        {
            return Ok(attempt.ToResponse());
        }

        var existing = await deploymentService.GetByTransactionIdAsync(request.TransactionId.Trim(), cancellationToken);
        if (existing is not null && existing.Id != attempt.Id)
        {
            return Conflict(new { error = "The transaction ID is already recorded for another deployment." });
        }

        var nextAttemptCapability = DeploymentAuthorizationService.CreateAttemptCapability();
        var updated = await deploymentService.MarkSubmittedAsync(attempt, request.TransactionId, nextAttemptCapability, cancellationToken);
        return Ok(updated.ToResponse() with { AttemptCapability = nextAttemptCapability });
    }

    [HttpPost("{deploymentId}/confirm")]
    public async Task<ActionResult<DeploymentResponse>> ConfirmAttemptAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability,
            requireCurrentAccess: false, cancellationToken, allowExpiredFailedConfirmation: true);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var project = context.Value.Project;
        var attempt = context.Value.Attempt;

        if (attempt.Status == "confirmed")
        {
            return Ok(attempt.ToResponse());
        }

        if (string.IsNullOrWhiteSpace(attempt.TransactionId))
        {
            return BadRequest(new { error = "The wallet has not submitted a transaction for this deployment." });
        }

        attempt = await deploymentService.MarkConfirmingAsync(attempt, cancellationToken);

        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var verification = await deploymentVerification.RecoverAsync(
            project,
            deployments.Where(deployment => deployment.Id != attempt.Id).ToArray(),
            attempt,
            cancellationToken);
        if (!verification.IsValid || string.IsNullOrWhiteSpace(verification.ContractHash))
        {
            var isFinalFailure = verification.Error.StartsWith("Deployment transaction finished", StringComparison.Ordinal)
                || verification.Error.Contains("was not signed", StringComparison.Ordinal)
                || verification.Error.Contains("does not contain the expected", StringComparison.Ordinal)
                || verification.Error.Contains("did not include a contract hash", StringComparison.Ordinal)
                || verification.Error.StartsWith("Pusharoo could not verify the submitted invocation", StringComparison.Ordinal);
            if (isFinalFailure)
            {
                var failed = await deploymentService.MarkFailedAsync(attempt, "confirmation", verification.Error, cancellationToken);
                return BadRequest(new { error = failed.FailureReason, deployment = failed.ToResponse() });
            }

            var submitted = attempt with { Status = "submitted", FailureStage = "confirmation", FailureReason = verification.Error, UpdatedAt = DateTime.UtcNow };
            await deploymentService.UpdateAsync(submitted, cancellationToken);
            return Conflict(new { error = verification.Error, deployment = submitted.ToResponse() });
        }

        if (string.Equals(attempt.Operation, "update", StringComparison.Ordinal)
            && !HashesMatch(verification.ContractHash, attempt.AuthorizationSnapshot?.ExpectedTargetContractHash))
        {
            var failed = await deploymentService.MarkFailedAsync(
                attempt,
                "confirmation",
                "Deployment update returned a contract hash other than the authorized target.",
                cancellationToken);
            return BadRequest(new { error = failed.FailureReason, deployment = failed.ToResponse() });
        }

        var confirmed = await deploymentService.MarkConfirmedAsync(attempt, verification.ContractHash, cancellationToken);
        return Ok(confirmed.ToResponse());
    }

    [HttpPost("{deploymentId}/failed")]
    public async Task<ActionResult<DeploymentResponse>> MarkFailedAsync(
        string projectId,
        string deploymentId,
        FailDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability, requireCurrentAccess: true, cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var attempt = context.Value.Attempt;

        if (attempt.Status == "confirmed")
        {
            return Conflict(new { error = "A confirmed deployment cannot be marked failed." });
        }

        var stage = request.Stage is "preparing" or "wallet" or "confirmation" or "record"
            ? request.Stage
            : "record";
        var failed = await deploymentService.MarkFailedAsync(attempt, stage, request.Reason, cancellationToken);
        return Ok(failed.ToResponse());
    }

    [HttpPost("recover")]
    public async Task<ActionResult<DeploymentResponse>> RecoverAsync(
        string projectId,
        RecoverDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Network is not ("neo3:testnet" or "neo3:mainnet")
            || string.IsNullOrWhiteSpace(request.TransactionId)
            || request.TransactionId.Length != 66
            || !request.TransactionId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !request.TransactionId[2..].All(Uri.IsHexDigit))
        {
            return BadRequest(new { error = "Select a Neo N3 network and enter a 0x-prefixed 64-character transaction hash." });
        }

        var signature = request.Authorization;
        if (signature is null || string.IsNullOrWhiteSpace(request.DeployedBy)
            || !string.Equals(signature.Address, request.DeployedBy.Trim(), StringComparison.Ordinal)
            || !string.Equals(signature.Network, request.Network, StringComparison.Ordinal))
        {
            return Unauthorized(new { error = "Connect and sign with the deployment wallet on the selected network." });
        }
        var signatureError = signatureRequestValidator.Validate(signature);
        if (signatureError is not null) return Unauthorized(new { error = signatureError });

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var project = projectResult.Value;
        var permission = projectAuthorization.CanDeployToNetwork(project, signature.Address, request.Network);
        if (!permission.IsAllowed) return StatusCode(permission.StatusCode, new { error = permission.Error });

        var transactionId = request.TransactionId.ToLowerInvariant();
        var message = BuildRecoveryMessage(projectId, request.Network, transactionId, request.DeployedBy.Trim(), signature);
        if (!string.Equals(signature.Message, message, StringComparison.Ordinal))
        {
            return Unauthorized(new { error = "Wallet signature message does not match this deployment recovery." });
        }
        var signed = signatureVerifier.Verify(signature, message);
        if (!signed.IsValid || string.IsNullOrWhiteSpace(signed.ScriptHash))
        {
            return Unauthorized(new { error = signed.Error });
        }

        var existing = await deploymentService.GetByTransactionIdAsync(transactionId, cancellationToken);
        if (existing is not null && (existing.ProjectId != projectId
            || existing.Network != request.Network || existing.DeployedBy != signed.Address))
        {
            return Conflict(new { error = "This transaction is already recorded for a different deployment." });
        }
        if (existing?.Status == "confirmed") return Ok(existing.ToResponse());
        if (existing is not null && (existing.AuthorizationSnapshot is null
            || !HashesMatch(existing.AuthorizationSnapshot.InitiatorScriptHash, signed.ScriptHash)))
        {
            return Conflict(new { error = "The recorded attempt was authorized by a different wallet." });
        }

        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var confirmedOnNetwork = deployments.Any(item => item.Network == request.Network
            && item.Status == "confirmed" && !string.IsNullOrWhiteSpace(item.ContractHash));
        if (existing is null && confirmedOnNetwork)
        {
            return Conflict(new { error = "This project already has a contract on that network. Recover its recorded attempt or update it from Pusharoo." });
        }
        var active = existing is null ? deployments.FirstOrDefault(item => item.Network == request.Network
            && item.Status is "preparing" or "awaiting_wallet" or "submitted" or "confirming") : null;
        if (active is not null && (active.DeployedBy != signed.Address || !string.IsNullOrWhiteSpace(active.TransactionId)))
        {
            return Conflict(new { error = "Another deployment attempt is active on this network. Resolve it before recovering a different transaction." });
        }

        var inspection = existing is null
            ? await deploymentVerification.InspectRecoveredDeployAsync(project, request.Network, transactionId, signed.ScriptHash, cancellationToken)
            : await deploymentVerification.RecoverAsync(project,
                deployments.Where(item => item.Id != existing.Id).ToArray(), existing, cancellationToken);
        if (!inspection.IsValid || string.IsNullOrWhiteSpace(inspection.ContractHash))
        {
            return Conflict(new { error = inspection.Error });
        }
        if (existing?.Operation == "update"
            && !HashesMatch(inspection.ContractHash, existing.AuthorizationSnapshot?.ExpectedTargetContractHash))
        {
            return Conflict(new { error = "The update transaction does not target the contract authorized by its original attempt." });
        }
        if (!await nonceService.TryConsumeAsync(signature, cancellationToken))
        {
            return Conflict(new { error = "This recovery signature has already been used. Sign again to retry." });
        }

        if (existing is not null)
        {
            var confirmed = await deploymentService.MarkConfirmedAsync(existing, inspection.ContractHash, cancellationToken);
            return Ok(confirmed.ToResponse());
        }

        try
        {
            var recovered = await deploymentService.CreateRecoveredAsync(projectId, request.Network,
                transactionId, signed.Address!, inspection.ContractHash, cancellationToken);
            if (active is not null)
            {
                await deploymentService.MarkFailedAsync(active, "record", "Superseded by a recovered deployment transaction.", cancellationToken);
            }
            return Created($"/api/projects/{projectId}/deployments/{recovered.Id}", recovered.ToResponse());
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(new { error = "This transaction is already recorded. Refresh the deployments list." });
        }
    }

    private static string BuildRecoveryMessage(string projectId, string network, string transactionId,
        string deployedBy, WalletSignatureRequest signature)
        => string.Join('\n',
            "Pusharoo deployment recovery",
            "Schema: pusharoo.deployment.recovery.v1",
            "Action: deployment.recover",
            $"Project ID: {projectId}",
            $"Network: {network}",
            $"Transaction ID: {transactionId}",
            $"Wallet: {deployedBy}",
            $"Audience: {signature.Audience.Trim()}",
            $"Origin: {signature.Origin.Trim()}",
            $"Issued at UTC: {signature.IssuedAtUtc.Trim()}",
            $"Nonce: {signature.Nonce.Trim()}");

    private async Task<DeploymentWorkflowResult<ProjectDeploymentAttemptContext>> LoadCapabilityAttemptAsync(
        string projectId,
        string deploymentId,
        string? attemptCapability,
        bool requireCurrentAccess,
        CancellationToken cancellationToken,
        bool allowExpiredFailedConfirmation = false)
    {
        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null)
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Failure(projectResult);
        }
        var attempt = await deploymentService.GetByIdAsync(deploymentId, cancellationToken);
        if (attempt is null || attempt.ProjectId != projectId)
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.NotFound("Deployment attempt was not found.");
        }
        var canRetryFailedConfirmation = allowExpiredFailedConfirmation
            && !string.IsNullOrWhiteSpace(attempt.TransactionId)
            && attempt.Status == "failed"
            && attempt.FailureStage == "confirmation"
            && attempt.FailureReason?.StartsWith("Pusharoo could not verify the submitted invocation:", StringComparison.Ordinal) == true
            && DeploymentAuthorizationService.MatchesCapabilityHash(attempt, attemptCapability);
        if (!DeploymentAuthorizationService.IsValidCapability(attempt, attemptCapability) && !canRetryFailedConfirmation)
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden("A valid, unexpired deployment attempt capability is required. Start a fresh signed attempt to continue.");
        }
        var snapshot = attempt.AuthorizationSnapshot;
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.InitiatorWalletAddress))
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden("Deployment attempt authorization is missing. Start a fresh signed attempt.");
        }
        if (requireCurrentAccess)
        {
            var access = projectAuthorization.CanDeployToNetwork(projectResult.Value, snapshot.InitiatorWalletAddress, attempt.Network);
            if (!access.IsAllowed)
            {
                return new DeploymentWorkflowResult<ProjectDeploymentAttemptContext>(null, access.StatusCode, access.Error);
            }
        }

        return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Success(
            new ProjectDeploymentAttemptContext(projectResult.Value, attempt));
    }

    private async Task<string?> ValidateAttemptSnapshotAsync(
        ProjectDocument project,
        DeploymentDocument attempt,
        CancellationToken cancellationToken)
    {
        var snapshot = attempt.AuthorizationSnapshot;
        if (snapshot is null)
        {
            return "Deployment attempt authorization is missing. Start a fresh signed attempt.";
        }
        var authorizationSchemaVersion = snapshot.AuthorizationSchemaVersion ?? 1;
        if (authorizationSchemaVersion is < 1 or > 2)
        {
            return "The deployment attempt uses an unsupported authorization format.";
        }
        if (authorizationSchemaVersion == 2)
        {
            if (snapshot.DeploymentData is null || string.IsNullOrWhiteSpace(snapshot.DeploymentDataSha256)
                || string.IsNullOrWhiteSpace(snapshot.DeploymentDataFormatVersion))
            {
                return "The authorized deployment data snapshot is incomplete. Start a fresh signed attempt.";
            }
            try
            {
                var normalizedData = deploymentData.Normalize(snapshot.DeploymentData, snapshot.DeploymentDataFormatVersion);
                if (!string.Equals(normalizedData.Sha256, snapshot.DeploymentDataSha256, StringComparison.Ordinal)
                    || !string.Equals(normalizedData.FormatVersion, snapshot.DeploymentDataFormatVersion, StringComparison.Ordinal))
                {
                    return "The authorized deployment data snapshot failed its integrity check.";
                }
            }
            catch (DeploymentDataValidationException)
            {
                return "The authorized deployment data snapshot is invalid.";
            }
        }
        var artifact = await artifactService.GetByIdAsync(attempt.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != project.Id)
        {
            return "The authorized deployment artifact is no longer available.";
        }
        var deployments = await deploymentService.GetByProjectIdAsync(project.Id, cancellationToken);
        var current = deploymentAuthorization.CreateContext(
            project,
            artifact,
            deployments.Where(item => item.Id != attempt.Id).ToArray(),
            attempt.Network,
            attempt.DeployedBy,
            attempt.Notes);
        if (!string.Equals(current.Operation, attempt.Operation, StringComparison.Ordinal)
            || current.ExpectedDeploymentRevision != snapshot.ExpectedDeploymentRevision
            || !string.Equals(current.ExpectedTargetContractHash, snapshot.ExpectedTargetContractHash, StringComparison.OrdinalIgnoreCase))
        {
            return "The deployment target or revision changed while this attempt was open. Review and sign a fresh attempt.";
        }

        return null;
    }

    private static bool HasValidAuthorizationRequest(string? artifactId, string? network, string? deployedBy, string? notes)
    {
        return !string.IsNullOrWhiteSpace(artifactId) && artifactId.Trim().Length <= 64
            && !string.IsNullOrWhiteSpace(network) && network.Trim().Length <= 64
            && !string.IsNullOrWhiteSpace(deployedBy) && deployedBy.Trim().Length <= 128
            && (notes?.Length ?? 0) <= 2_000;
    }

    private static bool HashesMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.Ordinal);
    }

    private static string NormalizeHash(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..].ToLowerInvariant()
            : normalized.ToLowerInvariant();
    }

    private ActionResult WorkflowFailure<T>(DeploymentWorkflowResult<T> result)
        => StatusCode(result.StatusCode, new { error = result.Error });
}
