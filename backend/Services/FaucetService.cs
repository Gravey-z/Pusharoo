using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Net;

namespace backend.Services;

public sealed class FaucetService(
    MongoDbContext db,
    FaucetRpcService rpc,
    NeoWalletAddressValidator addressValidator,
    IOptions<FaucetOptions> faucetOptions,
    IOptions<FaucetRelayerOptions> relayerOptions)
{
    private readonly FaucetOptions faucet = faucetOptions.Value;
    private readonly FaucetRelayerOptions relayer = relayerOptions.Value;

    public async Task<FaucetStatusResponse> GetStatusAsync(string? address, CancellationToken cancellationToken)
    {
        var availability = await rpc.GetAvailabilityAsync(cancellationToken);
        if (!availability.Available)
            return new FaucetStatusResponse(false, availability.Reason, false, GetOperationalReason(), null, null, null, null, null, null, null, null, null, null, null, null);

        var statusResult = await rpc.InvokeAsync("getStatus", null, cancellationToken);
        var status = FaucetRpcService.StackArray(statusResult);
        bool? registered = null;
        bool? sponsoredEligible = null;
        bool? directEligible = null;
        string? nextClaim = null;
        string? sponsoredReason = null;
        string? directReason = null;
        if (!string.IsNullOrWhiteSpace(address))
        {
            var validation = addressValidator.Validate(address);
            if (validation.IsValid)
            {
                var claimResult = await rpc.InvokeAsync("getClaimStatus", validation.ScriptHash, cancellationToken);
                var claim = FaucetRpcService.StackArray(claimResult);
                registered = ParseStackBoolean(claim[0]);
                sponsoredEligible = ParseStackBoolean(claim[1]);
                directEligible = ParseStackBoolean(claim[2]);
                nextClaim = FaucetRpcService.StackInteger(claim[3]).ToString();
                sponsoredReason = FaucetRpcService.StackValue(claim[4]);
                directReason = FaucetRpcService.StackValue(claim[5]);
            }
        }

        var sponsoredReasonUnavailable = GetOperationalReason();
        return new FaucetStatusResponse(
            true,
            availability.Reason,
            sponsoredReasonUnavailable is null,
            sponsoredReasonUnavailable,
            FaucetRpcService.StackInteger(status[0]).ToString(),
            FaucetRpcService.StackInteger(status[1]).ToString(),
            FaucetRpcService.StackValue(status[2]),
            FaucetRpcService.StackInteger(status[3]).ToString(),
            FaucetRpcService.StackInteger(status[5]).ToString(),
            registered,
            sponsoredEligible,
            directEligible,
            nextClaim,
            sponsoredReason,
            directReason,
            FaucetRpcService.StackInteger(status[6]).ToString());
    }

    public async Task<FaucetClaimResponse> SubmitClaimAsync(
        FaucetClaimRequest request,
        WalletSessionIdentity actor,
        IPAddress? clientIp,
        CancellationToken cancellationToken)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "N", out _))
            throw new FaucetRequestException(400, "Claim request ID must be a UUID without separators.");
        var recipient = addressValidator.Validate(actor.Address);
        if (!recipient.IsValid || !string.Equals(NormalizeHash(recipient.ScriptHash), NormalizeHash(actor.ScriptHash), StringComparison.OrdinalIgnoreCase))
            throw new FaucetRequestException(403, "The signed-in wallet identity is invalid.");
        var requestId = request.RequestId.ToLowerInvariant();
        var activeKey = $"neo3:testnet:{recipient.ScriptHash.ToLowerInvariant()}";
        var previous = await db.FaucetClaims.Find(x => x.Id == requestId).FirstOrDefaultAsync(cancellationToken);
        if (previous is not null) return MapForActor(previous, recipient.ScriptHash);
        var existing = await db.FaucetClaims.Find(x => x.ActiveWalletKey == activeKey).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return Map(existing);

        EnsureConfigured();
        var availability = await rpc.GetAvailabilityAsync(cancellationToken);
        if (!availability.Available) throw new FaucetUnavailableException(availability.Reason ?? "Faucet unavailable.");
        var current = await rpc.InvokeAsync("getClaimStatus", recipient.ScriptHash, cancellationToken);
        var claimStatus = FaucetRpcService.StackArray(current);
        if (!ParseStackBoolean(claimStatus[1]))
            throw new FaucetRequestException(409, $"This wallet is not currently eligible for a sponsored claim ({FaucetRpcService.StackValue(claimStatus[4])}).");

        var queueDepth = await db.FaucetClaims.CountDocumentsAsync(x => x.State == "Queued" || x.State == "Processing", cancellationToken: cancellationToken);
        if (queueDepth >= Math.Clamp(faucet.MaximumQueueDepth, 1, 10000))
            throw new FaucetRequestException(503, "The faucet queue is full. Try again shortly.");

        if (clientIp is null)
            throw new FaucetRequestException(503, "Cannot verify the client IP address for a sponsored claim.");
        var ipAddress = (clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp).ToString();
        await ReserveIpAsync(ipAddress, requestId, cancellationToken);

        var claim = new FaucetClaimDocument
        {
            Id = requestId,
            Recipient = recipient.WalletAddress,
            ScriptHash = recipient.ScriptHash,
            ActiveWalletKey = activeKey,
            ClientIpAddress = ipAddress,
            State = "Queued",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        try
        {
            await db.FaucetClaims.InsertOneAsync(claim, cancellationToken: cancellationToken);
            return Map(claim);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            previous = await db.FaucetClaims.Find(x => x.Id == requestId).FirstOrDefaultAsync(cancellationToken);
            if (previous is not null) return MapForActor(previous, recipient.ScriptHash);
            var duplicate = await db.FaucetClaims.Find(x => x.ActiveWalletKey == activeKey).FirstOrDefaultAsync(cancellationToken);
            await db.FaucetIpClaims.DeleteOneAsync(x => x.IpAddress == ipAddress && x.ClaimId == requestId, cancellationToken);
            return duplicate is null ? throw new FaucetRequestException(409, "A claim is already being processed.") : Map(duplicate);
        }
    }

    private async Task ReserveIpAsync(string ipAddress, string requestId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var reservation = new FaucetIpClaimDocument
        {
            IpAddress = ipAddress,
            ClaimId = requestId,
            ExpiresAt = now.AddHours(24)
        };
        try
        {
            await db.FaucetIpClaims.InsertOneAsync(reservation, cancellationToken: cancellationToken);
            return;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var replaced = await db.FaucetIpClaims.ReplaceOneAsync(
                x => x.IpAddress == ipAddress && x.ExpiresAt <= now,
                reservation,
                cancellationToken: cancellationToken);
            if (replaced.ModifiedCount == 1) return;
            try
            {
                // The TTL monitor may have removed the expired record between the two writes.
                await db.FaucetIpClaims.InsertOneAsync(reservation, cancellationToken: cancellationToken);
                return;
            }
            catch (MongoWriteException retryException) when (retryException.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
            }
        }

        throw new FaucetRequestException(429, "This IP address has already requested a sponsored claim in the past 24 hours.");
    }

    public async Task<FaucetClaimResponse?> GetClaimAsync(string id, CancellationToken cancellationToken)
    {
        var claim = await db.FaucetClaims.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
        return claim is null ? null : Map(claim);
    }

    private void EnsureConfigured()
    {
        var reason = GetOperationalReason();
        if (reason is not null) throw new FaucetUnavailableException(reason);
    }

    private string? GetOperationalReason()
    {
        if (!relayer.KeyConfigured) return "Faucet relayer signing key is not configured.";
        if (relayer.MaximumTransactionFeeGas <= 0 || relayer.DailyFeeBudgetGas <= 0)
            return "Faucet sponsored-fee limits are not configured.";
        return null;
    }

    private static bool ParseStackBoolean(System.Text.Json.JsonElement item) =>
        string.Equals(FaucetRpcService.StackValue(item), "true", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHash(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

    private FaucetClaimResponse MapForActor(FaucetClaimDocument claim, string scriptHash) =>
        string.Equals(claim.ScriptHash, scriptHash, StringComparison.OrdinalIgnoreCase)
            ? Map(claim)
            : throw new FaucetRequestException(409, "Claim request ID belongs to a different wallet.");

    private FaucetClaimResponse Map(FaucetClaimDocument claim) => new(
        claim.Id,
        claim.State,
        claim.TransactionHash,
        claim.TransactionHash is null ? null : rpc.ExplorerTransactionUrl(claim.TransactionHash),
        claim.Error);
}

public sealed record FaucetStatusResponse(
    bool Available,
    string? Reason,
    bool SponsoredAvailable,
    string? SponsoredReason,
    string? ClaimAmount,
    string? DailyCap,
    string? Paused,
    string? Balance,
    string? RemainingDailyAllowance,
    bool? Registered,
    bool? SponsoredEligible,
    bool? DirectEligible,
    string? NextClaimAt,
    string? SponsoredIneligibleReason,
    string? DirectIneligibleReason,
    string? DailyResetAt);

public sealed record FaucetClaimRequest(string RequestId);
public sealed record FaucetClaimResponse(string RequestId, string State, string? TransactionHash, string? ExplorerUrl, string? Error);

public sealed class FaucetRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
