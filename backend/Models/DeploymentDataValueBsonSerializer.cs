using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace backend.Models;

public sealed class DeploymentDataValueBsonSerializer : SerializerBase<DeploymentDataValue>
{
    public override void Serialize(
        BsonSerializationContext context,
        BsonSerializationArgs args,
        DeploymentDataValue value)
    {
        var writer = context.Writer;
        writer.WriteStartDocument();
        writer.WriteName("type");
        writer.WriteString(value.Type);
        writer.WriteName("value");
        WriteJsonElement(writer, value.Value);
        writer.WriteEndDocument();
    }

    public override DeploymentDataValue Deserialize(
        BsonDeserializationContext context,
        BsonDeserializationArgs args)
    {
        var document = BsonDocumentSerializer.Instance.Deserialize(context, args);
        if (!document.TryGetValue("type", out var typeValue) || !typeValue.IsString
            || !document.TryGetValue("value", out var bsonValue))
        {
            throw new BsonSerializationException("Stored deployment data must contain a string type and a value.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteBsonValue(writer, bsonValue);
        }
        using var json = JsonDocument.Parse(stream.ToArray());
        return new DeploymentDataValue(typeValue.AsString, json.RootElement.Clone());
    }

    private static void WriteJsonElement(IBsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartDocument();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WriteName(property.Name);
                    WriteJsonElement(writer, property.Value);
                }
                writer.WriteEndDocument();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteJsonElement(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteString(value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.True:
                writer.WriteBoolean(true);
                break;
            case JsonValueKind.False:
                writer.WriteBoolean(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNull();
                break;
            case JsonValueKind.Number when value.TryGetInt32(out var intValue):
                writer.WriteInt32(intValue);
                break;
            case JsonValueKind.Number when value.TryGetInt64(out var longValue):
                writer.WriteInt64(longValue);
                break;
            case JsonValueKind.Number:
                writer.WriteDouble(value.GetDouble());
                break;
            default:
                throw new BsonSerializationException($"Unsupported deployment data JSON value '{value.ValueKind}'.");
        }
    }

    private static void WriteBsonValue(Utf8JsonWriter writer, BsonValue value)
    {
        if (value.IsBsonNull)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.IsBsonDocument)
        {
            writer.WriteStartObject();
            foreach (var element in value.AsBsonDocument.Elements)
            {
                writer.WritePropertyName(element.Name);
                WriteBsonValue(writer, element.Value);
            }
            writer.WriteEndObject();
            return;
        }

        if (value.IsBsonArray)
        {
            writer.WriteStartArray();
            foreach (var item in value.AsBsonArray) WriteBsonValue(writer, item);
            writer.WriteEndArray();
            return;
        }

        switch (value.BsonType)
        {
            case BsonType.String: writer.WriteStringValue(value.AsString); break;
            case BsonType.Boolean: writer.WriteBooleanValue(value.AsBoolean); break;
            case BsonType.Int32: writer.WriteNumberValue(value.AsInt32); break;
            case BsonType.Int64: writer.WriteNumberValue(value.AsInt64); break;
            case BsonType.Double: writer.WriteNumberValue(value.AsDouble); break;
            default:
                throw new BsonSerializationException($"Unsupported stored deployment data BSON type '{value.BsonType}'.");
        }
    }
}
