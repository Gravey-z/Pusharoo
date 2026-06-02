using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed class NeoContractManifest
{
    [BsonElement("name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("groups")]
    [JsonPropertyName("groups")]
    public List<BsonDocument> Groups { get; set; } = [];

    [BsonElement("features")]
    [JsonPropertyName("features")]
    public BsonDocument Features { get; set; } = new();

    [BsonElement("supportedstandards")]
    [JsonPropertyName("supportedstandards")]
    public string[] SupportedStandards { get; set; } = [];

    [BsonElement("abi")]
    [JsonPropertyName("abi")]
    public NeoAbi Abi { get; set; } = new();

    [BsonElement("permissions")]
    [JsonPropertyName("permissions")]
    public List<NeoPermission> Permissions { get; set; } = [];

    [BsonElement("trusts")]
    [JsonPropertyName("trusts")]
    public List<BsonValue> Trusts { get; set; } = [];

    [BsonElement("extra")]
    [JsonPropertyName("extra")]
    public BsonDocument Extra { get; set; } = new();
}

public sealed class NeoAbi
{
    [BsonElement("methods")]
    [JsonPropertyName("methods")]
    public List<NeoMethod> Methods { get; set; } = [];

    [BsonElement("events")]
    [JsonPropertyName("events")]
    public List<NeoEvent> Events { get; set; } = [];
}

public sealed class NeoMethod
{
    [BsonElement("name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("parameters")]
    [JsonPropertyName("parameters")]
    public List<NeoParameter> Parameters { get; set; } = [];

    [BsonElement("returntype")]
    [JsonPropertyName("returntype")]
    public string ReturnType { get; set; } = string.Empty;

    [BsonElement("offset")]
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [BsonElement("safe")]
    [JsonPropertyName("safe")]
    public bool Safe { get; set; }
}

public sealed class NeoEvent
{
    [BsonElement("name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("parameters")]
    [JsonPropertyName("parameters")]
    public List<NeoParameter> Parameters { get; set; } = [];
}

public sealed class NeoParameter
{
    [BsonElement("name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("type")]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

public sealed class NeoPermission
{
    [BsonElement("contract")]
    [JsonPropertyName("contract")]
    public BsonValue Contract { get; set; } = BsonNull.Value;

    [BsonElement("methods")]
    [JsonPropertyName("methods")]
    public BsonValue Methods { get; set; } = BsonNull.Value;
}
