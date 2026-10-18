using System.Numerics;
using System.Security.Cryptography;

namespace Pusharoo.EventRelay.Services;

public static class NeoPaymentEncoding
{
    public static string NormalizeHash(string? hash)
    {
        var value = hash?.Trim() ?? string.Empty;

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? $"0x{value[2..].ToLowerInvariant()}"
            : $"0x{value.ToLowerInvariant()}";
    }

    public static bool IsScriptHash(string? hash) =>
        NormalizeHash(hash).Length == 42 && NormalizeHash(hash)[2..].All(Uri.IsHexDigit);

    public static bool TryGetScriptHashFromAddress(string address, out string scriptHash)
    {
        scriptHash = string.Empty;

        try
        {
            var decoded = Base58Decode(address.Trim());

            if (decoded.Length != 25 || decoded[0] != 0x35)
                return false;

            var checksum = SHA256.HashData(SHA256.HashData(decoded[..21]))[..4];

            if (!checksum.SequenceEqual(decoded[21..]))
                return false;

            var bytes = decoded[1..21];
            Array.Reverse(bytes);
            scriptHash = "0x" + Convert.ToHexString(bytes).ToLowerInvariant();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Base58Decode(string value)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        BigInteger number = BigInteger.Zero;

        foreach (var character in value)
        {
            var index = alphabet.IndexOf(character);

            if (index < 0)
                throw new FormatException();

            number = number * 58 + index;
        }

        var encoded = number.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = value.TakeWhile(character => character == '1').Count();

        return Enumerable.Repeat((byte)0, leadingZeros).Concat(encoded).ToArray();
    }

    public static string NormalizeTransactionId(string? id)
    {
        var value = id?.Trim() ?? string.Empty;

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];

        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new PaymentValidationException(
                "Transaction ID must be a 32-byte hexadecimal hash.",
                400
            );

        return "0x" + value.ToLowerInvariant();
    }
}
