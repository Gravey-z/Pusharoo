using System.Numerics;
using System.Security.Cryptography;

namespace backend.Services;

/// <summary>Validates a canonical Neo N3 address and derives its script hash.</summary>
public sealed class NeoWalletAddressValidator
{
    private const byte NeoN3AddressVersion = 0x35;
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public NeoWalletAddressValidationResult Validate(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Trim().Length > 64)
        {
            return Fail("A valid Neo N3 wallet address is required.");
        }

        var canonicalAddress = address.Trim();
        if (!TryBase58Decode(canonicalAddress, out var decoded)
            || decoded.Length != 25
            || decoded[0] != NeoN3AddressVersion)
        {
            return Fail("Wallet address must be a Neo N3 Base58Check address.");
        }

        var payload = decoded[..21];
        var checksum = decoded[21..];
        if (!SHA256.HashData(SHA256.HashData(payload))[..4].SequenceEqual(checksum))
        {
            return Fail("Wallet address has an invalid Base58Check checksum.");
        }

        var scriptHashBytes = payload[1..].Reverse().ToArray();
        return new NeoWalletAddressValidationResult(
            true,
            string.Empty,
            canonicalAddress,
            $"0x{Convert.ToHexString(scriptHashBytes).ToLowerInvariant()}");
    }

    private static bool TryBase58Decode(string value, out byte[] bytes)
    {
        bytes = [];
        var number = BigInteger.Zero;
        foreach (var character in value)
        {
            var digit = Base58Alphabet.IndexOf(character);
            if (digit < 0)
            {
                return false;
            }

            number = (number * 58) + digit;
        }

        var decoded = number.ToByteArray(isUnsigned: true, isBigEndian: true).ToList();
        var leadingZeroes = value.TakeWhile(character => character == Base58Alphabet[0]).Count();
        if (leadingZeroes > 0)
        {
            decoded.InsertRange(0, Enumerable.Repeat((byte)0, leadingZeroes));
        }

        bytes = decoded.ToArray();
        return true;
    }

    private static NeoWalletAddressValidationResult Fail(string error)
        => new(false, error, string.Empty, string.Empty);
}

public sealed record NeoWalletAddressValidationResult(
    bool IsValid,
    string Error,
    string WalletAddress,
    string ScriptHash);
