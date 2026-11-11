using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
namespace UserManagement.Api.Common;

// Translate failures without exposing exception messages, connection strings or submitted values.
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    // Run the request and convert known infrastructure/domain failures to safe problems.
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var status = exception is ApiException domain ? domain.Status
                : exception is MongoException or TimeoutException ? 503 : 500;
            var code = exception is ApiException api ? api.Code : status == 503 ? "database_unavailable" : "internal_error";
            var detail = exception is ApiException safe ? safe.Message : status == 503 ? "Storage is unavailable. Try again." : "The request could not be completed.";
            if (exception is not ApiException) logger.LogWarning("Request failed: {FailureType}; trace {TraceId}", exception.GetType().Name, context.TraceIdentifier);
            if (!context.Response.HasStarted) await WriteAsync(context, status, code, detail);
        }
    }

    // Emit the shared error contract with stable extension keys.
    public static async Task WriteAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = status switch { 400 => "Validation failed", 401 => "Authentication required", 403 => "Access denied", 404 => "Not found", 409 => "Conflict", 503 => "Service unavailable", _ => "Request failed" },
            Detail = detail, Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
        }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
