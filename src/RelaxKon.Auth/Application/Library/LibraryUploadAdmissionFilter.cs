using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RelaxKon.Auth.Application.Library;

// Resource filters run after authorization but before MVC multipart model binding.
// Reject excess uploads before they can consume buffering memory/temp disk.
public sealed class LibraryUploadAdmissionFilter(LibraryAdmission admission) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!await admission.Uploads.WaitAsync(0, context.HttpContext.RequestAborted))
        {
            context.HttpContext.Response.Headers.RetryAfter = "1";
            context.Result = new ObjectResult(new ProblemDetails { Status = 429, Title = "Library is busy. Please retry shortly." })
                { StatusCode = 429 };
            return;
        }
        try { await next(); }
        finally { admission.Uploads.Release(); }
    }
}
