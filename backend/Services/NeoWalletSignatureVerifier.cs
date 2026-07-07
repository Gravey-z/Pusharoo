using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using backend.Models;

namespace backend.Services;

public sealed class NeoWalletSignatureVerifier
{
    private const byte NeoN3AddressVersion = 0x35;
    private const string P256PrimeHex = "ffffffff00000001000000000000000000000000ffffffffffffffffffffffff";
    private const string P256BHex = "5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b";
    private const string NeoCheckSigInteropCode = "56e7b327";
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static readonly BigInteger P256Prime = FromHex(P256PrimeHex);
    private static readonly BigInteger P256B = FromHex(P256BHex);

    public WalletSignatureVerificationResult Verify(
        WalletSignatureRequest signature,
        string expectedMessage)
    {
        if (!TryReadPublicKey(signature.PublicKey, out var compressedPublicKey, out var parameters))
        {
            return Fail("Wallet public key is invalid.");
        }

        if (!TryReadSignature(signature.Data, out var signatureBytes))
        {
            return Fail("Wallet signature data is invalid.");
        }

        if (!IsSignatureForExpectedMessage(signature, expectedMessage, parameters, signatureBytes))
        {
            return Fail("Wallet signature could not be verified.");
        }

        var scriptHash = GetScriptHashFromPublicKey(compressedPublicKey);
        if (!string.Equals(scriptHash, NormalizeHex(signature.ScriptHash), StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Wallet script hash does not match the signing public key.");
        }

        var address = GetAddressFromScriptHash(scriptHash);
        if (!string.Equals(address, signature.Address.Trim(), StringComparison.Ordinal))
        {
            return Fail("Wallet address does not match the signing public key.");
        }

        return new WalletSignatureVerificationResult(
            true,
            string.Empty,
            ToHex(compressedPublicKey),
            address,
            scriptHash);
    }

    public bool PublicKeysMatch(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && TryReadPublicKey(left, out var leftPublicKey, out _)
            && TryReadPublicKey(right, out var rightPublicKey, out _)
            && leftPublicKey.SequenceEqual(rightPublicKey);
    }

    private static bool IsSignatureForExpectedMessage(
        WalletSignatureRequest signature,
        string expectedMessage,
        ECParameters parameters,
        byte[] signatureBytes)
    {
        try
        {
            var candidateMessageHexes = GetExpectedMessageHexes(expectedMessage, signature.Salt).ToArray();
            var providedMessageHex = NormalizeOptionalHex(signature.MessageHex);

            if (providedMessageHex is not null
                && !candidateMessageHexes.Contains(providedMessageHex, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var messageHexes = providedMessageHex is null
                ? candidateMessageHexes
                : [providedMessageHex];

            using var ecdsa = ECDsa.Create(parameters);

            return messageHexes.Any(messageHex =>
            {
                var messageHash = SHA256.HashData(HexToBytes(messageHex));

                return ecdsa.VerifyHash(
                    messageHash,
                    signatureBytes,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            });
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IEnumerable<string> GetExpectedMessageHexes(string message, string? salt)
    {
        yield return ClassicFormat(message);
        yield return JavaScriptStringToHex(message);

        if (!string.IsNullOrWhiteSpace(salt))
        {
            yield return ClassicFormat($"{salt.Trim()}{message}");
        }
    }

    private static string ClassicFormat(string message)
    {
        var parameterHexString = JavaScriptStringToHex(message);
        var lengthHex = NumToVarInt(parameterHexString.Length / 2);

        return $"010001f0{lengthHex}{parameterHexString}0000";
    }

    private static string JavaScriptStringToHex(string value)
    {
        var builder = new StringBuilder(value.Length * 2);

        foreach (var character in value)
        {
            builder.Append(((byte)character).ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string NumToVarInt(int value)
    {
        if (value < 0xfd)
        {
            return value.ToString("x2", CultureInfo.InvariantCulture);
        }

        if (value <= 0xffff)
        {
            return "fd" + ToLittleEndianHex(value, 2);
        }

        return "fe" + ToLittleEndianHex(value, 4);
    }

    private static string ToLittleEndianHex(int value, int byteCount)
    {
        var bytes = BitConverter.GetBytes(value);

        return Convert.ToHexString(bytes[..byteCount]).ToLowerInvariant();
    }

    private static bool TryReadPublicKey(
        string value,
        out byte[] compressedPublicKey,
        out ECParameters parameters)
    {
        compressedPublicKey = [];
        parameters = default;

        try
        {
            var publicKey = NormalizeHex(value);
            if (publicKey.Length == 130 && publicKey.StartsWith("04", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = HexToBytes(publicKey);
                compressedPublicKey = CompressPublicKey(bytes);
                parameters = new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint
                    {
                        X = bytes[1..33],
                        Y = bytes[33..65]
                    }
                };

                return true;
            }

            if (publicKey.Length != 66
                || (publicKey[..2] != "02" && publicKey[..2] != "03"))
            {
                return false;
            }

            compressedPublicKey = HexToBytes(publicKey);
            var x = FromBytes(compressedPublicKey[1..33]);
            var y = DecompressY(x, compressedPublicKey[0] == 0x03);

            parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = ToFixedLengthBytes(x, 32),
                    Y = ToFixedLengthBytes(y, 32)
                }
            };

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] CompressPublicKey(byte[] uncompressedPublicKey)
    {
        var y = uncompressedPublicKey[64];
        var result = new byte[33];
        result[0] = (byte)((y & 1) == 1 ? 0x03 : 0x02);
        Array.Copy(uncompressedPublicKey, 1, result, 1, 32);

        return result;
    }

    private static BigInteger DecompressY(BigInteger x, bool isOdd)
    {
        var xCubed = Mod(BigInteger.ModPow(x, 3, P256Prime));
        var rhs = Mod(xCubed - (3 * x) + P256B);
        var y = BigInteger.ModPow(rhs, (P256Prime + 1) / 4, P256Prime);

        if (Mod(BigInteger.ModPow(y, 2, P256Prime) - rhs) != BigInteger.Zero)
        {
            throw new CryptographicException("Public key point is not on the P-256 curve.");
        }

        if (y.IsEven == isOdd)
        {
            y = P256Prime - y;
        }

        return y;
    }

    private static bool TryReadSignature(string value, out byte[] signature)
    {
        signature = [];
        var normalized = NormalizeHex(value);

        if (normalized.Length == 128 && IsHex(normalized))
        {
            signature = HexToBytes(normalized);
            return true;
        }

        try
        {
            signature = Convert.FromBase64String(value.Trim());

            return signature.Length == 64;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string GetScriptHashFromPublicKey(byte[] compressedPublicKey)
    {
        var verificationScript = HexToBytes($"0c21{ToHex(compressedPublicKey)}41{NeoCheckSigInteropCode}");
        var hash160 = Ripemd160(SHA256.HashData(verificationScript));

        return ToHex(hash160.Reverse().ToArray());
    }

    private static string GetAddressFromScriptHash(string scriptHash)
    {
        var hash = HexToBytes(scriptHash).Reverse().ToArray();
        var payload = new[] { NeoN3AddressVersion }.Concat(hash).ToArray();
        var checksum = SHA256.HashData(SHA256.HashData(payload))[..4];

        return Base58Encode(payload.Concat(checksum).ToArray());
    }

    private static byte[] Ripemd160(byte[] value)
    {
        return Ripemd160Managed.Hash(value);
    }

    private static string Base58Encode(byte[] value)
    {
        var intData = new BigInteger(value, isUnsigned: true, isBigEndian: true);
        var builder = new StringBuilder();

        while (intData > 0)
        {
            intData = BigInteger.DivRem(intData, 58, out var remainder);
            builder.Insert(0, Base58Alphabet[(int)remainder]);
        }

        foreach (var item in value)
        {
            if (item != 0)
            {
                break;
            }

            builder.Insert(0, Base58Alphabet[0]);
        }

        return builder.ToString();
    }

    private static string? NormalizeOptionalHex(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : NormalizeHex(value);
    }

    private static string NormalizeHex(string value)
    {
        var trimmed = value.Trim();

        var normalized = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? trimmed[2..]
            : trimmed;

        return normalized.ToLowerInvariant();
    }

    private static bool IsHex(string value)
    {
        return value.All(Uri.IsHexDigit);
    }

    private static byte[] HexToBytes(string hex)
    {
        return Convert.FromHexString(NormalizeHex(hex));
    }

    private static string ToHex(byte[] value)
    {
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    private static BigInteger FromHex(string hex)
    {
        return FromBytes(HexToBytes(hex));
    }

    private static BigInteger FromBytes(byte[] value)
    {
        return new BigInteger(value, isUnsigned: true, isBigEndian: true);
    }

    private static byte[] ToFixedLengthBytes(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);

        if (bytes.Length == length)
        {
            return bytes;
        }

        if (bytes.Length > length)
        {
            return bytes[^length..];
        }

        var padded = new byte[length];
        Array.Copy(bytes, 0, padded, length - bytes.Length, bytes.Length);

        return padded;
    }

    private static BigInteger Mod(BigInteger value)
    {
        var result = value % P256Prime;

        return result.Sign < 0 ? result + P256Prime : result;
    }

    private static WalletSignatureVerificationResult Fail(string error)
    {
        return new WalletSignatureVerificationResult(false, error);
    }

    private static class Ripemd160Managed
    {
        private static readonly int[] LeftMessageOrder =
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
            3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
            1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
            4, 0, 5, 9, 7, 12, 2, 10, 14, 1, 3, 8, 11, 6, 15, 13
        ];

        private static readonly int[] RightMessageOrder =
        [
            5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
            6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
            15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
            8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
            12, 15, 10, 4, 1, 5, 8, 7, 6, 2, 13, 14, 0, 3, 9, 11
        ];

        private static readonly int[] LeftRotations =
        [
            11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
            7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
            11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
            11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
            9, 15, 5, 11, 6, 8, 13, 12, 5, 12, 13, 14, 11, 8, 5, 6
        ];

        private static readonly int[] RightRotations =
        [
            8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
            9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
            9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
            15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
            8, 5, 12, 9, 12, 5, 14, 6, 8, 13, 6, 5, 15, 13, 11, 11
        ];

        public static byte[] Hash(byte[] value)
        {
            var padded = Pad(value);
            uint h0 = 0x67452301;
            uint h1 = 0xefcdab89;
            uint h2 = 0x98badcfe;
            uint h3 = 0x10325476;
            uint h4 = 0xc3d2e1f0;

            Span<uint> words = stackalloc uint[16];
            for (var offset = 0; offset < padded.Length; offset += 64)
            {
                for (var index = 0; index < 16; index++)
                {
                    words[index] = BitConverter.ToUInt32(padded, offset + (index * 4));
                }

                Compress(words, ref h0, ref h1, ref h2, ref h3, ref h4);
            }

            var result = new byte[20];
            WriteLittleEndian(result, 0, h0);
            WriteLittleEndian(result, 4, h1);
            WriteLittleEndian(result, 8, h2);
            WriteLittleEndian(result, 12, h3);
            WriteLittleEndian(result, 16, h4);

            return result;
        }

        private static byte[] Pad(byte[] value)
        {
            var bitLength = (ulong)value.Length * 8;
            var paddedLength = value.Length + 1;

            while (paddedLength % 64 != 56)
            {
                paddedLength++;
            }

            var padded = new byte[paddedLength + 8];
            Array.Copy(value, padded, value.Length);
            padded[value.Length] = 0x80;

            for (var index = 0; index < 8; index++)
            {
                padded[paddedLength + index] = (byte)(bitLength >> (8 * index));
            }

            return padded;
        }

        private static void Compress(
            Span<uint> words,
            ref uint h0,
            ref uint h1,
            ref uint h2,
            ref uint h3,
            ref uint h4)
        {
            unchecked
            {
                var al = h0;
                var bl = h1;
                var cl = h2;
                var dl = h3;
                var el = h4;
                var ar = h0;
                var br = h1;
                var cr = h2;
                var dr = h3;
                var er = h4;

                for (var index = 0; index < 80; index++)
                {
                    var temp = RotateLeft(
                        al + F(index, bl, cl, dl) + words[LeftMessageOrder[index]] + LeftConstant(index),
                        LeftRotations[index]) + el;
                    al = el;
                    el = dl;
                    dl = RotateLeft(cl, 10);
                    cl = bl;
                    bl = temp;

                    temp = RotateLeft(
                        ar + F(79 - index, br, cr, dr) + words[RightMessageOrder[index]] + RightConstant(index),
                        RightRotations[index]) + er;
                    ar = er;
                    er = dr;
                    dr = RotateLeft(cr, 10);
                    cr = br;
                    br = temp;
                }

                var combined = h1 + cl + dr;
                h1 = h2 + dl + er;
                h2 = h3 + el + ar;
                h3 = h4 + al + br;
                h4 = h0 + bl + cr;
                h0 = combined;
            }
        }

        private static uint F(int round, uint x, uint y, uint z)
        {
            return round switch
            {
                < 16 => x ^ y ^ z,
                < 32 => (x & y) | (~x & z),
                < 48 => (x | ~y) ^ z,
                < 64 => (x & z) | (y & ~z),
                _ => x ^ (y | ~z)
            };
        }

        private static uint LeftConstant(int round)
        {
            return round switch
            {
                < 16 => 0x00000000,
                < 32 => 0x5a827999,
                < 48 => 0x6ed9eba1,
                < 64 => 0x8f1bbcdc,
                _ => 0xa953fd4e
            };
        }

        private static uint RightConstant(int round)
        {
            return round switch
            {
                < 16 => 0x50a28be6,
                < 32 => 0x5c4dd124,
                < 48 => 0x6d703ef3,
                < 64 => 0x7a6d76e9,
                _ => 0x00000000
            };
        }

        private static uint RotateLeft(uint value, int bits)
        {
            return (value << bits) | (value >> (32 - bits));
        }

        private static void WriteLittleEndian(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }
    }
}

public sealed record WalletSignatureVerificationResult(
    bool IsValid,
    string Error,
    string? PublicKey = null,
    string? Address = null,
    string? ScriptHash = null)
{
    public static WalletSignatureVerificationResult Valid { get; } = new(true, string.Empty);
}
