using backend.Models;

namespace backend.Services;

public sealed record DeploymentWorkflowResult<T>(T? Value, int StatusCode, string? Error)
{
    public bool IsSuccess => Value is not null;

    public DeploymentResponse? RelatedDeployment { get; init; }

    public static DeploymentWorkflowResult<T> Created(T value) =>
        new(value, StatusCodes.Status201Created, null);

    public static DeploymentWorkflowResult<T> Rejected(int statusCode, string? error) =>
        new(default, statusCode, error);

    public static DeploymentWorkflowResult<T> Conflict(
        string? error,
        DeploymentResponse? deployment = null
    ) => new(default, StatusCodes.Status409Conflict, error) { RelatedDeployment = deployment };

    public static DeploymentWorkflowResult<T> Success(T value) =>
        new(value, StatusCodes.Status200OK, null);

    public static DeploymentWorkflowResult<T> NotFound(string error) =>
        new(default, StatusCodes.Status404NotFound, error);

    public static DeploymentWorkflowResult<T> BadRequest(
        string? error,
        DeploymentResponse? deployment = null
    ) => new(default, StatusCodes.Status400BadRequest, error) { RelatedDeployment = deployment };

    public static DeploymentWorkflowResult<T> Forbidden(string error) =>
        new(default, StatusCodes.Status403Forbidden, error);

    public static DeploymentWorkflowResult<T> Failure<TSource>(
        DeploymentWorkflowResult<TSource> source
    ) =>
        new(default, source.StatusCode, source.Error)
        {
            RelatedDeployment = source.RelatedDeployment,
        };
}
