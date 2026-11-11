using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Common;
using UserManagement.Api.Discovery;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Exercise discovery through the real Mongo repository, including nulls, literal predicates and stable pages.
public sealed class DiscoveryTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    private DiscoveryService Service => app.Services.GetRequiredService<DiscoveryService>();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Defaults_bound_page_total_and_safe_projection_survive_repository_recreation()
    {
        var users = Enumerable.Range(0, 25).Select(i => new UserDocument { Username = $"account-{i:00}", PasswordHash = "never-return", City = "Example city" }).ToArray();
        await app.Mongo.Users.InsertManyAsync(users.Reverse());
        var page = await Service.ListAsync(new(), default);
        Assert.Equal(20, page.Limit); Assert.Equal(0, page.Skip); Assert.Equal(25, page.Total);
        Assert.Equal(users.Take(20).Select(x => x.Id.ToString()), page.Items.Select(x => x.Id));
        var again = await new DiscoveryService(new MongoUserRepository(app.Mongo)).ListAsync(new(Limit: 100), default);
        Assert.Equal(25, again.Items.Count); Assert.Equal(users.Select(x => x.Id.ToString()), again.Items.Select(x => x.Id));
        Assert.Equal("Example city", again.Items[0].City);
        Assert.DoesNotContain("never-return", System.Text.Json.JsonSerializer.Serialize(page, TestApp.Json));
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<UserManagement.Api.Audit.LogDocument>.Filter.Empty));
    }

    public static IEnumerable<object[]> Sorts()
    {
        foreach (var field in new[] { "username", "city", "country", "education" })
            foreach (var direction in new[] { "asc", "desc" }) yield return [field, direction];
    }
    [Theory]
    [MemberData(nameof(Sorts))]
    public async Task Every_sort_direction_orders_nulls_values_and_ascending_id_ties_across_pages(string field, string direction)
    {
        var users = new[] {
            new UserDocument { Id = ObjectId.Parse("000000000000000000000004"), Username = "delta", City = "B", Country = "B", Education = "B" },
            new UserDocument { Id = ObjectId.Parse("000000000000000000000003"), Username = "charlie", City = "A", Country = "A", Education = "A" },
            new UserDocument { Id = ObjectId.Parse("000000000000000000000002"), Username = "bravo", City = "A", Country = "A", Education = "A" },
            new UserDocument { Id = ObjectId.Parse("000000000000000000000001"), Username = "alpha" }
        };
        await app.Mongo.Users.InsertManyAsync(users);
        // Make username ties real as well, only within this fixture's isolated collection.
        if (field == "username") {
            await app.Mongo.Users.Indexes.DropOneAsync("username_unique");
            users[1].Username = "bravo";
            await app.Mongo.Users.UpdateOneAsync(x => x.Id == users[1].Id, Builders<UserDocument>.Update.Set(x => x.Username, "bravo"));
        }
        string? Value(UserDocument u) => field switch { "username" => u.Username, "city" => u.City, "country" => u.Country, _ => u.Education };
        var ordered = direction == "asc" ? users.OrderBy(Value, StringComparer.Ordinal).ThenBy(x => x.Id) : users.OrderByDescending(Value, StringComparer.Ordinal).ThenBy(x => x.Id);
        var expected = ordered.Select(x => x.Id.ToString()).ToArray();
        var first = await Service.ListAsync(new(Limit: 2, Sort: field, Direction: direction), default);
        var second = await Service.ListAsync(new(Limit: 2, Skip: 2, Sort: field, Direction: direction), default);
        Assert.Equal(expected, first.Items.Concat(second.Items).Select(x => x.Id));
        Assert.Equal(4, first.Total); Assert.Equal(4, second.Total);
        Assert.Equal(first.Items.Select(x => x.Id), (await Service.ListAsync(new(Limit: 2, Sort: field, Direction: direction), default)).Items.Select(x => x.Id));
    }

    [Theory]
    [InlineData("ALPHA", "alpha")] [InlineData("LONDON", "bravo")]
    [InlineData(".*", "charlie")] [InlineData("[a]+$", "delta")]
    public async Task Search_is_case_insensitive_literal_username_or_city_only(string search, string expected)
    {
        await app.Mongo.Users.InsertManyAsync(new[] {
            new UserDocument { Username = "alpha", City = null },
            new UserDocument { Username = "bravo", City = "London" },
            new UserDocument { Username = "charlie", City = "literal .* city" },
            new UserDocument { Username = "delta", City = "[a]+$" },
            new UserDocument { Username = "other", Country = search, Education = search, Address = search }
        });
        var page = await Service.ListAsync(new(Search: search), default);
        Assert.Equal(expected, Assert.Single(page.Items).Username); Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Each_exact_filter_and_all_combinations_preserve_null_empty_case_and_boolean_semantics()
    {
        var users = new[] {
            new UserDocument { Username = "alpha", City = "Find me", Country = "UK", Education = "Degree", IsActive = true },
            new UserDocument { Username = "bravo", City = "Find me", Country = "UK", Education = "Degree", IsActive = false },
            new UserDocument { Username = "charlie", Country = "UK", Education = "Other", IsActive = true },
            new UserDocument { Username = "delta", Country = "uk", Education = "degree", IsActive = true },
            new UserDocument { Username = "echo", Country = "", Education = "", IsActive = false },
            new UserDocument { Username = "foxtrot" }
        };
        await app.Mongo.Users.InsertManyAsync(users);
        for (var mask = 0; mask < 16; mask++) {
            var query = new UserListQuery(Search: (mask & 1) != 0 ? "FIND" : null, Country: (mask & 2) != 0 ? "UK" : null, Education: (mask & 4) != 0 ? "Degree" : null, IsActive: (mask & 8) != 0 ? true : null);
            var expected = users.Where(u => (query.Search is null || u.City == "Find me") && (query.Country is null || u.Country == "UK") && (query.Education is null || u.Education == "Degree") && (query.IsActive is null || u.IsActive)).Select(x => x.Username).ToArray();
            var page = await Service.ListAsync(query, default); Assert.Equal(expected, page.Items.Select(x => x.Username)); Assert.Equal(expected.Length, page.Total);
        }
        Assert.Equal(new[] { "bravo", "echo" }, (await Service.ListAsync(new(IsActive: false), default)).Items.Select(x => x.Username));
        Assert.Equal("delta", Assert.Single((await Service.ListAsync(new(Country: "uk", Education: "degree"), default)).Items).Username);
        Assert.Equal("echo", Assert.Single((await Service.ListAsync(new(Country: "", Education: ""), default)).Items).Username);
        Assert.Empty((await Service.ListAsync(new(Country: "U"), default)).Items);
        Assert.Equal(6, (await Service.ListAsync(new(Search: ""), default)).Total);
    }

    [Theory]
    [InlineData(1, 0, 1)] [InlineData(100, 0, 3)] [InlineData(1, 2, 1)] [InlineData(20, 3, 0)] [InlineData(20, int.MaxValue, 0)]
    public async Task Valid_bounds_and_beyond_end_offsets_never_change_total(int limit, int skip, int count)
    {
        await app.Mongo.Users.InsertManyAsync(Enumerable.Range(0,3).Select(i => new UserDocument { Username = $"user-{i}" }));
        var page = await Service.ListAsync(new(Limit: limit, Skip: skip), default);
        Assert.Equal(limit, page.Limit); Assert.Equal(skip, page.Skip); Assert.Equal(count, page.Items.Count); Assert.Equal(3, page.Total);
        if (count > 0) Assert.Equal($"user-{skip}", page.Items[0].Username);
    }
    [Fact]
    public async Task Empty_database_and_unmatched_search_return_real_zeroes()
    {
        var empty = await Service.ListAsync(new(), default); Assert.Empty(empty.Items); Assert.Equal(0, empty.Total);
        await app.Mongo.Users.InsertOneAsync(new UserDocument { Username = "exists" });
        var missing = await Service.ListAsync(new(Search: "absent"), default); Assert.Empty(missing.Items); Assert.Equal(0, missing.Total);
    }
    public static IEnumerable<object[]> InvalidQueries()
    {
        yield return [new UserListQuery(Limit:0)]; yield return [new UserListQuery(Limit:101)]; yield return [new UserListQuery(Skip:-1)];
        foreach(var field in new[] { "id", "$where", "Username", "" }) yield return [new UserListQuery(Sort:field)];
        foreach(var direction in new[] { "ASC", "", "sideways" }) yield return [new UserListQuery(Direction:direction)];
        yield return [new UserListQuery(Search:new string('x',101))]; yield return [new UserListQuery(Country:new string('x',101))]; yield return [new UserListQuery(Education:new string('x',101))];
    }
    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task Direct_service_and_repository_invalid_values_fail_without_read_or_write(UserListQuery query)
    {
        var error = await Assert.ThrowsAsync<ApiException>(() => Service.ListAsync(query, default)); Assert.Equal(400, error.Status);
        await Assert.ThrowsAsync<ApiException>(() => new MongoUserRepository(app.Mongo).ListAsync(query, default));
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }
    [Fact]
    public void Parser_accepts_exact_text_bounds_but_rejects_duplicate_unknown_and_malformed_values()
    {
        var parsed = UserListQuery.Parse(new QueryCollection(new Dictionary<string,StringValues> { ["limit"]="100", ["skip"]=int.MaxValue.ToString(), ["search"]=new string('s',100), ["country"]=new string('c',100), ["education"]=new string('e',100), ["is_active"]="false", ["sort"]="city", ["direction"]="desc" }));
        Assert.Equal(100, parsed.Limit); Assert.Equal(int.MaxValue, parsed.Skip); Assert.False(parsed.IsActive); Assert.Equal("desc", parsed.Direction);
        Assert.Equal(new UserListQuery(), UserListQuery.Parse(new QueryCollection()));
        foreach(var key in new[] { "limit", "skip", "search", "country", "education", "is_active", "sort", "direction" })
            Assert.Throws<ApiException>(() => UserListQuery.Parse(new QueryCollection(new Dictionary<string,StringValues> { [key]=new StringValues(new[] { "1", "1" }) })));
        foreach(var pair in new[] { ("unknown","1"), ("Limit","1"), ("skip","-1"), ("skip","2147483648"), ("limit","1.5"), ("limit","+20"), ("limit",""), ("is_active","True"), ("is_active","0"), ("is_active","") })
            Assert.Throws<ApiException>(() => UserListQuery.Parse(new QueryCollection(new Dictionary<string,StringValues> { [pair.Item1]=pair.Item2 })));
    }
    [Theory]
    [InlineData("{\"$ne\":null}")] [InlineData("$where")] [InlineData("' OR 1=1 --")]
    public async Task Operator_looking_text_is_a_scalar_not_an_executable_predicate(string literal)
    {
        await app.Mongo.Users.InsertManyAsync(new[] { new UserDocument { Username="literal", City=literal, Country=literal, Education=literal }, new UserDocument { Username="other", Country="else" } });
        var page=await Service.ListAsync(new(Search:literal,Country:literal,Education:literal),default); Assert.Equal("literal",Assert.Single(page.Items).Username); Assert.Equal(1,page.Total);
    }
    [Fact]
    public async Task Cancelled_or_unavailable_database_never_becomes_a_successful_empty_page()
    {
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service.ListAsync(new(),cancelled.Token));
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"]="mongodb://127.0.0.1:1", ["Mongo:Database"]=app.DatabaseName }).Build();
        await Assert.ThrowsAnyAsync<TimeoutException>(()=>new DiscoveryService(new MongoUserRepository(new MongoContext(config))).ListAsync(new(),default));
    }
}
