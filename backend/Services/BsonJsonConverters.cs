using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;

namespace backend.Services;

public sealed class BsonDocumentJsonConverter : JsonConverter<BsonDocument>
{
    public override BsonDocument Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = BsonValueJsonConverter.ReadBsonValue(ref reader);

        return value.IsBsonDocument ? value.AsBsonDocument : new BsonDocument();
    }

    public override void Write(Utf8JsonWriter writer, BsonDocument value, JsonSerializerOptions options)
    {
        BsonValueJsonConverter.WriteBsonValue(writer, value);
    }
}

public sealed class BsonValueJsonConverter : JsonConverter<BsonValue>
{
    public override BsonValue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return ReadBsonValue(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, BsonValue value, JsonSerializerOptions options)
    {
        WriteBsonValue(writer, value);
    }

    public static BsonValue ReadBsonValue(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.StartObject => ReadDocument(ref reader),
            JsonTokenType.StartArray => ReadArray(ref reader),
            JsonTokenType.String => new BsonString(reader.GetString() ?? string.Empty),
            JsonTokenType.Number when reader.TryGetInt64(out var longValue) => new BsonInt64(longValue),
            JsonTokenType.Number => new BsonDouble(reader.GetDouble()),
            JsonTokenType.True => BsonBoolean.True,
            JsonTokenType.False => BsonBoolean.False,
            JsonTokenType.Null => BsonNull.Value,
            _ => throw new JsonException($"Unexpected JSON token '{reader.TokenType}'.")
        };
    }

    public static void WriteBsonValue(Utf8JsonWriter writer, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                writer.WriteStartObject();
                foreach (var element in value.AsBsonDocument)
                {
                    writer.WritePropertyName(element.Name);
                    WriteBsonValue(writer, element.Value);
                }
                writer.WriteEndObject();
                break;
            case BsonType.Array:
                writer.WriteStartArray();
                foreach (var item in value.AsBsonArray)
                {
                    WriteBsonValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            case BsonType.Boolean:
                writer.WriteBooleanValue(value.AsBoolean);
                break;
            case BsonType.Double:
                writer.WriteNumberValue(value.AsDouble);
                break;
            case BsonType.Int32:
                writer.WriteNumberValue(value.AsInt32);
                break;
            case BsonType.Int64:
                writer.WriteNumberValue(value.AsInt64);
                break;
            case BsonType.Null:
                writer.WriteNullValue();
                break;
            case BsonType.String:
                writer.WriteStringValue(value.AsString);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static BsonDocument ReadDocument(ref Utf8JsonReader reader)
    {
        var document = new BsonDocument();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return document;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a JSON property name.");
            }

            var propertyName = reader.GetString() ?? string.Empty;
            reader.Read();
            document[propertyName] = ReadBsonValue(ref reader);
        }

        throw new JsonException("Unexpected end of JSON object.");
    }

    private static BsonArray ReadArray(ref Utf8JsonReader reader)
    {
        var array = new BsonArray();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return array;
            }

            array.Add(ReadBsonValue(ref reader));
        }

        throw new JsonException("Unexpected end of JSON array.");
    }
}
