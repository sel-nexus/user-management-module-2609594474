using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Audit;
using UserManagement.Api.Common;
using UserManagement.Api.Dashboard;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Exercise actual Mongo pipelines under a deterministic clock, including legacy missing fields.
public sealed class DashboardTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    private readonly DashboardClock clock = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();
    private IUserRepository Users => app.Services.GetRequiredService<IUserRepository>();
    private MongoLogRepository Logs => app.Services.GetRequiredService<MongoLogRepository>();
    private DashboardService Service => new(Users, Logs, clock);
    [Fact]
    public async Task Empty_collections_produce_real_zeroes_and_one_UTC_clock_sample()
    {
        var result = await Service.GetAsync(default);
        Assert.Equal(0, result.Total); Assert.Equal(0, result.Active); Assert.Equal(0, result.Inactive);
        Assert.Empty(result.CountryDistribution); Assert.Equal(0, result.Activity); Assert.Equal(0, result.LoginSuccess); Assert.Equal(0, result.LoginFailure);
        Assert.Equal(clock.Now.UtcDateTime, result.GeneratedAt); Assert.Equal(clock.Now.UtcDateTime.AddDays(-7), result.WindowStart);
        Assert.Equal(DateTimeKind.Utc, result.GeneratedAt.Kind); Assert.Equal(DateTimeKind.Utc, result.WindowStart.Kind); Assert.Equal(1, clock.Reads);
        Assert.Empty((await Users.SummarizeAsync(default)).CountryDistribution);
        Assert.Equal(new ActivitySummary(0,0,0), await Logs.SummarizeAsync(result.WindowStart,result.GeneratedAt,default));
    }
    [Fact]
    public async Task Missing_null_blank_and_whitespace_countries_group_as_Unknown_with_exact_sorted_countries()
    {
        var raw = app.Mongo.Database.GetCollection<BsonDocument>("users");
        var countries = new BsonValue?[] { null, BsonNull.Value, "", " \t\n ", "India", " India ", "india", "Unknown", "New Zealand" };
        for (var i=0;i<countries.Length;i++)
        {
            var user = new UserDocument { Username = "country-"+i, IsActive = i%2==0 }.ToBsonDocument().Add("private_field","not-returned");
            if (countries[i] is null) user.Remove("country"); else user["country"] = countries[i]!;
            await raw.InsertOneAsync(user);
        }
        var result = await Service.GetAsync(default);
        Assert.Equal(9,result.Total); Assert.Equal(5,result.Active); Assert.Equal(4,result.Inactive);
        Assert.Equal(new[] { new CountryCount("India",2),new CountryCount("New Zealand",1),new CountryCount("Unknown",5),new CountryCount("india",1) },result.CountryDistribution);
        Assert.Equal(result.Total,result.CountryDistribution.Sum(x=>x.Count));
        var population = await Users.SummarizeAsync(default); Assert.Equal(result.CountryDistribution,population.CountryDistribution);
        // A persisted edit/deactivation is reflected on a new read, without deleting the account.
        var target = (await raw.Find(new BsonDocument("username","country-0")).SingleAsync())["_id"].AsObjectId;
        await raw.UpdateOneAsync(new BsonDocument("_id",target),new BsonDocument("$set",new BsonDocument("country","India").Add("is_active",false)));
        var changed = await Service.GetAsync(default); Assert.Equal(9,changed.Total);Assert.Equal(4,changed.Active);Assert.Equal(5,changed.Inactive);
        Assert.Contains(new CountryCount("India",3),changed.CountryDistribution);Assert.Contains(new CountryCount("Unknown",4),changed.CountryDistribution);
        Assert.Equal(9,await raw.CountDocumentsAsync(new BsonDocument()));Assert.Equal(0,await Logs.CountAsync(null,null,null,default));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Uniform_population_has_no_fabricated_opposite_status_count(bool active)
    {
        await app.Mongo.Users.InsertManyAsync(Enumerable.Range(0,125).Select(i=>new UserDocument { Username="population-"+i,Country="India",IsActive=active }));
        var result=await Service.GetAsync(default);Assert.Equal(125,result.Total);Assert.Equal(active?125:0,result.Active);Assert.Equal(active?0:125,result.Inactive);Assert.Equal(new CountryCount("India",125),Assert.Single(result.CountryDistribution));
    }
    [Fact]
    public async Task Seven_day_bounds_include_start_and_end_exclude_older_and_future_and_match_exact_login_outcomes()
    {
        var end=clock.Now.UtcDateTime;var start=end.AddDays(-7);
        var logs=new[] {
            new LogDocument { Timestamp=start.AddMilliseconds(-1),Action="login",Status="success" },
            new LogDocument { Timestamp=start,Action="login",Status="success" },
            new LogDocument { Timestamp=start.AddMilliseconds(1),Action="login",Status="failure" },
            new LogDocument { Timestamp=end.AddMilliseconds(-1),Action="update_user",Status="success" },
            new LogDocument { Timestamp=end,Action="login",Status="failure" },
            new LogDocument { Timestamp=end.AddMilliseconds(1),Action="login",Status="failure" },
            new LogDocument { Timestamp=end.AddDays(-1),Action="Login",Status="success" },
            new LogDocument { Timestamp=end.AddDays(-1),Action="login",Status="Success" },
            new LogDocument { Timestamp=end.AddDays(-1),Action="login",Status="unknown" }
        };
        await app.Mongo.Logs.InsertManyAsync(logs);
        var raw=app.Mongo.Database.GetCollection<BsonDocument>("logs");
        foreach(var timestamp in new BsonValue?[] { null,BsonNull.Value,"2026-02-01T12:00:00Z" })
        {
            var doc=new BsonDocument { { "action","login" },{ "status","success" } }; if(timestamp is not null) doc["timestamp"]=timestamp;await raw.InsertOneAsync(doc);
        }
        var result=await Service.GetAsync(default);Assert.Equal(7,result.Activity);Assert.Equal(1,result.LoginSuccess);Assert.Equal(2,result.LoginFailure);
        Assert.Equal(new ActivitySummary(1,2,7),await Logs.SummarizeAsync(start,end,default));Assert.Equal(12,await raw.CountDocumentsAsync(new BsonDocument()));
        clock.Now=clock.Now.AddDays(8);var later=await Service.GetAsync(default);Assert.Equal(0,later.Activity);Assert.Equal(0,later.LoginSuccess);Assert.Equal(0,later.LoginFailure);
    }
    [Fact]
    public async Task Fractional_clock_boundaries_do_not_round_an_older_BSON_timestamp_into_the_window()
    {
        var end=clock.Now.UtcDateTime;var start=end.AddDays(-7);
        await app.Mongo.Logs.InsertManyAsync(new[] {
            new LogDocument { Timestamp=start,Action="login",Status="success" },
            new LogDocument { Timestamp=start.AddMilliseconds(1),Action="login",Status="success" },
            new LogDocument { Timestamp=end,Action="login",Status="failure" },
            new LogDocument { Timestamp=end.AddMilliseconds(1),Action="login",Status="failure" }
        });
        clock.Now=clock.Now.AddTicks(1);var result=await Service.GetAsync(default);
        Assert.Equal(2,result.Activity);Assert.Equal(1,result.LoginSuccess);Assert.Equal(1,result.LoginFailure);
        Assert.Equal(clock.Now.UtcDateTime,result.GeneratedAt);Assert.Equal(clock.Now.UtcDateTime.AddDays(-7),result.WindowStart);
    }
    [Fact]
    public async Task Missing_action_or_status_is_activity_but_not_a_login_outcome()
    {
        var raw=app.Mongo.Database.GetCollection<BsonDocument>("logs");var now=clock.Now.UtcDateTime;
        await raw.InsertManyAsync(new[] { new BsonDocument("timestamp",now), new BsonDocument("timestamp",now).Add("action","login"),new BsonDocument("timestamp",now).Add("status","success"),new BsonDocument("timestamp",now).Add("action",BsonNull.Value).Add("status",BsonNull.Value) });
        var result=await Service.GetAsync(default);Assert.Equal(4,result.Activity);Assert.Equal(0,result.LoginSuccess);Assert.Equal(0,result.LoginFailure);
    }
    [Fact]
    public async Task Cancellation_propagates_from_service_and_each_repository_without_fake_summary()
    {
        using var source=new CancellationTokenSource();source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service.GetAsync(source.Token));Assert.Equal(0,clock.Reads);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Users.SummarizeAsync(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Logs.SummarizeAsync(clock.Now.UtcDateTime.AddDays(-7),clock.Now.UtcDateTime,source.Token));
    }
    [Theory] [InlineData("users")] [InlineData("logs")]
    public async Task Real_unavailable_repository_propagates_failure_not_partial_or_empty_success(string collection)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"]="mongodb://127.0.0.1:1",["Mongo:Database"]=app.DatabaseName }).Build();var unavailable=new MongoContext(config);
        var users=collection=="users"?new MongoUserRepository(unavailable):Users;var logs=collection=="logs"?new MongoLogRepository(unavailable):Logs;
        await Assert.ThrowsAnyAsync<TimeoutException>(()=>new DashboardService(users,logs,clock).GetAsync(default));
        Assert.Equal(0,await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));Assert.Equal(0,await Logs.CountAsync(null,null,null,default));
    }
}
// Offset deliberately differs from UTC; advancing between requests does not require sleeps.
public sealed class DashboardClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026,2,1,17,30,0,TimeSpan.FromHours(5.5));
    public int Reads { get; private set; }
    public override DateTimeOffset GetUtcNow() { Reads++;return Now; }
}
