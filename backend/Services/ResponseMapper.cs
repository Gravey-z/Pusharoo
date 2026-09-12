using backend.Models;

namespace backend.Services;

public static class ResponseMapper
{
    public static ProjectResponse ToResponse(this ProjectDocument project)
    {
        return new ProjectResponse(
            project.Id,
            project.Name,
            project.Description,
            project.CreatedByWalletAddress,
            project.CreatorNetwork,
            project.CreatedAt);
    }

    public static ArtifactResponse ToResponse(this ArtifactDocument artifact)
    {
        return new ArtifactResponse(
            artifact.Id,
            artifact.ProjectId,
            artifact.Version,
            artifact.Notes,
            artifact.ContractName,
            artifact.NefFileName,
            artifact.NefSize,
            artifact.Manifest,
            artifact.Summary,
            artifact.Warnings,
            artifact.CreatedAt);
    }

    public static DeploymentResponse ToResponse(this DeploymentDocument deployment)
    {
        return new DeploymentResponse(
            deployment.Id,
            deployment.ProjectId,
            deployment.ArtifactId,
            deployment.Version,
            deployment.Network,
            deployment.ContractHash,
            deployment.TransactionId,
            deployment.DeployedBy,
            deployment.Notes,
            deployment.CreatedAt,
            deployment.Operation,
            string.IsNullOrWhiteSpace(deployment.Status) ? "confirmed" : deployment.Status,
            deployment.FailureStage,
            deployment.FailureReason,
            deployment.UpdatedAt == default ? deployment.CreatedAt : deployment.UpdatedAt);
    }

    public static ProjectCollaboratorResponse ToResponse(this ProjectCollaboratorDocument collaborator)
    {
        return new ProjectCollaboratorResponse(
            collaborator.WalletAddress,
            collaborator.ScriptHash,
            collaborator.Role,
            collaborator.AllowedNetworks,
            collaborator.GrantRevision,
            collaborator.AddedAtUtc,
            collaborator.AddedByWalletAddress,
            collaborator.UpdatedAtUtc,
            collaborator.UpdatedByWalletAddress);
    }

    public static ProjectAccessAuditResponse ToResponse(this ProjectAccessAuditEvent auditEvent)
    {
        return new ProjectAccessAuditResponse(
            auditEvent.Action,
            auditEvent.ActorWalletAddress,
            auditEvent.TargetWalletAddress,
            auditEvent.BeforeRole,
            auditEvent.AfterRole,
            auditEvent.BeforeNetworks,
            auditEvent.AfterNetworks,
            auditEvent.GrantRevision,
            auditEvent.CreatedAtUtc);
    }
}
