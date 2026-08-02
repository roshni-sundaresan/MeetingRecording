namespace MeetingRecorder.Application.Exceptions;

/// <summary>Base exception for expected application failures. Maps to 4xx in the global middleware.</summary>
public class AppException : Exception
{
    public int StatusCode { get; }

    public AppException(string message, int statusCode = 400) : base(message)
    {
        StatusCode = statusCode;
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
    public ConflictException(string message) : base(message, 409)
    {
    }
}

public sealed class ValidationException : AppException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.", 400)
    {
        Errors = errors;
    }
}
