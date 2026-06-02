using backend.Models;
using backend.Services;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class ProjectRepository(MongoDbContext db) : IProjectRepository
{
    public async Task InsertAsync(ProjectDocument project, CancellationToken cancellationToken)
    {
        await db.Projects.InsertOneAsync(project, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectDocument>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await db.Projects
            .Find(Builders<ProjectDocument>.Filter.Empty)
            .SortByDescending(project => project.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ProjectDocument?> GetByIdAsync(string projectId, CancellationToken cancellationToken)
    {
        return await db.Projects
            .Find(project => project.Id == projectId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
