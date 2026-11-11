using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using Xunit;

namespace UserManagement.Tests;

public sealed class CommonTests
{
    [Theory]
    [InlineData("Mongo:Uri", null)]
    [InlineData("Mongo:Uri", "")]
    [InlineData("Mongo:Uri", "   ")]
    [InlineData("Mongo:Database", null)]
    [InlineData("Mongo:Database", "")]
    [InlineData("Mongo:Database", "   ")]
    public void Mongo_missing_required_settings_refuses_context_creation(string key, string? value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = "mongodb://127.0.0.1:27017", ["Mongo:Database"] = "umm_test_config"
        }).Build();
        config[key] = value;

        var error = Assert.Throws<InvalidOperationException>(() => new MongoContext(config));

        Assert.Equal("Configure Mongo__Uri and Mongo__Database.", error.Message);
    }

    [Theory]
    [InlineData("Jwt:Secret", null)]
    [InlineData("Jwt:Secret", "")]
    [InlineData("Jwt:Secret", "too-short")]
    [InlineData("Jwt:Issuer", null)]
    [InlineData("Jwt:Issuer", "   ")]
    [InlineData("Jwt:Audience", null)]
    [InlineData("Jwt:Audience", "   ")]
    [InlineData("Jwt:ExpiryMinutes", null)]
    [InlineData("Jwt:ExpiryMinutes", "0")]
    [InlineData("Jwt:ExpiryMinutes", "29")]
    [InlineData("Jwt:ExpiryMinutes", "31")]
    public void Invalid_signing_settings_refuse_token_service_creation(string key, string? value)
    {
        var config = ValidJwtConfig();
        config[key] = value;

        Assert.Throws<InvalidOperationException>(() => new TokenService(config));
    }

    [Theory]
    [InlineData("CHANGE-ME")]
    [InlineData("DEV-SECRET")]
    public void Long_placeholder_secrets_are_rejected_before_tokens_can_be_issued(string placeholder)
    {
        var config = ValidJwtConfig();
        config["Jwt:Secret"] = new string('x', 64) + placeholder;

        Assert.Throws<InvalidOperationException>(() => new TokenService(config));
    }

    [Fact]
    public void Secret_one_byte_below_minimum_is_rejected_before_tokens_can_be_issued()
    {
        var config = ValidJwtConfig();
        config["Jwt:Secret"] = new string('x', 63);

        Assert.Throws<InvalidOperationException>(() => new TokenService(config));
    }

    [Fact]
    public async Task Middleware_success_preserves_downstream_status_headers_and_body()
    {
        var context = NewContext();
        var middleware = new ErrorMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = 202;
            ctx.Response.Headers["X-Result"] = "accepted";
            await ctx.Response.WriteAsync("downstream result");
        }, NullLogger<ErrorMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal("accepted", context.Response.Headers["X-Result"].ToString());
        Assert.Equal("downstream result", ReadBody(context));
    }

    [Theory]
    [InlineData(400, "Validation failed")]
    [InlineData(401, "Authentication required")]
    [InlineData(403, "Access denied")]
    [InlineData(404, "Not found")]
    [InlineData(409, "Conflict")]
    [InlineData(422, "Request failed")]
    public async Task Middleware_domain_errors_keep_safe_detail_code_and_trace(int status, string title)
    {
        var context = NewContext();
        var middleware = Failing(new ApiException(status, "domain_code", "Safe domain detail."));

        await middleware.InvokeAsync(context);

        AssertProblem(context, status, title, "domain_code", "Safe domain detail.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Middleware_mongo_and_timeout_failures_return_safe_503_without_exception_details(bool mongo)
    {
        var context = NewContext();
        const string sensitive = "mongodb://private-user:private-password@private-host submitted-secret";
        Exception failure = mongo ? new MongoException(sensitive) : new TimeoutException(sensitive);

        await Failing(failure).InvokeAsync(context);

        AssertProblem(context, 503, "Service unavailable", "database_unavailable", "Storage is unavailable. Try again.");
        Assert.DoesNotContain(sensitive, ReadBody(context));
        Assert.DoesNotContain(failure.GetType().Name, ReadBody(context));
    }

    [Fact]
    public async Task Middleware_unexpected_failure_returns_safe_500_without_exception_details()
    {
        var context = NewContext();
        const string sensitive = "submitted-password private-connection-string";

        await Failing(new InvalidOperationException(sensitive)).InvokeAsync(context);

        AssertProblem(context, 500, "Request failed", "internal_error", "The request could not be completed.");
        Assert.DoesNotContain(sensitive, ReadBody(context));
        Assert.DoesNotContain(nameof(InvalidOperationException), ReadBody(context));
    }

    [Fact]
    public async Task Middleware_request_abort_cancellation_does_not_write_or_replace_response()
    {
        var context = NewContext();
        using var abort = new CancellationTokenSource();
        context.RequestAborted = abort.Token;
        context.Response.StatusCode = 202;
        context.Response.Headers["X-Result"] = "untouched";
        await context.Response.WriteAsync("existing body");
        abort.Cancel();

        await Failing(new OperationCanceledException(abort.Token)).InvokeAsync(context);

        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal("untouched", context.Response.Headers["X-Result"].ToString());
        Assert.Null(context.Response.ContentType);
        Assert.Equal("existing body", ReadBody(context));
    }

    [Fact]
    public async Task Middleware_cancellation_without_request_abort_is_an_unexpected_safe_500()
    {
        var context = NewContext();

        await Failing(new OperationCanceledException("internal cancellation secret")).InvokeAsync(context);

        AssertProblem(context, 500, "Request failed", "internal_error", "The request could not be completed.");
        Assert.DoesNotContain("internal cancellation secret", ReadBody(context));
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("dependency")]
    [InlineData("unexpected")]
    public async Task Middleware_started_response_is_never_overwritten_after_failure(string kind)
    {
        var context = NewContext();
        var response = new StartedResponseFeature { StatusCode = 206, Body = context.Response.Body };
        response.Headers["Content-Type"] = "text/plain";
        response.Headers["X-Result"] = "started";
        context.Features.Set<IHttpResponseFeature>(response);
        await context.Response.WriteAsync("partial response");
        Assert.True(context.Response.HasStarted);
        Exception failure = kind switch
        {
            "domain" => new ApiException(409, "conflict", "Conflict."),
            "dependency" => new TimeoutException("private dependency"),
            _ => new InvalidOperationException("private failure")
        };

        await Failing(failure).InvokeAsync(context);

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.Equal("started", context.Response.Headers["X-Result"].ToString());
        Assert.Equal("partial response", ReadBody(context));
    }

    private static IConfigurationRoot ValidJwtConfig() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Secret"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)),
        ["Jwt:Issuer"] = "umm-tests", ["Jwt:Audience"] = "umm-test-browser", ["Jwt:ExpiryMinutes"] = "30"
    }).Build();

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "common-test-trace" };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static ErrorMiddleware Failing(Exception error) => new(_ => Task.FromException(error), NullLogger<ErrorMiddleware>.Instance);

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static void AssertProblem(HttpContext context, int status, string title, string code, string detail)
    {
        Assert.Equal(status, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        using var json = JsonDocument.Parse(ReadBody(context));
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(title, json.RootElement.GetProperty("title").GetString());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(detail, json.RootElement.GetProperty("detail").GetString());
        Assert.Equal("common-test-trace", json.RootElement.GetProperty("traceId").GetString());
    }

    // Simulate only the transport's committed state, not middleware behavior or storage.
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; }
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
