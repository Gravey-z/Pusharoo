using System.Security.Cryptography;
using System.Text;
using backend.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed class ProjectCollaborationService(MongoDbContext db)
{
    public async Task<ProjectDocument?> AddAsync(
        string projectId,
        ProjectCollaboratorDocument collaborator,
        ProjectAccessAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        var filter = Builders<ProjectDocument>.Filter.And(
            Builders<ProjectDocument>.Filter.Eq(project => project.Id, projectId),
            Builders<ProjectDocument>.Filter.Not(Builders<ProjectDocument>.Filter.ElemMatch(
                project => project.Collaborators,
                item => item.WalletAddress == collaborator.WalletAddress)),
            BelowCollaboratorLimit());
        var update = Builders<ProjectDocument>.Update.Combine(
            Builders<ProjectDocument>.Update.Push(project => project.Collaborators, collaborator),
            Builders<ProjectDocument>.Update.Push(project => project.AccessAuditEvents, auditEvent));
        return await db.Projects.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<ProjectDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public async Task<ProjectDocument?> UpdateAsync(
        string projectId,
        ProjectCollaboratorDocument collaborator,
        long expectedRevision,
        ProjectAccessAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        var filter = Builders<ProjectDocument>.Filter.And(
            Builders<ProjectDocument>.Filter.Eq(project => project.Id, projectId),
            Builders<ProjectDocument>.Filter.ElemMatch(
                project => project.Collaborators,
                item => item.WalletAddress == collaborator.WalletAddress && item.GrantRevision == expectedRevision));
        var update = Builders<ProjectDocument>.Update.Combine(
            Builders<ProjectDocument>.Update.Set("collaborators.$.allowedNetworks", collaborator.AllowedNetworks),
            Builders<ProjectDocument>.Update.Set("collaborators.$.role", collaborator.Role),
            Builders<ProjectDocument>.Update.Set("collaborators.$.updatedAtUtc", collaborator.UpdatedAtUtc),
            Builders<ProjectDocument>.Update.Set("collaborators.$.updatedByWalletAddress", collaborator.UpdatedByWalletAddress),
            Builders<ProjectDocument>.Update.Set("collaborators.$.grantRevision", collaborator.GrantRevision),
            Builders<ProjectDocument>.Update.Push(project => project.AccessAuditEvents, auditEvent));
        return await db.Projects.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<ProjectDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public async Task<ProjectDocument?> RemoveAsync(
        string projectId,
        string walletAddress,
        long expectedRevision,
        ProjectAccessAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        var filter = Builders<ProjectDocument>.Filter.And(
            Builders<ProjectDocument>.Filter.Eq(project => project.Id, projectId),
            Builders<ProjectDocument>.Filter.ElemMatch(
                project => project.Collaborators,
                item => item.WalletAddress == walletAddress && item.GrantRevision == expectedRevision));
        var update = Builders<ProjectDocument>.Update.Combine(
            Builders<ProjectDocument>.Update.PullFilter(
                project => project.Collaborators,
                item => item.WalletAddress == walletAddress && item.GrantRevision == expectedRevision),
            Builders<ProjectDocument>.Update.Push(project => project.AccessAuditEvents, auditEvent));
        return await db.Projects.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<ProjectDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public static ProjectAccessAuditEvent CreateAuditEvent(
        string action,
        string actorWalletAddress,
        string targetWalletAddress,
        ProjectCollaboratorDocument? before,
        ProjectCollaboratorDocument? after,
        WalletSignatureRequest signature)
    {
        return new ProjectAccessAuditEvent
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
            Action = action,
            ActorWalletAddress = actorWalletAddress,
            TargetWalletAddress = targetWalletAddress,
            BeforeRole = before?.Role,
            AfterRole = after?.Role,
            BeforeNetworks = before?.AllowedNetworks.ToList() ?? [],
            AfterNetworks = after?.AllowedNetworks.ToList() ?? [],
            GrantRevision = after?.GrantRevision ?? before?.GrantRevision ?? 0,
            RequestNonceHash = HashNonce(signature),
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static FilterDefinition<ProjectDocument> BelowCollaboratorLimit()
    {
        return new BsonDocument("$expr", new BsonDocument("$lt", new BsonArray
        {
            new BsonDocument("$size", new BsonDocument("$ifNull", new BsonArray { "$collaborators", new BsonArray() })),
            ProjectCollaboratorInputValidator.MaxCollaborators
        }));
    }

    private static string HashNonce(WalletSignatureRequest signature)
    {
        var value = $"{signature.PublicKey.Trim()}:{signature.Nonce.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
