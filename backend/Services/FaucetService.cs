using System.Security.Cryptography;
using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.Contracts;

namespace backend.Services;

public sealed class FaucetService(
    MongoDbContext db,
    FaucetRpcService rpc,
    NeoWalletAddressValidator addressValidator,
    WalletSignatureRequestValidator signatureRequestValidator,
    NeoWalletSignatureVerifier signatureVerifier,
    IOptions<WalletSignatureOptions> walletOptions,
    IOptions<FaucetOptions> faucetOptions)
{
    private readonly WalletSignatureOptions wallet = walletOptions.Value;
    private readonly FaucetOptions faucet = faucetOptions.Value;

    public async Task<FaucetStatusResponse> GetStatusAsync(string? address, CancellationToken cancellationToken)
    {
        var availability = await rpc.GetAvailabilityAsync(cancellationToken);
        if (!availability.Available)
            return new FaucetStatusResponse(false, availability.Reason, false, GetOperationalReason(), null, null, null, null, null, null, null, null, null, null, null, null);

        var statusResult = await rpc.InvokeAsync("getStatus", null, cancellationToken);
        var status = statusResult.GetProperty("stack");
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
                var claim = claimResult.GetProperty("stack");
                registered = ParseStackBoolean(claim[0]);
                sponsoredEligible = ParseStackBoolean(claim[1]);
                directEligible = ParseStackBoolean(claim[2]);
                nextClaim = FaucetRpcService.StackValue(claim[3]);
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

    public async Task<FaucetChallengeResponse> CreateChallengeAsync(string address, string? origin, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var availability = await rpc.GetAvailabilityAsync(cancellationToken);
        if (!availability.Available) throw new FaucetUnavailableException(availability.Reason ?? "Faucet unavailable.");

        var recipient = addressValidator.Validate(address);
        if (!recipient.IsValid) throw new FaucetRequestException(400, recipient.Error);
        var current = await rpc.InvokeAsync("getClaimStatus", recipient.ScriptHash, cancellationToken);
        var claimStatus = current.GetProperty("stack");
        if (!ParseStackBoolean(claimStatus[1]))
            throw new FaucetRequestException(409, $"This wallet is not currently eligible for a sponsored claim ({FaucetRpcService.StackValue(claimStatus[4])}).");

        if (string.IsNullOrWhiteSpace(origin)) throw new FaucetRequestException(400, "Request origin is required.");
        if (!IsConfiguredOrigin(origin)) throw new FaucetRequestException(403, "Request origin is not allowed for this Pusharoo application.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expires = DateTime.UtcNow.AddSeconds(Math.Clamp(faucet.ChallengeLifetimeSeconds, 30, 300));
        var message = BuildMessage(recipient.WalletAddress, recipient.ScriptHash, origin, id, expires);
        await db.FaucetChallenges.InsertOneAsync(new FaucetChallengeDocument
        {
            Id = id,
            Recipient = recipient.WalletAddress,
            ScriptHash = recipient.ScriptHash,
            Message = message,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expires
        }, cancellationToken: cancellationToken);
        return new FaucetChallengeResponse(id, message, expires);
    }

    public async Task<FaucetClaimResponse> SubmitClaimAsync(
        FaucetClaimRequest request,
        string? origin,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var availability = await rpc.GetAvailabilityAsync(cancellationToken);
        if (!availability.Available) throw new FaucetUnavailableException(availability.Reason ?? "Faucet unavailable.");
        if (request.Signature is null) throw new FaucetRequestException(400, "A wallet signature is required.");
        if (string.IsNullOrWhiteSpace(request.ChallengeId) || request.ChallengeId.Length > 64) throw new FaucetRequestException(400, "Challenge ID is invalid.");

        var challenge = await db.FaucetChallenges.Find(x => x.Id == request.ChallengeId).FirstOrDefaultAsync(cancellationToken);
        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= DateTime.UtcNow)
            throw new FaucetRequestException(400, "Challenge has expired or was already used. Request a new one.");

        var signature = request.Signature;
        if (signature.Network != "neo3:testnet") throw new FaucetRequestException(400, "Faucet claims are available on Neo N3 testnet only.");
        if (signature.Address != challenge.Recipient || !string.Equals(signature.ScriptHash, challenge.ScriptHash, StringComparison.OrdinalIgnoreCase))
            throw new FaucetRequestException(400, "Signed wallet does not match the requested recipient.");
        var validationError = signatureRequestValidator.Validate(signature);
        if (validationError is not null) throw new FaucetRequestException(400, validationError);
        if (!string.Equals(origin, signature.Origin, StringComparison.Ordinal) || !string.Equals(signature.Message, challenge.Message, StringComparison.Ordinal))
            throw new FaucetRequestException(400, "Wallet signature does not match this faucet challenge.");
        var verification = signatureVerifier.Verify(signature, challenge.Message);
        if (!verification.IsValid) throw new FaucetRequestException(400, verification.Error);
        var activeKey = $"neo3:testnet:{challenge.ScriptHash.ToLowerInvariant()}";
        var existing = await db.FaucetClaims.Find(x => x.ActiveWalletKey == activeKey).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return Map(existing);

        var queueDepth = await db.FaucetClaims.CountDocumentsAsync(x => x.State == "Queued" || x.State == "Processing", cancellationToken: cancellationToken);
        if (queueDepth >= Math.Clamp(faucet.MaximumQueueDepth, 1, 10000))
            throw new FaucetRequestException(503, "The faucet queue is full. Try again shortly.");

        var consumed = await db.FaucetChallenges.FindOneAndUpdateAsync(
            x => x.Id == challenge.Id && x.ConsumedAt == null && x.ExpiresAt > DateTime.UtcNow,
            Builders<FaucetChallengeDocument>.Update.Set(x => x.ConsumedAt, DateTime.UtcNow),
            new FindOneAndUpdateOptions<FaucetChallengeDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (consumed is null) throw new FaucetRequestException(409, "Challenge was already submitted. Request a new one.");

        var claim = new FaucetClaimDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            Recipient = challenge.Recipient,
            ScriptHash = challenge.ScriptHash,
            ActiveWalletKey = activeKey,
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
            var duplicate = await db.FaucetClaims.Find(x => x.ActiveWalletKey == activeKey).FirstOrDefaultAsync(cancellationToken);
            return duplicate is null ? throw new FaucetRequestException(409, "A claim is already being processed.") : Map(duplicate);
        }
    }

    public async Task<FaucetClaimResponse?> GetClaimAsync(string id, CancellationToken cancellationToken)
    {
        var claim = await db.FaucetClaims.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
        return claim is null ? null : Map(claim);
    }

    private string BuildMessage(string address, string scriptHash, string origin, string id, DateTime expires) => string.Join('\n',
    [
        "Pusharoo testnet faucet claim v1",
        "Action: claim GAS",
        "Network: neo3:testnet",
        $"Network magic: {faucet.NetworkMagic}",
        $"Faucet: {rpc.ContractHash}",
        $"Recipient: {address}",
        $"Script hash: {scriptHash}",
        $"Request ID: {id}",
        $"Origin: {origin}",
        $"Audience: {wallet.Audience}",
        $"Expires at UTC: {expires:O}"
    ]);

    private void EnsureConfigured()
    {
        var reason = GetOperationalReason();
        if (reason is not null) throw new FaucetUnavailableException(reason);
    }

    private string? GetOperationalReason()
    {
        if (!faucet.Enabled) return "Faucet claims are disabled by configuration.";
        if (!faucet.RelayerEnabled) return "Faucet relayer is not enabled.";
        if (wallet.AllowedOrigins.Length == 0 || string.IsNullOrWhiteSpace(wallet.Audience)) return "Faucet claims are disabled until wallet signature origins are configured.";
        return null;
    }

    private bool IsConfiguredOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.AbsolutePath != "/" || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) return false;
        var canonicalOrigin = parsed.GetLeftPart(UriPartial.Authority);
        return string.Equals(origin, canonicalOrigin, StringComparison.Ordinal)
            && wallet.AllowedOrigins.Any(allowed => Uri.TryCreate(allowed, UriKind.Absolute, out var configured)
                && string.Equals(configured.GetLeftPart(UriPartial.Authority), canonicalOrigin, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ParseStackBoolean(System.Text.Json.JsonElement item) =>
        string.Equals(FaucetRpcService.StackValue(item), "true", StringComparison.OrdinalIgnoreCase);

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

public sealed record FaucetChallengeResponse(string ChallengeId, string Message, DateTime ExpiresAtUtc);
public sealed record FaucetClaimRequest(string ChallengeId, WalletSignatureRequest? Signature);
public sealed record FaucetClaimResponse(string RequestId, string State, string? TransactionHash, string? ExplorerUrl, string? Error);

public sealed class FaucetRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
