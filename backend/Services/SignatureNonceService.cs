using System.Security.Cryptography;
using System.Text;
using backend.Models;
using MongoDB.Driver;

namespace backend.Services;

/// <summary>Stores consumed wallet-signature nonces until their signed-message window expires.</summary>
public sealed class SignatureNonceService(MongoDbContext db)
{
    private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(12);

    public async Task<bool> TryConsumeAsync(WalletSignatureRequest signature, CancellationToken cancellationToken)
    {
        var nonce = new WebhookAuthorizationNonceDocument
        {
            Id = GetNonceId(signature),
            ExpiresAt = DateTime.UtcNow.Add(NonceLifetime)
        };

        try
        {
            await db.WebhookAuthorizationNonces.InsertOneAsync(nonce, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    private static string GetNonceId(WalletSignatureRequest signature)
    {
        var value = $"{signature.PublicKey.Trim()}:{signature.Nonce.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
