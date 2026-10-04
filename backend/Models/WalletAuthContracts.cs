using Pusharoo.Contracts;

namespace backend.Models;

public sealed record WalletLoginChallengeRequest(string Address, string Network, string Provider);
public sealed record WalletLoginChallengeResponse(string ChallengeId, string Message, string Origin,
    string Audience, string IssuedAtUtc, string Nonce, DateTime ExpiresAtUtc);
public sealed record WalletLoginRequest(string ChallengeId, WalletSignatureRequest? Signature);
public sealed record WalletSessionResponse(bool Authenticated, string? Address, string? ScriptHash,
    string? PublicKey, DateTime? ExpiresAtUtc);
