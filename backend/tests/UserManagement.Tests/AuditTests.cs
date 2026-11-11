using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Audit;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using UserManagement.Api.Profile;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Requery real BSON to prove event truth, identity absence, indexes and best-effort delivery.
public sealed class AuditTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();
    private AuditService Service => app.Services.GetRequiredService<AuditService>();
    private MongoLogRepository Repo => app.Services.GetRequiredService<MongoLogRepository>();
    private static ClaimsPrincipal Principal(UserDocument u) => new(new ClaimsIdentity([new Claim("sub", u.Id.ToString()), new Claim("role", u.Role)], "Bearer"));
    private MongoContext Unavailable() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build());
    [Fact]
    public async Task Empty_defaults_bounds_newest_ties_and_total_are_read_only_and_indexed()
    {
        var empty = await Service.ListAsync(new(), default); Assert.Empty(empty.Items); Assert.Equal(0, empty.Total); Assert.Equal(20, empty.Limit); Assert.Equal(0, empty.Skip);
        var now = DateTime.UtcNow.AddSeconds(-1);
        var logs = Enumerable.Range(0, 25).Select(i => new LogDocument { Timestamp = now.AddSeconds(i / 5), Action = "login", Status = i % 2 == 0 ? "success" : "failure" }).ToArray();
        foreach (var log in logs.Reverse()) await Repo.AppendAsync(log, default);
        var expected = logs.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id).ToArray();
        var first = await Service.ListAsync(new(), default); Assert.Equal(expected.Take(20).Select(x => x.Id.ToString()), first.Items.Select(x => x.Id)); Assert.Equal(25, first.Total);
        var next = await Service.ListAsync(new(20, 20), default); Assert.Equal(expected.Skip(20).Select(x => x.Id.ToString()), next.Items.Select(x => x.Id)); Assert.Equal(25, next.Total);
        Assert.Single((await Service.ListAsync(new(1), default)).Items); Assert.Equal(25, (await Service.ListAsync(new(100), default)).Items.Count);
        Assert.Empty((await Service.ListAsync(new(100, int.MaxValue), default)).Items); Assert.Equal(25, await Repo.CountAsync(null,null,null,default));
        var indexes = await (await app.Mongo.Logs.Indexes.ListAsync()).ToListAsync(); var index = Assert.Single(indexes, x => x["name"] == "timestamp_id"); Assert.Equal(new BsonDocument("timestamp", -1).Add("_id", -1), index["key"]); Assert.False(index.Contains("expireAfterSeconds"));
    }
    [Theory] [InlineData(0,0)] [InlineData(101,0)] [InlineData(20,-1)]
    public async Task Service_and_repository_reject_unbounded_pagination(int limit,int skip)
    {
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(() => Service.ListAsync(new(limit,skip),default))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(() => Repo.PageAsync(limit,skip,default))).Status);
        Assert.Equal(0,await Repo.CountAsync(null,null,null,default));
    }
    [Fact]
    public async Task Count_optional_filters_combine_in_Mongo_including_exact_boundary()
    {
        var now = DateTime.UtcNow; now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        foreach(var log in new[] { new LogDocument { Timestamp=now.AddDays(-1),Action="login",Status="success" },new LogDocument { Timestamp=now,Action="login",Status="failure" },new LogDocument { Timestamp=now,Action="update_user",Status="success" } }) await Repo.AppendAsync(log,default);
        Assert.Equal(3,await Repo.CountAsync(null,null,null,default)); Assert.Equal(2,await Repo.CountAsync(now,null,null,default)); Assert.Equal(2,await Repo.CountAsync(null,"login",null,default)); Assert.Equal(2,await Repo.CountAsync(null,null,"success",default)); Assert.Equal(1,await Repo.CountAsync(now,"login","failure",default)); Assert.Equal(0,await Repo.CountAsync(now,"login","success",default));
    }
    [Theory] [InlineData("unknown")] [InlineData("wrong")] [InlineData("inactive")] [InlineData("role")] [InlineData("invalid_username")] [InlineData("invalid_password")] [InlineData("success")]
    public async Task Login_decisions_are_truthful_and_unknown_identity_is_absent_from_raw_BSON(string scenario)
    {
        var user=await app.SeedAsync();
        if(scenario=="inactive") await app.Mongo.Users.UpdateOneAsync(x=>x.Id==user.Id,Builders<UserDocument>.Update.Set(x=>x.IsActive,false));
        if(scenario=="role") await app.Mongo.Users.UpdateOneAsync(x=>x.Id==user.Id,Builders<UserDocument>.Update.Set(x=>x.Role,"Invalid"));
        var input=new LoginRequest(scenario=="unknown"?"absent-account":scenario=="invalid_username"?"x":user.Username,scenario=="wrong"?"Wrong-password-123!":scenario=="invalid_password"?"short":"Test-password-123!");
        var auth=app.Services.GetRequiredService<AuthenticationService>();
        if(scenario=="success") Assert.Equal(user.Id.ToString(),(await auth.LoginAsync(input,default)).User.Id); else await Assert.ThrowsAsync<ApiException>(()=>auth.LoginAsync(input,default));
        // Invalid DTOs are rejected before any credential operation, so there is no event.
        if (scenario is "invalid_username" or "invalid_password")
        {
            Assert.Equal(0, await Repo.CountAsync(null,null,null,default));
            return;
        }
        var bson=await app.Mongo.Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument()).SingleAsync();
        var unknown=scenario == "unknown";
        Assert.Equal(unknown?new[] { "_id","action","status","timestamp" }:new[] { "_id","action","status","timestamp","user_id" },bson.Names.OrderBy(x=>x));
        if(!unknown) Assert.Equal(user.Id,bson["user_id"].AsObjectId);
        Assert.Equal("login",bson["action"]); Assert.Equal(scenario=="success"?"success":"failure",bson["status"]); Assert.Equal(BsonType.DateTime,bson["timestamp"].BsonType);
        Assert.DoesNotContain(user.Username,bson.ToJson()); Assert.DoesNotContain(input.Password,bson.ToJson()); Assert.DoesNotContain(user.PasswordHash,bson.ToJson());
    }
    [Theory] [InlineData("create_user")] [InlineData("update_user")] [InlineData("deactivate_user")] [InlineData("update_profile")] [InlineData("change_password")]
    public async Task Lifecycle_and_profile_success_failure_events_match_persisted_state_without_values(string action)
    {
        var lifecycle=app.Services.GetRequiredService<UserLifecycleService>();var profile=app.Services.GetRequiredService<ProfileService>();var user=await app.SeedAsync();var principal=Principal(user);
        var start=DateTime.UtcNow;
        if(action=="create_user") { var created=await lifecycle.CreateAsync(new("created-account","Test-password-123!"),default); Assert.NotNull(await app.Mongo.Users.Find(x=>x.Id==ObjectId.Parse(created.Id)).SingleOrDefaultAsync()); await Assert.ThrowsAsync<ApiException>(()=>lifecycle.CreateAsync(new("created-account","Test-password-123!"),default)); }
        if(action=="update_user") { await lifecycle.UpdateAsync(user.Id.ToString(),JsonSerializer.Deserialize<UpdateUserRequest>("""{"city":"Safe example"}""",TestApp.Json)!,default); await Assert.ThrowsAsync<ApiException>(()=>lifecycle.UpdateAsync("bad",new(),default)); Assert.Equal("Safe example",(await app.Mongo.Users.Find(x=>x.Id==user.Id).SingleAsync()).City); }
        if(action=="deactivate_user") { await lifecycle.DeactivateAsync(user.Id.ToString(),default);await Assert.ThrowsAsync<ApiException>(()=>lifecycle.DeactivateAsync(ObjectId.GenerateNewId().ToString(),default));Assert.False((await app.Mongo.Users.Find(x=>x.Id==user.Id).SingleAsync()).IsActive); }
        if(action=="update_profile") { await profile.UpdateAsync(principal,JsonSerializer.Deserialize<UpdateProfileRequest>("""{"city":"Safe example"}""",TestApp.Json)!,default);await Assert.ThrowsAsync<ApiException>(()=>profile.UpdateAsync(principal,JsonSerializer.Deserialize<UpdateProfileRequest>("""{"city":42}""",TestApp.Json)!,default));Assert.Equal("Safe example",(await app.Mongo.Users.Find(x=>x.Id==user.Id).SingleAsync()).City); }
        if(action=="change_password") { await profile.ChangePasswordAsync(principal,new("Test-password-123!","New-password-123!"),default);await Assert.ThrowsAsync<ApiException>(()=>profile.ChangePasswordAsync(principal,new("Test-password-123!","Other-password-123!"),default));Assert.True(BCrypt.Net.BCrypt.Verify("New-password-123!",(await app.Mongo.Users.Find(x=>x.Id==user.Id).SingleAsync()).PasswordHash)); }
        var logs=await app.Mongo.Logs.Find(x=>x.Action==action).ToListAsync();Assert.Equal(new[] { "success","failure" },logs.Select(x=>x.Status));
        foreach(var log in logs) { Assert.InRange(log.Timestamp,start.AddMilliseconds(-1),DateTime.UtcNow);Assert.Equal(DateTimeKind.Utc,log.Timestamp.Kind);Assert.DoesNotContain("Safe example",log.ToBsonDocument().ToJson());Assert.DoesNotContain("password",log.ToBsonDocument().ToJson().Replace("change_password","operation")); }
    }
    [Fact]
    public async Task Delivery_failure_is_observable_safe_and_does_not_roll_back_completed_operations()
    {
        await app.Mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod","logs" },{ "validator",new BsonDocument("status","never-allowed") },{ "validationAction","error" } });
        var messages=new List<string>(); var logger=new AuditCaptureLogger(messages);var writer=new AuditWriter(Repo,logger);
        var lifecycle=new UserLifecycleService(app.Services.GetRequiredService<IUserRepository>(),new PasswordHasher(),writer);
        var result=await lifecycle.CreateAsync(new("delivery-account","Test-password-123!"),default);Assert.NotNull(await app.Mongo.Users.Find(x=>x.Id==ObjectId.Parse(result.Id)).SingleOrDefaultAsync());
        await Assert.ThrowsAsync<ApiException>(()=>lifecycle.CreateAsync(new("delivery-account","Test-password-123!"),default));Assert.Equal(0,await Repo.CountAsync(null,null,null,default));Assert.Equal(2,messages.Count);Assert.All(messages,x=> { Assert.Contains("Audit delivery failed:",x);Assert.DoesNotContain("delivery-account",x);Assert.DoesNotContain("Test-password",x); });
    }
    [Fact]
    public async Task Cancellation_and_unavailable_reads_propagate_no_fake_page_and_writer_timeout_is_safe()
    {
        using var ct=new CancellationTokenSource();ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service.ListAsync(new(),ct.Token));await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Repo.PageAsync(1,0,ct.Token));await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Repo.AppendAsync(new(),ct.Token));
        var repo=new MongoLogRepository(Unavailable());await Assert.ThrowsAnyAsync<TimeoutException>(()=>new AuditService(repo).ListAsync(new(),default));await Assert.ThrowsAnyAsync<TimeoutException>(()=>repo.PageAsync(1,0,default));
        var messages=new List<string>();await new AuditWriter(repo,new AuditCaptureLogger(messages)).WriteAsync(null,"login","failure");Assert.Single(messages);Assert.Contains("Audit delivery failed:",messages[0]);Assert.DoesNotContain("mongodb",messages[0]);Assert.Equal(0,await Repo.CountAsync(null,null,null,default));
    }
    [Theory] [InlineData("username")] [InlineData("address")] [InlineData("city")] [InlineData("country")] [InlineData("education")] [InlineData("password")]
    public async Task Create_validation_failure_has_no_event_or_persisted_account(string field)
    {
        var input = new CreateUserRequest(field == "username" ? "x" : "invalid-account", field == "password" ? "short" : "Test-password-123!",
            field == "address" ? new string('x',257) : null, field == "city" ? new string('x',101) : null, field == "country" ? new string('x',101) : null, field == "education" ? new string('x',101) : null);
        await Assert.ThrowsAsync<ApiException>(() => app.Services.GetRequiredService<UserLifecycleService>().CreateAsync(input,default));
        // Validation does not execute creation and therefore must not write an audit event.
        Assert.Equal(0,await Repo.CountAsync(null,null,null,default));
        Assert.Equal(0,await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }
    [Theory] [InlineData("login")] [InlineData("create")]
    public async Task Real_unavailable_producer_records_failure_in_separate_working_audit_storage(string operation)
    {
        var repository = new MongoUserRepository(Unavailable()); var writer = app.Services.GetRequiredService<IAuditWriter>(); var hasher = new PasswordHasher();
        if(operation == "login") await Assert.ThrowsAnyAsync<TimeoutException>(() => new AuthenticationService(repository,hasher,app.Services.GetRequiredService<TokenService>(),writer).LoginAsync(new("absent-account","Test-password-123!"),default));
        else await Assert.ThrowsAnyAsync<TimeoutException>(() => new UserLifecycleService(repository,hasher,writer).CreateAsync(new("absent-account","Test-password-123!"),default));
        var raw = await app.Mongo.Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument()).SingleAsync(); Assert.Equal("failure",raw["status"]); Assert.False(raw.Contains("user_id")); Assert.DoesNotContain("absent-account",raw.ToJson());
    }
    [Fact]
    public async Task Writer_success_round_trips_identity_and_omits_unknown_identity_without_operational_warning()
    {
        var messages=new List<string>();var writer=new AuditWriter(Repo,new AuditCaptureLogger(messages));var id=ObjectId.GenerateNewId();
        await writer.WriteAsync(id,"login","success");await writer.WriteAsync(null,"login","failure");Assert.Empty(messages);
        var logs=await Service.ListAsync(new(),default);Assert.Equal(2,logs.Total);Assert.Contains(logs.Items,x=>x.UserId==id.ToString() && x.Status=="success");Assert.Contains(logs.Items,x=>x.UserId==null && x.Status=="failure");
    }
    [Theory] [InlineData("login")] [InlineData("create")]
    public async Task Cancelled_login_records_failure_but_pre_cancelled_create_has_no_event(string operation)
    {
        var user=await app.SeedAsync();using var ct=new CancellationTokenSource();ct.Cancel();
        if(operation=="login") await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>app.Services.GetRequiredService<AuthenticationService>().LoginAsync(new(user.Username,"Test-password-123!"),ct.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>app.Services.GetRequiredService<UserLifecycleService>().CreateAsync(new("cancelled-account","Test-password-123!"),ct.Token));
        // Creation never starts for an already-cancelled request; preserve its no-write contract.
        if (operation == "create") Assert.Equal(0,await Repo.CountAsync(null,null,null,default));
        else { var log=await app.Mongo.Logs.Find(Builders<LogDocument>.Filter.Empty).SingleAsync();Assert.Equal("failure",log.Status);Assert.Null(log.UserId); }
        Assert.Equal(1,await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }
}
// Capture only operational logger output, not a replacement persistence implementation.
public sealed class AuditCaptureLogger(List<string> messages) : ILogger<AuditWriter>
{
    public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
    public bool IsEnabled(LogLevel level)=>true;
    public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>messages.Add(formatter(state,exception));
}
