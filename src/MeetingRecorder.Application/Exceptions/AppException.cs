namespace MeetingRecorder.Application.Exceptions;

/// <summary>Base exception for expected application failures. Maps to 4xx in the global middleware.</summary>
public class AppException : Exception
{
    public int StatusCode { get; }

    /// <summary>Stable machine-readable code (e.g. EMAIL_TAKEN) surfaced on the API envelope.</summary>
    public string? ErrorCode { get; }

    public AppException(string message, int statusCode = 400, string? errorCode = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

public sealed class NotFoundException : AppException
{
    public NotFoundException(string entity, object key)
        : base($"{entity} with id '{key}' was not found.", 404)
    {
    }
}

public sealed class ConflictException : AppException
{
    public ConflictException(string message, string? errorCode = null) : base(message, 409, errorCode)
    {
    }
}

public sealed class ValidationException : AppException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.", 400, "VALIDATION_ERROR")
    {
        Errors = errors;
    }
}
