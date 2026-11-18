using Microsoft.AspNetCore.Mvc.Filters;

namespace StudentRecords.Api;
// Keep malformed MVC-bound bodies in the existing safe ProblemDetails pipeline.
public sealed class RequestValidationFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            throw new ApiException(400, "Dữ liệu không hợp lệ.");
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
