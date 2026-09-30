using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using backend.Models;

namespace backend.Services;

public sealed class DeploymentDataService(NeoWalletAddressValidator addressValidator)
{
    public const string FormatVersion = "pusharoo.deployment-data.v1";
    public const int MaximumNestingDepth = 8;
    public const int MaximumNodeCount = 128;
    public const int MaximumSerializedBytes = 16 * 1024;

    private static readonly BigInteger MinimumInteger = -BigInteger.Pow(2, 255);
    private static readonly BigInteger MaximumInteger = BigInteger.Pow(2, 255) - BigInteger.One;

    public NormalizedDeploymentData Normalize(DeploymentDataValue? input)
    {
        var nodes = 0;
        var normalized = NormalizeValue(input ?? new DeploymentDataValue("Any", JsonSerializer.SerializeToElement<object?>(null)), 0, ref nodes);
        var canonicalJson = SerializeCanonical(normalized);
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        if (bytes.Length > MaximumSerializedBytes)
        {
            throw new DeploymentDataValidationException($"Deployment data must be {MaximumSerializedBytes} bytes or smaller after normalization.");
        }

        var digestInput = Encoding.UTF8.GetBytes($"{FormatVersion}\n{canonicalJson}");
        var digest = Convert.ToHexString(SHA256.HashData(digestInput)).ToLowerInvariant();
        return new NormalizedDeploymentData(normalized, digest, FormatVersion);
    }

    private DeploymentDataValue NormalizeValue(DeploymentDataValue input, int depth, ref int nodes)
    {
        nodes++;
        if (nodes > MaximumNodeCount)
        {
            throw new DeploymentDataValidationException($"Deployment data cannot contain more than {MaximumNodeCount} values.");
        }
        if (depth > MaximumNestingDepth)
        {
            throw new DeploymentDataValidationException($"Deployment data cannot be nested more than {MaximumNestingDepth} levels.");
        }
        if (input.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new DeploymentDataValidationException("Deployment data value is required.");
        }

        return input.Type switch
        {
            "Any" => NormalizeNull(input),
            "String" => NormalizeString(input),
            "Boolean" => NormalizeBoolean(input),
            "Integer" => NormalizeInteger(input),
            "Hash160" => NormalizeHash160(input),
            "ByteArray" => NormalizeByteArray(input),
            "Array" => NormalizeArray(input, depth, ref nodes),
            _ => throw new DeploymentDataValidationException($"Deployment data type '{input.Type}' is not supported.")
        };
    }

    private static DeploymentDataValue NormalizeNull(DeploymentDataValue input)
    {
        if (input.Value.ValueKind != JsonValueKind.Null)
        {
            throw new DeploymentDataValidationException("Any deployment data currently supports only a null value.");
        }
        return new DeploymentDataValue("Any", JsonSerializer.SerializeToElement<object?>(null));
    }

    private static DeploymentDataValue NormalizeString(DeploymentDataValue input)
    {
        if (input.Value.ValueKind != JsonValueKind.String)
        {
            throw new DeploymentDataValidationException("String deployment data must have a string value.");
        }
        var value = input.Value.GetString() ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(value) > MaximumSerializedBytes)
        {
            throw new DeploymentDataValidationException($"Deployment data must be {MaximumSerializedBytes} bytes or smaller after normalization.");
        }
        return new DeploymentDataValue("String", JsonSerializer.SerializeToElement(value));
    }

    private static DeploymentDataValue NormalizeBoolean(DeploymentDataValue input)
    {
        if (input.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new DeploymentDataValidationException("Boolean deployment data must be true or false.");
        }
        return new DeploymentDataValue("Boolean", JsonSerializer.SerializeToElement(input.Value.GetBoolean()));
    }

    private static DeploymentDataValue NormalizeInteger(DeploymentDataValue input)
    {
        var value = input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString() : null;
        if (string.IsNullOrEmpty(value) || value.Length > 78
            || !BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            throw new DeploymentDataValidationException("Integer deployment data must be a decimal integer string.");
        }
        if (integer < MinimumInteger || integer > MaximumInteger)
        {
            throw new DeploymentDataValidationException("Integer deployment data must fit within Neo's signed 256-bit integer range.");
        }
        return new DeploymentDataValue("Integer", JsonSerializer.SerializeToElement(integer.ToString(CultureInfo.InvariantCulture)));
    }

    private DeploymentDataValue NormalizeHash160(DeploymentDataValue input)
    {
        if (input.Value.ValueKind != JsonValueKind.String)
        {
            throw new DeploymentDataValidationException("Hash160 deployment data must be a Neo N3 address or script hash.");
        }

        var value = input.Value.GetString()?.Trim() ?? string.Empty;
        if (value.Length > 64)
        {
            throw new DeploymentDataValidationException("Hash160 deployment data must be a valid Neo N3 wallet address or 20-byte script hash.");
        }
        var candidate = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (candidate.Length == 40 && candidate.All(Uri.IsHexDigit))
        {
            return new DeploymentDataValue("Hash160", JsonSerializer.SerializeToElement($"0x{candidate.ToLowerInvariant()}"));
        }

        var address = addressValidator.Validate(value);
        if (!address.IsValid)
        {
            throw new DeploymentDataValidationException("Hash160 deployment data must be a valid Neo N3 wallet address or 20-byte script hash.");
        }

        return new DeploymentDataValue("Hash160", JsonSerializer.SerializeToElement(address.ScriptHash.ToLowerInvariant()));
    }

    private static DeploymentDataValue NormalizeByteArray(DeploymentDataValue input)
    {
        if (input.Value.ValueKind != JsonValueKind.String)
        {
            throw new DeploymentDataValidationException("ByteArray deployment data must be an even-length hexadecimal string.");
        }

        var value = input.Value.GetString()?.Trim() ?? string.Empty;
        var candidate = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (candidate.Length > MaximumSerializedBytes * 2 || candidate.Length % 2 != 0 || !candidate.All(Uri.IsHexDigit))
        {
            throw new DeploymentDataValidationException("ByteArray deployment data must be an even-length hexadecimal string.");
        }
        return new DeploymentDataValue("ByteArray", JsonSerializer.SerializeToElement(candidate.ToLowerInvariant()));
    }

    private DeploymentDataValue NormalizeArray(DeploymentDataValue input, int depth, ref int nodes)
    {
        if (input.Value.ValueKind != JsonValueKind.Array)
        {
            throw new DeploymentDataValidationException("Array deployment data must contain an array value.");
        }

        var values = new List<DeploymentDataValue>();
        foreach (var element in input.Value.EnumerateArray())
        {
            values.Add(NormalizeValue(ParseValue(element), depth + 1, ref nodes));
        }
        var normalizedValue = JsonSerializer.SerializeToElement(values);
        return new DeploymentDataValue("Array", normalizedValue);
    }

    private static DeploymentDataValue ParseValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DeploymentDataValidationException("Each array entry must be a typed object with type and value properties.");
        }

        string? type = null;
        JsonElement data = default;
        var hasType = false;
        var hasValue = false;
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type" when !hasType:
                    hasType = true;
                    type = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    break;
                case "value" when !hasValue:
                    hasValue = true;
                    data = property.Value.Clone();
                    break;
                case "type":
                case "value":
                    throw new DeploymentDataValidationException($"Array entry property '{property.Name}' is duplicated.");
                default:
                    throw new DeploymentDataValidationException($"Unknown array entry property '{property.Name}'.");
            }
        }

        if (!hasType || string.IsNullOrWhiteSpace(type) || !hasValue)
        {
            throw new DeploymentDataValidationException("Each array entry must contain a non-empty type and a value.");
        }
        return new DeploymentDataValue(type, data);
    }

    private static string SerializeCanonical(DeploymentDataValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteCanonical(writer, value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, DeploymentDataValue value)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WritePropertyName("value");
        if (value.Type == "Array")
        {
            writer.WriteStartArray();
            foreach (var child in value.Value.EnumerateArray())
            {
                WriteCanonical(writer, ParseValue(child));
            }
            writer.WriteEndArray();
        }
        else
        {
            value.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
