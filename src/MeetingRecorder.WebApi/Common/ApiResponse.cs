using System.Text.Json.Serialization;

namespace MeetingRecorder.WebApi.Common;

/// <summary>
/// Standard API envelope used by every endpoint, including errors:
/// { success, message, data, errors, statusCode }
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Errors { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }
    public int StatusCode { get; set; }

    public static ApiResponse<T> Ok(T data, string? message = null, int statusCode = 200)
        => new() { Success = true, Data = data, Message = message, StatusCode = statusCode };

    public static ApiResponse<T> Fail(string message, int statusCode, string[]? errors = null, string? errorCode = null)
        => new() { Success = false, Message = message, Errors = errors, ErrorCode = errorCode, StatusCode = statusCode };
}

public static class ApiResponseFactory
{
    public static ApiResponse<bool> Ok(string? message = null) => ApiResponse<bool>.Ok(true, message);

    public static ApiResponse<object?> Fail(string message, int statusCode, string[]? errors = null, string? errorCode = null)
        => ApiResponse<object?>.Fail(message, statusCode, errors, errorCode);
}
