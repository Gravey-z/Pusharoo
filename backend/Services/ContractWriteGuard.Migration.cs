using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed partial class ContractWriteGuard
{
    public async Task EnsureMigrationAllowsWritesAsync(
        CancellationToken cancellationToken,
        bool allowReconciliation = false
    )
    {
        var state = await db
            .Projects.Database.GetCollection<BsonDocument>("contractMigrationState")
            .Find(new BsonDocument("_id", "multi-contract-v1"))
            .FirstOrDefaultAsync(cancellationToken);
        if (state is null)
            return;
        var status = state.GetValue("status", "").AsString;
        var owned = state.TryGetValue("leaseToken", out var token) && !token.IsBsonNull;
        if (!owned && (status == "ready" || (allowReconciliation && status == "blocked")))
            return;
        throw new ContractOperationException(
            503,
            "contract_migration_blocked",
            "Workspace migration is incomplete. Keep new writes disabled and finish reconciliation before continuing."
        );
    }
}
