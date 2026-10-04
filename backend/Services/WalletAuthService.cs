using System.Security.Cryptography;
using System.Text;
using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.Contracts;

namespace backend.Services;

public sealed class WalletAuthService(
    MongoDbContext db,
    NeoWalletAddressValidator addressValidator,
    NeoWalletSignatureVerifier signatureVerifier,
    WalletSignatureRequestValidator signatureRequestValidator,
    IOptions<WalletSignatureOptions> signatureOptions,
    IOptions<WalletAuthOptions> authOptions)
{
    private readonly WalletAuthOptions options = authOptions.Value;
    private readonly WalletSignatureOptions signatures = signatureOptions.Value;

    public async Task<(WalletLoginChallengeResponse Response, string BrowserToken)> CreateChallengeAsync(
        WalletLoginChallengeRequest request, string? origin, CancellationToken cancellationToken)
    {
        RequireOrigin(origin);
        if (request.Address?.Length > 64 || request.Network?.Length > 32 || request.Provider?.Length > 32)
            throw new WalletAuthException(400, "invalid_login_request", "Wallet login request is too large.");

        var address = addressValidator.Validate(request.Address);
        if (!address.IsValid) throw new WalletAuthException(400, "invalid_wallet", address.Error);
        if (request.Network is not ("neo3:testnet" or "neo3:mainnet"))
            throw new WalletAuthException(400, "invalid_network", "Select N3 TestNet or MainNet.");
        if (request.Provider is not ("neoline" or "onegate" or "walletconnect"))
            throw new WalletAuthException(400, "invalid_provider", "Unsupported wallet provider.");
        if (string.IsNullOrWhiteSpace(signatures.Audience))
            throw new WalletAuthException(503, "auth_unavailable", "Wallet login is not configured.");

        var issuedAt = DateTime.UtcNow;
        issuedAt = issuedAt.AddTicks(-(issuedAt.Ticks % TimeSpan.TicksPerMillisecond));
        var expiresAt = issuedAt.AddMinutes(Math.Clamp(options.ChallengeLifetimeMinutes, 1, 10));
        var browserToken = NewToken();
        var nonce = NewToken();
        var message = string.Join('\n',
            "Sign in to Pusharoo",
            "Schema: pusharoo.wallet.login.v1",
            "Action: wallet.login",
            $"Wallet: {address.WalletAddress}",
            $"Script hash: {address.ScriptHash}",
            $"Network at login: {request.Network}",
            $"Origin: {origin}",
            $"Audience: {signatures.Audience.Trim()}",
            "Scope: Pusharoo workspace actions on N3 TestNet and MainNet",
            $"Session duration: {Math.Clamp(options.SessionLifetimeHours, 1, 168)} hours",
            "This login authorizes permitted workspace actions during the session. It does not approve blockchain transactions.",
            $"Issued at UTC: {issuedAt:O}",
            $"Expires at UTC: {expiresAt:O}",
            $"Nonce: {nonce}");
        var challenge = new WalletLoginChallengeDocument
        {
            Id = NewToken(),
            BrowserBindingHash = Hash(browserToken),
            Address = address.WalletAddress,
            ScriptHash = address.ScriptHash,
            Network = request.Network,
            Provider = request.Provider,
            Origin = origin!,
            Audience = signatures.Audience.Trim(),
            Nonce = nonce,
            Message = message,
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = expiresAt
        };
        await db.WalletLoginChallenges.InsertOneAsync(challenge, cancellationToken: cancellationToken);
        return (new WalletLoginChallengeResponse(challenge.Id, message, challenge.Origin,
            challenge.Audience, issuedAt.ToString("O"), nonce, expiresAt), browserToken);
    }

    public async Task<(WalletLoginSessionDocument Session, string Token)> LoginAsync(
        WalletLoginRequest request, string? browserToken, string? origin, string? previousSessionToken,
        CancellationToken cancellationToken)
    {
        RequireOrigin(origin);
        if (request.ChallengeId is null || request.ChallengeId.Length != 64 || !IsHex(request.ChallengeId)
            || browserToken is null || browserToken.Length != 64 || !IsHex(browserToken)
            || request.Signature is null)
        {
            throw new WalletAuthException(400, "invalid_login", "Request a new wallet login challenge.");
        }

        var challenge = await db.WalletLoginChallenges.Find(x => x.Id == request.ChallengeId).FirstOrDefaultAsync(cancellationToken);
        if (challenge is null || challenge.ConsumedAtUtc is not null || challenge.ExpiresAtUtc <= DateTime.UtcNow
            || !FixedHashEquals(challenge.BrowserBindingHash, Hash(browserToken)))
        {
            throw new WalletAuthException(401, "invalid_challenge", "Login challenge expired or belongs to another browser. Request a new one.");
        }

        var signature = request.Signature;
        if (signature.Message?.Length > 2048 || signature.Data?.Length > 2048
            || signature.PublicKey?.Length > 256 || signature.Salt?.Length > 512
            || signature.MessageHex?.Length > 8192)
        {
            throw new WalletAuthException(400, "invalid_login", "Wallet signature response is too large.");
        }
        var validationError = signatureRequestValidator.Validate(signature);
        if (validationError is not null) throw new WalletAuthException(401, "invalid_signature", validationError);
        if (signature.Address != challenge.Address
            || !HashesMatch(signature.ScriptHash, challenge.ScriptHash)
            || signature.Network != challenge.Network || signature.Provider != challenge.Provider
            || signature.Origin != challenge.Origin || signature.Audience != challenge.Audience
            || signature.Nonce != challenge.Nonce
            || signature.IssuedAtUtc != challenge.IssuedAtUtc.ToString("O")
            || signature.Message != challenge.Message || origin != challenge.Origin)
        {
            throw new WalletAuthException(401, "invalid_signature", "Wallet signature does not match this login challenge.");
        }
        var verified = signatureVerifier.Verify(signature, challenge.Message);
        if (!verified.IsValid || verified.Address != challenge.Address
            || !HashesMatch(verified.ScriptHash, challenge.ScriptHash) || string.IsNullOrWhiteSpace(verified.PublicKey))
        {
            throw new WalletAuthException(401, "invalid_signature", "Wallet login signature could not be verified.");
        }

        var now = DateTime.UtcNow;
        var filter = Builders<WalletLoginChallengeDocument>.Filter.Where(x => x.Id == challenge.Id
            && x.BrowserBindingHash == challenge.BrowserBindingHash && x.ConsumedAtUtc == null && x.ExpiresAtUtc > now);
        var consumed = await db.WalletLoginChallenges.FindOneAndUpdateAsync(filter,
            Builders<WalletLoginChallengeDocument>.Update.Set(x => x.ConsumedAtUtc, now),
            new FindOneAndUpdateOptions<WalletLoginChallengeDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (consumed is null)
            throw new WalletAuthException(409, "challenge_used", "Login challenge was already used. Request a new one.");

        if (previousSessionToken is not null) await RevokeAsync(previousSessionToken, cancellationToken);
        var token = NewToken();
        var session = new WalletLoginSessionDocument
        {
            TokenHash = Hash(token),
            Address = verified.Address!,
            ScriptHash = verified.ScriptHash!,
            PublicKey = verified.PublicKey,
            Provider = signature.Provider,
            NetworkAtLogin = signature.Network,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(Math.Clamp(options.SessionLifetimeHours, 1, 168))
        };
        await db.WalletLoginSessions.InsertOneAsync(session, cancellationToken: cancellationToken);
        return (session, token);
    }

    public async Task<WalletLoginSessionDocument?> GetSessionAsync(string? token, CancellationToken cancellationToken)
    {
        if (token is null || token.Length != 64 || !IsHex(token)) return null;
        var session = await db.WalletLoginSessions.Find(x => x.TokenHash == Hash(token)).FirstOrDefaultAsync(cancellationToken);
        return session is { RevokedAtUtc: null } && session.ExpiresAtUtc > DateTime.UtcNow ? session : null;
    }

    public async Task RevokeAsync(string? token, CancellationToken cancellationToken)
    {
        if (token is null || token.Length != 64 || !IsHex(token)) return;
        await db.WalletLoginSessions.UpdateOneAsync(
            x => x.TokenHash == Hash(token) && x.RevokedAtUtc == null,
            Builders<WalletLoginSessionDocument>.Update.Set(x => x.RevokedAtUtc, DateTime.UtcNow),
            cancellationToken: cancellationToken);
    }

    public void RequireOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(options.PublicOrigin))
            throw new WalletAuthException(503, "auth_unavailable", "Wallet login is not configured.");
        if (!string.Equals(origin, options.PublicOrigin, StringComparison.Ordinal))
            throw new WalletAuthException(403, "origin_rejected", "Browser origin does not match this Pusharoo application.");
        if (!signatures.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
            throw new WalletAuthException(403, "origin_rejected", "Browser origin is not allowed for wallet signatures.");
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool IsHex(string value) => value.All(Uri.IsHexDigit);
    private static bool HashesMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var normalizedLeft = left.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? left.Trim()[2..] : left.Trim();
        var normalizedRight = right.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? right.Trim()[2..] : right.Trim();
        return normalizedLeft.Length == 40 && normalizedRight.Length == 40
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }
    private static bool FixedHashEquals(string left, string right) => left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}

public sealed class WalletAuthException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
