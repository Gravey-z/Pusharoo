using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pusharoo.RetentionInventory;

public sealed class InventoryReader(IMongoDatabase database, TimeProvider clock)
{
    private const int MetadataLimit = 1000;
    private static readonly string[] FinancialCollections =
    [
        "relayPaymentIntents",
        "relayPayments",
        "relayPurchasedPeriods",
        "relayPeriodUpgrades",
        "relayBillingDecisions",
        "relayGrantProgress",
        "relayEntitlementHistory",
        "relayPaymentResolutions",
        "relayPaymentTransactions",
        "relayLegacyBillingSnapshots",
    ];
    private static readonly HashSet<string> GlobalCollections =
    [
        "eventCheckpoints",
        "relayBillingSchema",
        "walletLoginChallenges",
        "walletLoginSessions",
        "faucetClaims",
        "faucetIpClaims",
        "faucetDailyFeeBudgets",
    ];

    public async Task<InventoryReport> ReadAsync(
        int take = 100,
        string? afterProjectId = null,
        CancellationToken ct = default
    )
    {
        if (take is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be between 1 and 100.");
        var now = clock.GetUtcNow().UtcDateTime;
        var names = (
            await (await database.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct)
        ).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains("projects"))
            throw new InvalidOperationException(
                "The selected database has no projects collection."
            );
        var projects = database.GetCollection<BsonDocument>("projects");
        if (
            await projects
                .Find(
                    new BsonDocument(
                        "_id",
                        new BsonDocument("$not", new BsonDocument("$type", "string"))
                    )
                )
                .Project<BsonDocument>(Projection())
                .Limit(1)
                .AnyAsync(ct)
        )
            throw new InvalidOperationException(
                "Unsupported project identity; reconcile before inventory pagination."
            );
        var page = await projects
            .Find(
                afterProjectId is null
                    ? new BsonDocument()
                    : new BsonDocument("_id", new BsonDocument("$gt", afterProjectId))
            )
            .Project<BsonDocument>(
                Projection(
                    "createdAt",
                    "contractSchemaVersion",
                    "isDeleting",
                    "contractWriteToken",
                    "contractMigrationState"
                )
            )
            .Sort(new BsonDocument("_id", 1))
            .Limit(take + 1)
            .ToListAsync(ct);
        var migration = names.Contains("contractMigrationState")
            ? await database
                .GetCollection<BsonDocument>("contractMigrationState")
                .Find(new BsonDocument("_id", "multi-contract-v1"))
                .Project<BsonDocument>(Projection("status", "leaseToken"))
                .FirstOrDefaultAsync(ct)
            : null;
        var result = new List<ProjectInventory>();
        foreach (var project in page.Take(take))
            result.Add(await ReadProjectAsync(project, names, migration, now, ct));
        return new(now, result, page.Count > take ? result[^1].ProjectId : null);
    }

    private async Task<ProjectInventory> ReadProjectAsync(
        BsonDocument project,
        HashSet<string> names,
        BsonDocument? migration,
        DateTime now,
        CancellationToken ct
    )
    {
        var id = project["_id"].AsString;
        var blockers = new SortedSet<string>(StringComparer.Ordinal);
        var reconciliation = new SortedSet<string>(StringComparer.Ordinal);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var queried = new HashSet<string>(GlobalCollections, StringComparer.Ordinal)
        {
            "projects",
            "contractMigrationState",
        };
        var owner = new BsonDocument("projectId", id);
        var identity = new BsonDocument(
            "$or",
            new BsonArray
            {
                owner,
                new BsonDocument("_id", id),
                new BsonDocument("_id", new BsonRegularExpression("^" + Regex.Escape(id) + ":")),
            }
        );
        if (
            migration is not null
            && (
                migration.GetValue("status", "") != "ready"
                || !migration.GetValue("leaseToken", BsonNull.Value).IsBsonNull
            )
        )
            blockers.Add("migration_fence");

        var contracts = await Read(
            "contracts",
            owner,
            "testnetDeploymentWriteToken",
            "mainnetDeploymentWriteToken"
        );
        if (
            contracts.Any(doc =>
                !doc.GetValue("testnetDeploymentWriteToken", BsonNull.Value).IsBsonNull
                || !doc.GetValue("mainnetDeploymentWriteToken", BsonNull.Value).IsBsonNull
            )
        )
            blockers.Add("deployment_writer");
        var activityFilter = Or(owner, In("contractId", Ids(contracts)));
        var artifacts = await Read("contractArtifacts", activityFilter);
        var deployments = await Read(
            "deployments",
            Or(activityFilter, In("artifactId", Ids(artifacts)))
        );
        var activity = artifacts.Count > 0 || deployments.Count > 0;
        var historical = await Read(
            "contractMigrationProjects",
            new BsonDocument("_id", id),
            "status",
            "plan.records.kind"
        );
        foreach (var journal in historical)
        {
            if (journal.GetValue("status", "") != "ready")
                blockers.Add("project_migration_state");
            var records = Path(journal, "plan", "records");
            if (records.IsBsonArray && records.AsBsonArray.Count > 0)
                activity = true;
            else if (!records.IsBsonArray)
                reconciliation.Add("unknown_migration_history");
        }
        var relocations = await Read(
            "contractRelocations",
            Or(
                identity,
                new BsonDocument("sourceProjectId", id),
                new BsonDocument("destinationProjectId", id)
            )
        );
        activity |= relocations.Count > 0;

        var subscriptions = await Read("eventSubscriptions", owner);
        BlockIfAny("eventSubscriptions", subscriptions);
        var subscriptionFilter = In("subscriptionId", Ids(subscriptions));
        var deliveries = await Read("webhookDeliveries", Or(owner, subscriptionFilter));
        BlockIfAny("webhookDeliveries", deliveries);
        BlockIfAny(
            "webhookDeliveryAttempts",
            await Read("webhookDeliveryAttempts", Or(owner, In("deliveryId", Ids(deliveries))))
        );
        BlockIfAny("eventReplayJobs", await Read("eventReplayJobs", Or(owner, subscriptionFilter)));
        BlockIfAny(
            "eventReplayAdmissions",
            await Read(
                "eventReplayAdmissions",
                Or(
                    new BsonDocument("entries.job.projectId", id),
                    In("entries.job.subscriptionId", Ids(subscriptions))
                )
            )
        );

        var relatedIds = new HashSet<BsonValue>();
        foreach (var name in FinancialCollections)
        {
            var records = await Read(
                name,
                Or(
                    identity,
                    In("intentId", relatedIds),
                    In("decisionId", relatedIds),
                    In("paymentId", relatedIds),
                    In("_id", relatedIds)
                )
            );
            BlockIfAny(name, records);
            relatedIds.UnionWith(Ids(records));
        }
        var entitlements = await Read(
            "relayEntitlements",
            identity,
            "plan",
            "network",
            "packageId",
            "packageTerms",
            "purchasedPeriodId",
            "appliedPaymentAllocations",
            "eventsUsed",
            "paidUntil",
            "graceEndsAt",
            "lastPaymentId",
            "paymentGrants",
            "status",
            "maxEvents",
            "maxActiveSubscriptions"
        );
        if (entitlements.Any(doc => !IsUnusedFreeEntitlement(doc)))
            blockers.Add("relay_entitlement_not_unused_free");
        var allowances = await Read(
            "relayAllowancePeriods",
            identity,
            "eventsUsed",
            "chargedDeliveries"
        );
        if (
            allowances.Any(doc =>
                !IsFreePeriodId(doc["_id"], id)
                || !Zero(doc, "eventsUsed")
                || !EmptyArray(doc, "chargedDeliveries")
            )
        )
            blockers.Add("relay_usage_or_receipts");
        var ledgers = await Read("relayBillingLedgers", identity, "openIntent");
        if (ledgers.Any(doc => !doc.GetValue("openIntent", BsonNull.Value).IsBsonNull))
            blockers.Add("billing_reservation");
        var admissions = await Read(
            "relayProjectAdmissions",
            identity,
            "mainnet",
            "testnet",
            "slots",
            "transition",
            "mutation",
            "publication",
            "selections",
            "lastTransition",
            "attemptAdmissions"
        );
        if (admissions.Any(doc => !IsIdleFreeAdmission(doc)))
            blockers.Add("relay_admission_or_pending_work");

        foreach (var name in names.Except(queried).Order())
        {
            var records = await Read(name, identity);
            if (records.Count > 0)
                reconciliation.Add("unclassified_collection:" + name);
        }
        return InventoryClassifier.Classify(
            project,
            now,
            activity,
            reconciliation,
            blockers,
            counts
        );

        async Task<List<BsonDocument>> Read(
            string name,
            BsonDocument filter,
            params string[] fields
        )
        {
            queried.Add(name);
            if (!names.Contains(name))
                return [];
            var documents = await database
                .GetCollection<BsonDocument>(name)
                .Find(filter)
                .Project<BsonDocument>(Projection(fields))
                .Limit(MetadataLimit + 1)
                .ToListAsync(ct);
            counts[name] = documents.Count;
            if (documents.Count > MetadataLimit)
                reconciliation.Add("metadata_limit:" + name);
            return documents;
        }

        void BlockIfAny(string name, List<BsonDocument> records)
        {
            if (records.Count > 0)
                blockers.Add("retained_dependency:" + name);
        }
    }

    private static bool IsUnusedFreeEntitlement(BsonDocument document) =>
        document.GetValue("plan", "") == "free_beta"
        && (
            document.GetValue("network", "") == "neo3:testnet"
            || document.GetValue("network", "") == "neo3:mainnet"
                && document.GetValue("status", "") == "expired"
                && Zero(document, "maxEvents")
                && Zero(document, "maxActiveSubscriptions")
        )
        && (
            document.GetValue("packageId", BsonNull.Value).IsBsonNull
            || document["packageId"] == "free_beta"
        )
        && document.GetValue("packageTerms", BsonNull.Value).IsBsonNull
        && document.GetValue("purchasedPeriodId", BsonNull.Value).IsBsonNull
        && document.GetValue("paidUntil", BsonNull.Value).IsBsonNull
        && document.GetValue("graceEndsAt", BsonNull.Value).IsBsonNull
        && document.GetValue("lastPaymentId", BsonNull.Value).IsBsonNull
        && Zero(document, "eventsUsed")
        && EmptyArray(document, "appliedPaymentAllocations")
        && EmptyArray(document, "paymentGrants");

    private static bool IsIdleFreeAdmission(BsonDocument document)
    {
        if (
            !EmptyArray(document, "slots")
            || !EmptyArray(document, "selections")
            || !Zero(document, "attemptAdmissions")
            || new[] { "transition", "mutation", "publication", "lastTransition" }.Any(field =>
                !document.GetValue(field, BsonNull.Value).IsBsonNull
            )
        )
            return false;
        foreach (var field in new[] { "mainnet", "testnet" })
        {
            var value = document.GetValue(field, BsonNull.Value);
            if (value.IsBsonNull)
                continue;
            if (
                !value.IsBsonDocument
                || !Zero(value.AsBsonDocument, "eventsUsed")
                || !EmptyArray(value.AsBsonDocument, "receipts")
            )
                return false;
            var entitlement = Path(value.AsBsonDocument, "entitlement");
            if (
                !entitlement.IsBsonDocument
                || !IsUnusedFreeEntitlement(entitlement.AsBsonDocument)
                || !IsFreePeriodId(Path(value.AsBsonDocument, "periodId"), document["_id"].AsString)
            )
                return false;
        }
        return true;
    }

    private static bool Zero(BsonDocument doc, string field) =>
        !doc.Contains(field) || doc[field].IsNumeric && doc[field].ToDouble() == 0;

    private static bool EmptyArray(BsonDocument doc, string field) =>
        !doc.Contains(field) || doc[field].IsBsonArray && doc[field].AsBsonArray.Count == 0;

    private static BsonValue Path(BsonDocument doc, params string[] path)
    {
        BsonValue value = doc;
        foreach (var field in path)
        {
            if (!value.IsBsonDocument || !value.AsBsonDocument.TryGetValue(field, out value))
                return BsonNull.Value;
        }
        return value;
    }

    private static bool IsFreePeriodId(BsonValue value, string projectId) =>
        value.IsString
        && Regex.IsMatch(
            value.AsString,
            "^" + Regex.Escape(projectId) + @":neo3:(?:testnet|mainnet):-?\d+:free$"
        );

    private static BsonDocument Projection(params string[] fields) =>
        new(
            new[] { "_id" }
                .Concat(fields)
                .Distinct()
                .Select(field => new BsonElement(
                    field,
                    new[]
                    {
                        "transition",
                        "mutation",
                        "publication",
                        "openIntent",
                        "lastTransition",
                        "packageTerms",
                    }.Contains(field)
                        ? new BsonDocument(
                            "$cond",
                            new BsonArray
                            {
                                new BsonDocument(
                                    "$eq",
                                    new BsonArray
                                    {
                                        new BsonDocument(
                                            "$ifNull",
                                            new BsonArray { "$" + field, BsonNull.Value }
                                        ),
                                        BsonNull.Value,
                                    }
                                ),
                                BsonNull.Value,
                                true,
                            }
                        )
                        : (BsonValue)1
                ))
        );

    private static IEnumerable<BsonValue> Ids(IEnumerable<BsonDocument> documents) =>
        documents.Select(doc => doc["_id"]);

    private static BsonDocument In(string field, IEnumerable<BsonValue> ids) =>
        new(field, new BsonDocument("$in", new BsonArray(ids)));

    private static BsonDocument Or(params BsonDocument[] filters) =>
        new("$or", new BsonArray(filters));
}
