using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>Maps domain <see cref="Result"/>/<see cref="Error"/> values to RFC 9457 Problem Details.</summary>
public static class ResultMapping
{
    public static IResult ToProblem(this Error error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Data is not null)
        {
            extensions["data"] = error.Data;
        }

        if (error.Type == ErrorType.Validation)
        {
            return TypedResults.ValidationProblem(
                error.Fields?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string[]>(),
                detail: error.Message,
                title: "Please correct the highlighted fields.",
                extensions: extensions);
        }

        var (status, title) = error.Type switch
        {
            ErrorType.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, "Conflict"),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, "Not allowed"),
            ErrorType.Unauthorized => (StatusCodes.Status401Unauthorized, "Sign-in required"),
            ErrorType.BusinessRule => (StatusCodes.Status422UnprocessableEntity, "Cannot complete this action"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
        };

        return TypedResults.Problem(detail: error.Message, statusCode: status, title: title, extensions: extensions);
    }

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error!.ToProblem();

    public static IResult ToHttpResult(this Result result, Func<IResult> onSuccess) =>
        result.IsSuccess ? onSuccess() : result.Error!.ToProblem();

    public static IResult ToOk<T>(this Result<T> result) => result.ToHttpResult(value => TypedResults.Ok(value));

    public static IResult ToNoContent(this Result result) => result.ToHttpResult(TypedResults.NoContent);

    public static IResult ToCreated<T>(this Result<T> result, Func<T, string> location) =>
        result.ToHttpResult(value => TypedResults.Created(location(value), value));
}
