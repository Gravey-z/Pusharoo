using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace backend.Services;

public sealed class ContractApiFilter : IExceptionFilter
{
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
