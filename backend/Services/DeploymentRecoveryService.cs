using backend.Models;
using MongoDB.Driver;
using static backend.Services.DeploymentAttemptValidator;

namespace backend.Services;

public sealed class DeploymentRecoveryService(
    DeploymentService deploymentService,
    DeploymentWorkflowService deploymentWorkflow,
    NeoDeploymentVerificationService deploymentVerification,
    ProjectAuthorizationService projectAuthorization
)
{
    public async Task<DeploymentWorkflowResult<DeploymentResponse>> RecoverAsync(
        string projectId,
        RecoverDeploymentRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        if (
            request.Network is not ("neo3:testnet" or "neo3:mainnet")
            || string.IsNullOrWhiteSpace(request.TransactionId)
            || request.TransactionId.Length != 66
            || !request.TransactionId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !request.TransactionId[2..].All(Uri.IsHexDigit)
        )
        {
            return DeploymentWorkflowResult<DeploymentResponse>.BadRequest(
                "Select a Neo N3 network and enter a 0x-prefixed 64-character transaction hash."
            );
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);

        if (!projectResult.IsSuccess || projectResult.Value is null)
            return DeploymentWorkflowResult<DeploymentResponse>.Failure(projectResult);

        var project = projectResult.Value;
        var transactionId = request.TransactionId.ToLowerInvariant();
        var existing = await deploymentService.GetByTransactionIdAsync(
            transactionId,
            cancellationToken
        );

        if (
            existing is not null
            && (
                existing.ProjectId != projectId
                || existing.Network != request.Network
                || existing.DeployedBy != actor.Address
            )
        )
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This transaction is already recorded for a different deployment."
            );
        }

        if (existing?.Status == "confirmed")
            return DeploymentWorkflowResult<DeploymentResponse>.Success(existing.ToResponse());

        if (
            existing is not null
            && (existing.AuthorizationSnapshot is null || !MatchesInitiator(existing, actor))
        )
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "The recorded attempt was authorized by a different wallet."
            );
        }

        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var active = existing is null
            ? deployments.FirstOrDefault(item =>
                item.Network == request.Network
                && item.Status is "preparing" or "awaiting_wallet" or "submitted" or "confirming"
            )
            : null;

        if (
            active is not null
            && (
                !MatchesInitiator(active, actor) || !string.IsNullOrWhiteSpace(active.TransactionId)
            )
        )
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "Another deployment attempt is active on this network. Resolve it before recovering a different transaction."
            );
        }

        if (existing is null && active is null)
        {
            var permission = projectAuthorization.CanDeployToNetwork(
                project,
                actor.Address,
                request.Network
            );

            if (!permission.IsAllowed)
                return DeploymentWorkflowResult<DeploymentResponse>.Rejected(
                    permission.StatusCode,
                    permission.Error
                );
        }

        var confirmedOnNetwork = deployments.Any(item =>
            item.Network == request.Network
            && item.Status == "confirmed"
            && !string.IsNullOrWhiteSpace(item.ContractHash)
        );

        if (existing is null && active is null && confirmedOnNetwork)
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This project already has a contract on that network. Recover its recorded attempt or update it from Pusharoo."
            );
        }

        var trackedAttempt = existing ?? active;
        var inspection = trackedAttempt is null
            ? await deploymentVerification.InspectRecoveredDeployAsync(
                project,
                request.Network,
                transactionId,
                actor.ScriptHash,
                cancellationToken
            )
            : await deploymentVerification.RecoverAsync(
                project,
                deployments.Where(item => item.Id != trackedAttempt.Id).ToArray(),
                trackedAttempt with
                {
                    TransactionId = transactionId,
                },
                cancellationToken
            );

        if (!inspection.IsValid || string.IsNullOrWhiteSpace(inspection.ContractHash))
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(inspection.Error);
        }

        if (
            trackedAttempt?.Operation == "update"
            && !HashesMatch(
                inspection.ContractHash,
                trackedAttempt.AuthorizationSnapshot?.ExpectedTargetContractHash
            )
        )
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "The update transaction does not target the contract authorized by its original attempt."
            );
        }

        if (trackedAttempt is not null)
        {
            var confirmed = await deploymentService.MarkConfirmedAsync(
                trackedAttempt with
                {
                    TransactionId = transactionId,
                },
                inspection.ContractHash,
                cancellationToken
            );

            return DeploymentWorkflowResult<DeploymentResponse>.Success(confirmed.ToResponse());
        }

        try
        {
            var recovered = await deploymentService.CreateRecoveredAsync(
                projectId,
                request.Network,
                transactionId,
                actor.Address,
                inspection.ContractHash,
                cancellationToken
            );

            return DeploymentWorkflowResult<DeploymentResponse>.Created(recovered.ToResponse());
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This transaction is already recorded. Refresh the deployments list."
            );
        }
    }
}
