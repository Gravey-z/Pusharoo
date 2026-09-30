using System.Text.Json;
using System.Text.Json.Serialization;

namespace backend.Models;

[JsonConverter(typeof(DeploymentDataValueJsonConverter))]
public sealed record DeploymentDataValue(string Type, JsonElement Value);

public sealed record NormalizedDeploymentData(
    DeploymentDataValue Value,
    string Sha256,
    string FormatVersion);

public sealed class DeploymentDataValueJsonConverter : JsonConverter<DeploymentDataValue>
{
    public override DeploymentDataValue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Deployment data must be an object with type and value properties.");
        }

        string? type = null;
        JsonElement value = default;
        var hasType = false;
        var hasValue = false;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type" when !hasType:
                    hasType = true;
                    type = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                    break;
                case "value" when !hasValue:
                    hasValue = true;
                    value = property.Value.Clone();
                    break;
                case "type":
                case "value":
                    throw new JsonException($"Deployment data property '{property.Name}' is duplicated.");
                default:
                    throw new JsonException($"Unknown deployment data property '{property.Name}'.");
            }
        }

        if (!hasType || string.IsNullOrWhiteSpace(type) || !hasValue)
        {
            throw new JsonException("Deployment data must contain a non-empty type and a value.");
        }

        return new DeploymentDataValue(type, value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        DeploymentDataValue value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WritePropertyName("value");
        value.Value.WriteTo(writer);
        writer.WriteEndObject();
    }
}

public sealed class DeploymentDataValidationException(string message) : Exception(message);
