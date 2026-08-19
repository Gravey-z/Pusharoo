namespace Pusharoo.Contracts;

public sealed record WalletSignatureRequest(
    string Address,
    string ScriptHash,
    string Network,
    string Provider,
    string Origin,
    string IssuedAtUtc,
    string Nonce,
    string Message,
    string PublicKey,
    string Data,
    string? Salt,
    string? MessageHex);
