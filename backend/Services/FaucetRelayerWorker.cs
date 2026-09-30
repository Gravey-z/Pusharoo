using System.Globalization;
using System.Numerics;
using System.Text.Json;
using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Neo;
using Neo.Extensions;
using Neo.Network.P2P.Payloads;
using Neo.Network.RPC;
using Neo.SmartContract;
using Neo.Wallets;
using Utility = Neo.Network.RPC.Utility;

namespace backend.Services;

public sealed class FaucetRelayerWorker(
    MongoDbContext db,
    NeoRpcClient rpc,
    IConfiguration configuration,
    IOptions<NeoRpcOptions> neoOptions,
    IOptions<FaucetOptions> faucetOptions,
    IOptions<FaucetRelayerOptions> relayerOptions,
    ILogger<FaucetRelayerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private readonly FaucetOptions faucet = faucetOptions.Value;
    private readonly FaucetRelayerOptions relayer = relayerOptions.Value;
    private readonly NeoNetworkRpcOptions network = neoOptions.Value.Networks.GetValueOrDefault("testnet")
        ?? throw new InvalidOperationException("Testnet RPC is not configured for the faucet relayer.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!relayer.Enabled) return;
        if (string.IsNullOrWhiteSpace(relayer.PrivateKeyWif) || string.IsNullOrWhiteSpace(faucet.ContractHash))
            throw new InvalidOperationException("Faucet relayer requires a testnet contract hash and WIF private key.");
        if (relayer.MaximumTransactionFeeGas <= 0 || relayer.DailyFeeBudgetGas <= 0)
            throw new InvalidOperationException("Configure measured positive per-transaction and daily sponsored-fee limits before enabling the faucet relayer.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claim = await LeaseClaimAsync(stoppingToken);
                if (claim is not null)
                {
                    try
                    {
                        await ProcessAsync(claim, stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Faucet relayer failed request {RequestId}, attempt {Attempt}.", claim.Id, claim.Attempts);
                        if (claim.Attempts >= 5)
                        {
                            await SetStateAsync(claim.Id, "NeedsReview", "Relayer reached its retry limit. Review the on-chain transaction and fee wallet before retrying.", stoppingToken, release: false, transactionHash: claim.TransactionHash);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) { logger.LogError(exception, "Faucet relayer iteration failed; queued claims remain recoverable."); }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(relayer.PollIntervalSeconds, 1, 30)), stoppingToken);
        }
    }

    private async Task<FaucetClaimDocument?> LeaseClaimAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<FaucetClaimDocument>.Filter.Or(
            Builders<FaucetClaimDocument>.Filter.Eq(claim => claim.State, "Queued"),
            Builders<FaucetClaimDocument>.Filter.And(
                Builders<FaucetClaimDocument>.Filter.In(claim => claim.State, ["Processing", "Broadcasting", "Submitted"]),
                Builders<FaucetClaimDocument>.Filter.Or(
                    Builders<FaucetClaimDocument>.Filter.Eq(claim => claim.LeaseUntil, null),
                    Builders<FaucetClaimDocument>.Filter.Lt(claim => claim.LeaseUntil, now))));
        return await db.FaucetClaims.FindOneAndUpdateAsync(
            filter,
            Builders<FaucetClaimDocument>.Update
                .Set(claim => claim.State, "Processing")
                .Set(claim => claim.LeaseUntil, now.Add(Lease))
                .Set(claim => claim.UpdatedAt, now)
                .Inc(claim => claim.Attempts, 1),
            new FindOneAndUpdateOptions<FaucetClaimDocument>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<FaucetClaimDocument>.Sort.Ascending(claim => claim.CreatedAt)
            },
            cancellationToken);
    }

    private async Task ProcessAsync(FaucetClaimDocument claim, CancellationToken cancellationToken)
    {
        var key = Utility.GetKeyPair(relayer.PrivateKeyWif);
        var sender = Contract.CreateSignatureContract(key.PublicKey).ScriptHash;
        var availability = await GetVerifiedNetworkAsync(cancellationToken);
        if (!availability) throw new InvalidOperationException("Faucet relayer RPC failed the testnet network-magic check.");

        if (!string.IsNullOrWhiteSpace(claim.SignedTransaction))
        {
            await ReconcileExistingAsync(claim, cancellationToken);
            return;
        }

        var status = await rpc.SendAsync(network.Endpoint, "invokefunction", [faucet.ContractHash, "getStatus", Array.Empty<object>()], cancellationToken);
        var statusStack = status.GetProperty("stack");
        var configuredRelayer = FaucetRpcService.StackValue(statusStack[8]);
        if (!string.Equals(NormalizeHash(configuredRelayer), NormalizeHash(sender.ToString()), StringComparison.OrdinalIgnoreCase))
        {
            await SetStateAsync(claim.Id, "NeedsReview", "Configured signing key is not the contract relayer account.", cancellationToken, release: true);
            return;
        }

        var script = UInt160.Parse(faucet.ContractHash).MakeScript("claim", UInt160.Parse(claim.ScriptHash));
        var signers = new[] { new Signer { Account = sender, Scopes = WitnessScope.CalledByEntry } };
        var preflight = await rpc.SendAsync(network.Endpoint, "invokefunction",
            [faucet.ContractHash, "claim", new[] { new { type = "Hash160", value = claim.ScriptHash } },
                new[] { new { account = sender.ToString(), scopes = "CalledByEntry" } }], cancellationToken);
        if (!string.Equals(preflight.GetProperty("state").GetString(), "HALT", StringComparison.OrdinalIgnoreCase)
            || !HasExpectedClaim(preflight, claim.ScriptHash))
        {
            await SetStateAsync(claim.Id, "Failed", "Exact faucet call did not simulate to HALT with the expected recipient event.", cancellationToken, release: true);
            return;
        }
        var protocol = ProtocolSettings.Load(configuration.GetSection("FaucetRelayer:ProtocolSettings"));
        if (protocol.Network != faucet.NetworkMagic)
            throw new InvalidOperationException("Neo SDK protocol settings are not configured for testnet.");

        using var client = new RpcClient(new Uri(network.Endpoint), null, null, protocol);
        var transactionManager = await new TransactionManagerFactory(client).MakeTransactionAsync(script, signers);
        var transaction = await transactionManager.AddSignature(key).SignAsync();
        var maximumFee = new BigInteger(relayer.MaximumTransactionFeeGas * 100_000_000m);
        if (transaction.SystemFee + transaction.NetworkFee > maximumFee)
        {
            await SetStateAsync(claim.Id, "NeedsReview", "Estimated sponsored transaction fee exceeds the configured ceiling.", cancellationToken, release: true);
            return;
        }

        if (!await ReserveDailyFeeBudgetAsync(claim.Id, transaction.SystemFee + transaction.NetworkFee, cancellationToken))
        {
            await SetStateAsync(claim.Id, "NeedsReview", "Daily sponsored-fee budget is exhausted.", cancellationToken, release: true);
            return;
        }

        var raw = Convert.ToBase64String(transaction.ToArray());
        var persisted = await db.FaucetClaims.UpdateOneAsync(
            x => x.Id == claim.Id && x.State == "Processing" && x.SignedTransaction == null,
            Builders<FaucetClaimDocument>.Update
                .Set(x => x.SignedTransaction, raw)
                .Set(x => x.TransactionHash, transaction.Hash.ToString())
                .Set(x => x.ValidUntilBlock, transaction.ValidUntilBlock)
                .Set(x => x.State, "Broadcasting")
                .Set(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken: cancellationToken);
        if (persisted.ModifiedCount != 1) return;

        if (!await GetVerifiedNetworkAsync(cancellationToken))
            throw new InvalidOperationException("Testnet RPC network magic changed before transaction submission.");
        await client.SendRawTransactionAsync(transaction);
        await db.FaucetClaims.UpdateOneAsync(x => x.Id == claim.Id,
            Builders<FaucetClaimDocument>.Update.Set(x => x.State, "Submitted").Set(x => x.UpdatedAt, DateTime.UtcNow).Unset(x => x.LeaseUntil),
            cancellationToken: cancellationToken);
    }

    private async Task ReconcileExistingAsync(FaucetClaimDocument claim, CancellationToken cancellationToken)
    {
        JsonElement log;
        try
        {
            log = await rpc.SendAsync(network.Endpoint, "getapplicationlog", [claim.TransactionHash], cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException)
        {
            await RebroadcastPersistedAsync(claim, cancellationToken);
            return;
        }
        var executions = log.GetProperty("executions");
        if (executions.GetArrayLength() == 0)
        {
            await RebroadcastPersistedAsync(claim, cancellationToken);
            return;
        }
        var execution = executions[0];
        var vmState = execution.GetProperty("vmstate").GetString();
        if (string.Equals(vmState, "HALT", StringComparison.OrdinalIgnoreCase))
        {
            var notifications = execution.GetProperty("notifications");
            var hasClaimEvent = HasExpectedClaim(execution, claim.ScriptHash);
            if (hasClaimEvent)
            {
                await SetStateAsync(claim.Id, "Confirmed", null, cancellationToken, release: true, transactionHash: claim.TransactionHash);
                return;
            }
            await SetStateAsync(claim.Id, "NeedsReview", "Transaction halted without the expected faucet Claimed event.", cancellationToken, release: false);
            return;
        }

        if (vmState?.Contains("FAULT", StringComparison.OrdinalIgnoreCase) == true)
        {
            await SetStateAsync(claim.Id, "Failed", "The faucet claim transaction faulted on-chain.", cancellationToken, release: true, transactionHash: claim.TransactionHash);
            return;
        }

        await RebroadcastPersistedAsync(claim, cancellationToken);
    }

    private async Task RebroadcastPersistedAsync(FaucetClaimDocument claim, CancellationToken cancellationToken)
    {
        if (claim.SignedTransaction is null) return;
        if (claim.ValidUntilBlock is not null)
        {
            var blockCount = await rpc.SendAsync(network.Endpoint, "getblockcount", [], cancellationToken);
            if (blockCount.GetInt64() > claim.ValidUntilBlock.Value)
            {
                await SetStateAsync(claim.Id, "NeedsReview", "Persisted transaction expired before its outcome could be reconciled; no replacement was submitted.", cancellationToken, release: false, transactionHash: claim.TransactionHash);
                return;
            }
        }
        var bytes = Convert.FromBase64String(claim.SignedTransaction);
        var reader = new Neo.IO.MemoryReader(bytes);
        var transaction = reader.ReadSerializable<Transaction>();
        using var client = new RpcClient(new Uri(network.Endpoint), null, null, ProtocolSettings.Load(configuration.GetSection("FaucetRelayer:ProtocolSettings")));
        await client.SendRawTransactionAsync(transaction);
    }

    private bool HasExpectedClaim(JsonElement execution, string recipientScriptHash)
    {
        if (!execution.TryGetProperty("notifications", out var notifications) || notifications.ValueKind != JsonValueKind.Array) return false;
        BigInteger? claimedAmount = null;
        BigInteger? transferAmount = null;
        foreach (var notification in notifications.EnumerateArray())
        {
            var contract = NormalizeHash(notification.GetProperty("contract").GetString() ?? string.Empty);
            var eventName = notification.GetProperty("eventname").GetString();
            var state = notification.GetProperty("state").GetProperty("value");
            if (state.ValueKind != JsonValueKind.Array) continue;
            if (string.Equals(contract, NormalizeHash(faucet.ContractHash), StringComparison.OrdinalIgnoreCase)
                && string.Equals(eventName, "Claimed", StringComparison.Ordinal)
                && state.GetArrayLength() >= 2
                && string.Equals(NormalizeHash(FaucetRpcService.StackValue(state[0])), NormalizeHash(recipientScriptHash), StringComparison.OrdinalIgnoreCase))
            {
                claimedAmount = FaucetRpcService.StackInteger(state[1]);
            }
            else if (string.Equals(contract, NormalizeHash("0xd2a4cff31913016155e38e474a2c06d08be276cf"), StringComparison.OrdinalIgnoreCase)
                && string.Equals(eventName, "Transfer", StringComparison.Ordinal)
                && state.GetArrayLength() >= 3
                && string.Equals(NormalizeHash(FaucetRpcService.StackValue(state[0])), NormalizeHash(faucet.ContractHash), StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeHash(FaucetRpcService.StackValue(state[1])), NormalizeHash(recipientScriptHash), StringComparison.OrdinalIgnoreCase))
            {
                transferAmount = FaucetRpcService.StackInteger(state[2]);
            }
        }
        return claimedAmount is not null && transferAmount == claimedAmount;
    }

    private async Task<bool> ReserveDailyFeeBudgetAsync(string claimId, BigInteger feeDatoshis, CancellationToken cancellationToken)
    {
        var budgetId = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var reservationField = $"Reservations.{claimId}";
        var existing = await db.FaucetDailyFeeBudgets.Find(x => x.Id == budgetId).FirstOrDefaultAsync(cancellationToken);
        if (existing?.Reservations.TryGetValue(claimId, out var priorReservation) == true)
            return new BigInteger(priorReservation) >= feeDatoshis;

        try
        {
            await db.FaucetDailyFeeBudgets.UpdateOneAsync(
                x => x.Id == budgetId,
                Builders<FaucetDailyFeeBudgetDocument>.Update
                    .SetOnInsert(x => x.Id, budgetId)
                    .SetOnInsert(x => x.ReservedDatoshis, 0),
                new UpdateOptions { IsUpsert = true }, cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
        }

        var fee = checked((long)feeDatoshis);
        var dailyLimit = checked((long)(relayer.DailyFeeBudgetGas * 100_000_000m));
        var filter = Builders<FaucetDailyFeeBudgetDocument>.Filter.And(
            Builders<FaucetDailyFeeBudgetDocument>.Filter.Eq(x => x.Id, budgetId),
            Builders<FaucetDailyFeeBudgetDocument>.Filter.Lte(x => x.ReservedDatoshis, dailyLimit - fee),
            Builders<FaucetDailyFeeBudgetDocument>.Filter.Exists(reservationField, false));
        var update = Builders<FaucetDailyFeeBudgetDocument>.Update
            .Inc(x => x.ReservedDatoshis, fee)
            .Set(reservationField, fee);
        var result = await db.FaucetDailyFeeBudgets.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        if (result.ModifiedCount == 1) return true;
        existing = await db.FaucetDailyFeeBudgets.Find(x => x.Id == budgetId).FirstOrDefaultAsync(cancellationToken);
        return existing?.Reservations.TryGetValue(claimId, out priorReservation) == true && priorReservation >= fee;
    }

    private async Task<bool> GetVerifiedNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await rpc.SendAsync(network.Endpoint, "getversion", [], cancellationToken);
            return version.GetProperty("protocol").GetProperty("network").GetInt64() == faucet.NetworkMagic;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    private Task SetStateAsync(string id, string state, string? error, CancellationToken cancellationToken, bool release, string? transactionHash = null)
    {
        var update = Builders<FaucetClaimDocument>.Update.Set(x => x.State, state).Set(x => x.Error, error)
            .Set(x => x.UpdatedAt, DateTime.UtcNow).Unset(x => x.LeaseUntil);
        if (release) update = update.Unset(x => x.ActiveWalletKey);
        if (transactionHash is not null) update = update.Set(x => x.TransactionHash, transactionHash);
        return db.FaucetClaims.UpdateOneAsync(x => x.Id == id, update, cancellationToken: cancellationToken);
    }

    private static string NormalizeHash(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
}
