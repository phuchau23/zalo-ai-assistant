using FluentValidation;

namespace ZaloAi.Api.Common;

/// <summary>Chạy FluentValidation cho body kiểu <typeparamref name="T"/> trước khi vào handler. Sai → 400 kèm lỗi từng trường.</summary>
internal sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Thiếu dữ liệu.",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_input" });
        }

        var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
        if (!result.IsValid)
        {
            return TypedResults.ValidationProblem(
                result.ToDictionary(),
                title: "Dữ liệu không hợp lệ.",
                extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
        }

        return await next(context);
    }
}

internal static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder)
        where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();
}
