using Microsoft.AspNetCore.Diagnostics;
using ZaloAi.Core.Errors;

namespace ZaloAi.Api.Common;

/// <summary>
/// Biến exception thành ProblemDetails (RFC 9457) kèm "code" ổn định cho FE.
/// Lỗi không lường trước → 500 với message chung, chi tiết chỉ nằm trong log/Sentry.
/// </summary>
internal sealed partial class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title) = exception switch
        {
            InvalidCredentialsException e => (StatusCodes.Status401Unauthorized, e.Code, e.Message),
            ForbiddenException e => (StatusCodes.Status403Forbidden, e.Code, e.Message),
            NotFoundException e => (StatusCodes.Status404NotFound, e.Code, e.Message),
            ConflictException e => (StatusCodes.Status409Conflict, e.Code, e.Message),
            InvalidInputException e => (StatusCodes.Status400BadRequest, e.Code, e.Message),
            BadHttpRequestException e => (e.StatusCode, "bad_request", "Yêu cầu không hợp lệ."),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "Có lỗi xảy ra, vui lòng thử lại."),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title, Extensions = { ["code"] = code } },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Lỗi không xử lý được ở {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);
}
