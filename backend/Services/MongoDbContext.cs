using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace backend.Services;

public sealed class MongoDbContext
{
    public MongoDbContext(IOptions<MongoDbOptions> options)
    {
        var mongoOptions = options.Value;
        var client = new MongoClient(mongoOptions.ConnectionString);
        var database = client.GetDatabase(mongoOptions.DatabaseName);

        Projects = database.GetCollection<ProjectDocument>("projects");
        ContractArtifacts = database.GetCollection<ArtifactDocument>("contractArtifacts");
    }

    public IMongoCollection<ProjectDocument> Projects { get; }

    public IMongoCollection<ArtifactDocument> ContractArtifacts { get; }
}
