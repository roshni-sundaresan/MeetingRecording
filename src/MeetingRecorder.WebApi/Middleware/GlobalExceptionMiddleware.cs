using System.Text.Json;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.WebApi.Common;

namespace MeetingRecorder.WebApi.Middleware;

/// <summary>
/// Global exception handler: logs every failure through Serilog and converts
/// exceptions into the standard <see cref="ApiResponse{T}"/> envelope.
/// Internal exception details are never leaked to clients.
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, message, errors) = exception switch
        {
            ValidationException ve => (ve.StatusCode, ve.Message, ve.Errors.SelectMany(e => e.Value).ToArray()),
            NotFoundException nf => (nf.StatusCode, nf.Message, null),
            ConflictException ce => (ce.StatusCode, ce.Message, null),
            AppException ae => (ae.StatusCode, ae.Message, null),
            FluentValidation.ValidationException fve => (400, "One or more validation errors occurred.",
                fve.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}").ToArray()),
            _ => (500, "An unexpected error occurred. Please try again later.", null)
        };

        if (statusCode >= 500)
        {
            _logger.LogError(exception, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);
        }
        else
        {
            _logger.LogWarning("Request failed ({StatusCode}) on {Method} {Path}: {Message}",
                statusCode, context.Request.Method, context.Request.Path, exception.Message);
        }

        if (context.Response.HasStarted)
        {
            _logger.LogWarning("Response already started; skipping error envelope for {Path}", context.Request.Path);
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var envelope = ApiResponseFactory.Fail(message, statusCode, errors,
            (exception as AppException)?.ErrorCode);
        await context.Response.WriteAsync(JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }
}
