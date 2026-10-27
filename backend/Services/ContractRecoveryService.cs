using backend.Models;
using MongoDB.Driver;
using static backend.Services.DeploymentAttemptValidator;

namespace backend.Services;

public sealed class ContractRecoveryService(
    DeploymentService deployments,
    ProjectContractLoader loader,
    NeoDeploymentVerificationService verification,
    ProjectAuthorizationService authorization,
    DeploymentAttemptValidator validator
)
{
    public async Task<DeploymentWorkflowResult<DeploymentResponse>> RecoverAsync(
        string projectId,
        string contractId,
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
            return DeploymentWorkflowResult<DeploymentResponse>.BadRequest(
                "Select a Neo N3 network and enter a 0x-prefixed 64-character transaction hash."
            );

        var context = await loader.LoadAsync(projectId, contractId, cancellationToken);
        var transactionId = request.TransactionId.ToLowerInvariant();
        var recorded = await deployments.GetByTransactionIdAsync(transactionId, cancellationToken);
        if (
            recorded is not null
            && (
                recorded.ProjectId != projectId
                || recorded.ContractId != contractId
                || recorded.Network != request.Network
                || recorded.DeployedBy != actor.Address
            )
        )
            return Conflict(
                "This transaction is already recorded for a different deployment or contract."
            );

        if (recorded is { Status: "failed", ChainFailureVerified: true })
            return Conflict(
                "This transaction has a verified failure for its recorded attempt. Start a freshly reviewed release instead."
            );

        if (recorded is not null && DeploymentRecoveryPolicy.IsConfirmed(recorded))
            return DeploymentWorkflowResult<DeploymentResponse>.Success(recorded.ToResponse());

        var history = await deployments.GetForScopeAsync(projectId, contractId, cancellationToken);
        var active = history
            .Where(item =>
                item.Network == request.Network && DeploymentRecoveryPolicy.IsUnresolved(item)
            )
            .ToArray();
        if (
            active.Length > 1
            || (recorded is not null && active.Any(item => item.Id != recorded.Id))
        )
            return Conflict(
                "Another unresolved attempt exists for this contract and network. Reconcile it before recovering a different transaction."
            );

        var attempt = recorded ?? active.SingleOrDefault();
        if (
            attempt is not null
            && (
                !MatchesInitiator(attempt, actor)
                || (
                    attempt.TransactionId is not null
                    && !HashesMatch(attempt.TransactionId, transactionId)
                )
            )
        )
            return Conflict("The recorded attempt belongs to another wallet or transaction.");

        NeoDeploymentInspectionResult? unrecordedInspection = null;
        if (attempt is not null)
        {
            var error = await validator.ValidateSnapshotAsync(
                context.Project,
                attempt,
                cancellationToken,
                requireCurrentTarget: false
            );
            if (error is not null)
                return Conflict(error);
        }
        else
        {
            var access = authorization.CanDeployToNetwork(
                context.Project,
                actor.Address,
                request.Network
            );
            if (!access.IsAllowed)
                return DeploymentWorkflowResult<DeploymentResponse>.Rejected(
                    access.StatusCode,
                    access.Error
                );
            var signer = await verification.VerifyRecoverySignerAsync(
                request.Network,
                transactionId,
                actor.ScriptHash,
                cancellationToken
            );
            if (!signer.IsValid)
                return Conflict(signer.Error);

            unrecordedInspection = await verification.InspectRecoveredDeployAsync(
                context.Project,
                request.Network,
                transactionId,
                actor.ScriptHash,
                cancellationToken
            );
            if (unrecordedInspection.IsValid && unrecordedInspection.ContractHash is not null)
            {
                var proposed = new DeploymentDocument
                {
                    ProjectId = projectId,
                    ContractId = contractId,
                    Network = request.Network,
                };
                var lineageError = DeploymentRecoveryPolicy.ValidateLineage(
                    history,
                    proposed,
                    unrecordedInspection.ContractHash
                );
                if (lineageError is not null)
                    return Conflict(lineageError);
            }
            else if (
                !unrecordedInspection.Error.StartsWith(
                    "Could not verify deployment against Neo RPC:",
                    StringComparison.Ordinal
                )
                && !unrecordedInspection.Error.StartsWith(
                    "Neo RPC returned an unexpected",
                    StringComparison.Ordinal
                )
            )
                return Conflict(unrecordedInspection.Error);
        }

        var created = attempt is null;
        string? capability = null;
        try
        {
            if (attempt is null)
            {
                capability = DeploymentAuthorizationService.CreateAttemptCapability();
                attempt = await deployments.CreateRecoveryPendingAsync(
                    projectId,
                    contractId,
                    request with
                    {
                        TransactionId = transactionId,
                    },
                    actor,
                    capability,
                    cancellationToken
                );
            }
            else if (attempt.TransactionId is null || attempt.ActiveAttemptKey is null)
            {
                attempt = await deployments.AttachRecoveryTransactionAsync(
                    attempt,
                    transactionId,
                    cancellationToken
                );
            }
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(
                "This transaction or active attempt is already recorded. Reload its stored contract before retrying."
            );
        }

        var inspection =
            unrecordedInspection
            ?? await verification.RecoverAsync(
                context.Project,
                history.Where(item => item.Id != attempt.Id).ToArray(),
                attempt,
                cancellationToken
            );
        var failure =
            !inspection.IsValid || string.IsNullOrWhiteSpace(inspection.ContractHash)
                ? inspection.Error
                : DeploymentRecoveryPolicy.ValidateLineage(
                    history,
                    attempt,
                    inspection.ContractHash
                );
        if (
            failure is null
            && attempt.Operation == "update"
            && !HashesMatch(
                inspection.ContractHash,
                attempt.AuthorizationSnapshot?.ExpectedTargetContractHash
            )
        )
            failure =
                "The update transaction does not target the contract authorized by its original attempt.";

        if (failure is not null)
        {
            var failed = await deployments.MarkFailedAsync(
                attempt,
                "record",
                failure,
                cancellationToken
            );
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                failure,
                failed.ToResponse() with
                {
                    AttemptCapability = capability,
                }
            );
        }

        var confirmed = await deployments.MarkConfirmedAsync(
            attempt,
            inspection.ContractHash!,
            cancellationToken
        );
        return created
            ? DeploymentWorkflowResult<DeploymentResponse>.Created(confirmed.ToResponse())
            : DeploymentWorkflowResult<DeploymentResponse>.Success(confirmed.ToResponse());
    }

    private static DeploymentWorkflowResult<DeploymentResponse> Conflict(string error) =>
        DeploymentWorkflowResult<DeploymentResponse>.Conflict(error);
}
