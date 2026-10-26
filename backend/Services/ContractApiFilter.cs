using backend.Controllers;
using backend.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace backend.Services;

public sealed class ContractApiFilter(IOptions<MultiContractOptions> options)
    : IAsyncResourceFilter,
        IExceptionFilter
{
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next
    )
    {
        var controllerType = (
            context.ActionDescriptor as ControllerActionDescriptor
        )?.ControllerTypeInfo.AsType();
        var isLegacyWorkflow =
            controllerType == typeof(ProjectArtifactsController)
            || controllerType == typeof(ProjectDeploymentsController);

        if (
            options.Value.WritesEnabled
            && isLegacyWorkflow
            && !HttpMethods.IsGet(context.HttpContext.Request.Method)
        )
        {
            context.Result = new ObjectResult(
                new
                {
                    code = "contracts_not_ready",
                    error = "Artifact and deployment writes require the remaining multi-contract implementation phases.",
                }
            )
            {
                StatusCode = 503,
            };
            return;
        }

        await next();
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ContractOperationException error)
            return;

        context.Result = new ObjectResult(new { code = error.Code, error = error.Message })
        {
            StatusCode = error.StatusCode,
        };
        context.ExceptionHandled = true;
    }
}
