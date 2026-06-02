namespace backend.Options;

public sealed class MongoDbOptions
{
    public const string SectionName = "MongoDb";

    public string ConnectionString { get; init; } = "mongodb://localhost:27017";

    public string DatabaseName { get; init; } = "Pusharoo";
}
