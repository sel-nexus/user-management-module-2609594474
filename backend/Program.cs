using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagement.Api.Common;
using UserManagement.Api.Auth;
using UserManagement.Api.Users;
using UserManagement.Api.Audit;

// Wire the modular API; configuration is supplied by appsettings or environment, never dotenv.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<MongoContext>();
builder.Services.AddSingleton<IUserRepository, MongoUserRepository>();
builder.Services.AddSingleton<MongoLogRepository>();
builder.Services.AddSingleton<IAuditWriter, AuditWriter>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<AuthenticationService>();
builder.Services.AddSingleton<UserLifecycleService>();
builder.Services.AddScoped<ActivePrincipalValidator>();
builder.Services.AddSingleton<DatabaseSetup>();
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
    o.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = action =>
{
    var problem = new ProblemDetails { Status = 400, Title = "Validation failed", Detail = "Check the submitted fields." };
    problem.Extensions["code"] = "validation_error";
    problem.Extensions["traceId"] = action.HttpContext.TraceIdentifier;
    problem.Extensions["errors"] = action.ModelState.Where(x => x.Value?.Errors.Count > 0).ToDictionary(x => x.Key, _ => new[] { "Invalid or missing value." });
    return new BadRequestObjectResult(problem);
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TokenService>((o, tokens) =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = tokens.ValidationParameters();
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = context => context.HttpContext.RequestServices.GetRequiredService<ActivePrincipalValidator>().ValidateAsync(context),
        OnChallenge = async context => { context.HandleResponse(); await ErrorMiddleware.WriteAsync(context.HttpContext, 401, "invalid_token", "Sign in with a valid active account."); },
        OnForbidden = context => ErrorMiddleware.WriteAsync(context.HttpContext, 403, "forbidden", "This account cannot perform that action.")
    };
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCors(o => o.AddPolicy("browser", policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0) policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var app = builder.Build();
app.UseMiddleware<ErrorMiddleware>();
app.Use(async (context, next) =>
{
    if ((context.Request.Path == "/api/login" || context.Request.Path == "/api/health") && context.Request.Query.Count > 0)
        throw new ApiException(400, "validation_error", "Unknown query parameter.");
    await next(context);
});
app.UseCors("browser");
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapControllers();
_ = app.Services.GetRequiredService<TokenService>(); // Fail startup on missing/default signing configuration.
await app.Services.GetRequiredService<DatabaseSetup>().InitializeAsync(CancellationToken.None);
await app.RunAsync();

// Expose the entry point for in-process real-database HTTP tests.
public partial class Program { }
