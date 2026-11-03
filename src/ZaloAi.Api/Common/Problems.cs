using Microsoft.AspNetCore.Mvc;

namespace ZaloAi.Api.Common;

internal static class Problems
{
    /// <summary>Ghi ProblemDetails trực tiếp, dùng ở chỗ không có exception (cookie auth events, rate limiter).</summary>
    public static async Task WriteAsync(HttpContext httpContext, int status, string code, string title)
    {
        httpContext.Response.StatusCode = status;
        await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Extensions = { ["code"] = code } },
        });
    }
}
