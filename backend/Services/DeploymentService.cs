using backend.Models;
using backend.Repositories;
using MongoDB.Bson;

namespace backend.Services;

public sealed class DeploymentService(IDeploymentRepository deployments)
{
    public async Task<DeploymentDocument> CreateAsync(
        string projectId,
        ArtifactDocument artifact,
        CreateDeploymentRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var deployment = new DeploymentDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ProjectId = projectId,
            ArtifactId = artifact.Id,
            Version = artifact.Version,
            Network = request.Network.Trim(),
            ContractHash = TrimToNull(request.ContractHash),
            TransactionId = TrimToNull(request.TransactionId),
            DeployedBy = request.DeployedBy.Trim(),
            Notes = TrimToNull(request.Notes),
            Operation = "deploy",
            Status = "confirmed",
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await deployments.InsertAsync(deployment, cancellationToken);

        return deployment;
    }

    public Task<DeploymentDocument?> GetByTransactionIdAsync(string transactionId, CancellationToken cancellationToken)
        => deployments.GetByTransactionIdAsync(transactionId, cancellationToken);

    public Task<DeploymentDocument?> GetByIdAsync(string deploymentId, CancellationToken cancellationToken)
        => deployments.GetByIdAsync(deploymentId, cancellationToken);

    public async Task<IReadOnlyList<DeploymentDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        return await deployments.GetByProjectIdAsync(projectId, cancellationToken);
    }

    public async Task<DeploymentDocument> StartAttemptAsync(
        string projectId,
        ArtifactDocument artifact,
        StartDeploymentAttemptRequest request,
        string operation,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var deployment = new DeploymentDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ProjectId = projectId,
            ArtifactId = artifact.Id,
            Version = artifact.Version,
            Network = request.Network.Trim(),
            DeployedBy = request.DeployedBy.Trim(),
            Notes = TrimToNull(request.Notes),
            Operation = operation,
            Status = "awaiting_wallet",
            ActiveAttemptKey = BuildActiveAttemptKey(projectId, request.Network),
            AuthorizationSnapshot = new DeploymentAuthorizationSnapshot
            {
                // Stage 3 replaces this compatibility snapshot with a verified
                // wallet-signature authorization before this endpoint can be used
                // by collaborators.
                InitiatorWalletAddress = request.DeployedBy.Trim(),
                ExpectedDeploymentRevision = 0
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        await deployments.InsertAsync(deployment, cancellationToken);
        return deployment;
    }

    public async Task<DeploymentDocument> MarkSubmittedAsync(
        DeploymentDocument deployment,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var updated = deployment with
        {
            TransactionId = transactionId.Trim(),
            Status = "submitted",
            FailureStage = null,
            FailureReason = null,
            UpdatedAt = DateTime.UtcNow
        };
        await deployments.ReplaceAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<DeploymentDocument> MarkConfirmedAsync(
        DeploymentDocument deployment,
        string contractHash,
        CancellationToken cancellationToken)
    {
        var updated = deployment with
        {
            ContractHash = contractHash.Trim(),
            Status = "confirmed",
            FailureStage = null,
            FailureReason = null,
            ActiveAttemptKey = null,
            UpdatedAt = DateTime.UtcNow
        };
        await deployments.ReplaceAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<DeploymentDocument> MarkConfirmingAsync(
        DeploymentDocument deployment,
        CancellationToken cancellationToken)
    {
        var updated = deployment with
        {
            Status = "confirming",
            FailureStage = null,
            FailureReason = null,
            UpdatedAt = DateTime.UtcNow
        };
        await deployments.ReplaceAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<DeploymentDocument> MarkFailedAsync(
        DeploymentDocument deployment,
        string stage,
        string reason,
        CancellationToken cancellationToken)
    {
        var updated = deployment with
        {
            Status = stage == "record" ? "record_failed" : "failed",
            FailureStage = stage,
            FailureReason = TrimToNull(reason),
            ActiveAttemptKey = null,
            UpdatedAt = DateTime.UtcNow
        };
        await deployments.ReplaceAsync(updated, cancellationToken);
        return updated;
    }

    public Task UpdateAsync(DeploymentDocument deployment, CancellationToken cancellationToken)
        => deployments.ReplaceAsync(deployment, cancellationToken);

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string BuildActiveAttemptKey(string projectId, string network)
        => $"{projectId.Trim()}:{network.Trim()}";
}
