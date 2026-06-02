using backend.Models;
using backend.Repositories;
using MongoDB.Bson;

namespace backend.Services;

public sealed class ProjectService(IProjectRepository projects)
{
    public async Task<ProjectDocument> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = new ProjectDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        await projects.InsertAsync(project, cancellationToken);

        return project;
    }

    public async Task<IReadOnlyList<ProjectDocument>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await projects.GetAllAsync(cancellationToken);
    }

    public async Task<ProjectDocument?> GetByIdAsync(string projectId, CancellationToken cancellationToken)
    {
        return await projects.GetByIdAsync(projectId, cancellationToken);
    }
}
