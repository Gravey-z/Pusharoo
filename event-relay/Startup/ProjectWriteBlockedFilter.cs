using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Pusharoo.Retention;

namespace Pusharoo.EventRelay.Startup;

public sealed class ProjectWriteBlockedFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ProjectWriteBlockedException)
            return;
        context.Result = new ObjectResult(
            new
            {
                error = "The project is being cleaned up or has been deleted. Retry after checking the project.",
            }
        )
        {
            StatusCode = StatusCodes.Status409Conflict,
        };
        context.ExceptionHandled = true;
    }
}
