namespace HyFib.HealthCare360.SharedKernel;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    BusinessRule,
    Forbidden,
    Unauthorized,
    Unexpected,
}

/// <summary>
/// A domain/application error. Endpoints translate it to RFC 9457 Problem Details
/// (Validation → 400, NotFound → 404, Conflict → 409, BusinessRule → 422, Forbidden → 403).
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.BusinessRule)
{
    /// <summary>Field-level validation messages keyed by camelCase property name.</summary>
    public IReadOnlyDictionary<string, string[]>? Fields { get; init; }

    /// <summary>Optional machine-readable payload (for example duplicate candidates).</summary>
    public object? Data { get; init; }

    public static Error NotFound(string entity, object? id = null) =>
        new($"{ToCode(entity)}.not_found",
            id is null ? $"{entity} was not found." : $"{entity} '{id}' was not found.",
            ErrorType.NotFound);

    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fields = null) =>
        new(code, message, ErrorType.Validation) { Fields = fields };

    public static Error Field(string field, string message) =>
        Validation("validation", message, new Dictionary<string, string[]> { [field] = [message] });

    public static Error Conflict(string code, string message, object? data = null) =>
        new(code, message, ErrorType.Conflict) { Data = data };

    public static Error Rule(string code, string message) => new(code, message, ErrorType.BusinessRule);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error ConcurrencyConflict { get; } = new(
        "concurrency",
        "This record was changed by someone else. Reload it and try again.",
        ErrorType.Conflict);

    private static string ToCode(string entity) =>
        string.Concat(entity.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

/// <summary>Outcome of an operation without a value.</summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => new(error);
}

/// <summary>Outcome of an operation that produces a value on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value) : base(null) => _value = value;

    private Result(Error error) : base(error)
    {
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error!.Code}).");

    public static Result<T> Ok(T value) => new(value);

    public static Result<T> Fail(Error error) => new(error);

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);
}
