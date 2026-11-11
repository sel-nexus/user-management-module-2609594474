using System.Text.RegularExpressions;
using UserManagement.Api.Discovery;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Common;
namespace UserManagement.Api.Users;

// Query typed scalar filters through the shared Mongo client; never accept client operators.
public sealed class MongoUserRepository(MongoContext context) : IUserRepository
{
    // Reduce accounts in Mongo: a scalar population facet and country groups, never account materialization.
    public async Task<UserManagement.Api.Dashboard.PopulationSummary> SummarizeAsync(CancellationToken ct)
    {
        var pipeline = new[] { BsonDocument.Parse("""
        { "$facet": {
          "population": [{ "$group": { "_id": null, "total": { "$sum": 1 },
            "active": { "$sum": { "$cond": [{ "$eq": ["$is_active", true] }, 1, 0] } },
            "inactive": { "$sum": { "$cond": [{ "$eq": ["$is_active", false] }, 1, 0] } } } }],
          "countries": [
            { "$project": { "country": { "$trim": { "input": { "$ifNull": ["$country", ""] } } } } },
            { "$group": { "_id": { "$cond": [{ "$eq": ["$country", ""] }, "Unknown", "$country"] }, "count": { "$sum": 1 } } },
            { "$sort": { "_id": 1 } }
          ]
        } }
        """) };
        var result = await context.Users.Aggregate<BsonDocument>(pipeline).SingleAsync(ct);
        var population = result["population"].AsBsonArray;
        var counts = population.Count == 0 ? null : population[0].AsBsonDocument;
        var countries = result["countries"].AsBsonArray.Select(x => new UserManagement.Api.Dashboard.CountryCount(x["_id"].AsString, x["count"].ToInt64())).ToArray();
        return new(counts?["total"].ToInt64() ?? 0, counts?["active"].ToInt64() ?? 0, counts?["inactive"].ToInt64() ?? 0, countries);
    }
    // Find the normalized natural key.
    public Task<UserDocument?> FindByUsernameAsync(string username, CancellationToken ct) => context.Users.Find(x => x.Username == username).FirstOrDefaultAsync(ct)!;
    // Find an account by its persisted BSON identity.
    public Task<UserDocument?> FindByIdAsync(ObjectId id, CancellationToken ct) => context.Users.Find(x => x.Id == id).FirstOrDefaultAsync(ct)!;
    // Build typed predicates with escaped literal search and a deterministic ascending id tie-break.
    public async Task<UserListResponse> ListAsync(UserListQuery query, CancellationToken ct)
    {
        query.Validate();
        var f = Builders<UserDocument>.Filter;
        var filter = f.Empty;
        if (!string.IsNullOrEmpty(query.Search))
        {
            var literal = new BsonRegularExpression(Regex.Escape(query.Search), "i");
            filter &= f.Or(f.Regex(x => x.Username, literal), f.Regex(x => x.City, literal));
        }
        if (query.Country is not null) filter &= f.Eq(x => x.Country, query.Country);
        if (query.Education is not null) filter &= f.Eq(x => x.Education, query.Education);
        if (query.IsActive is not null) filter &= f.Eq(x => x.IsActive, query.IsActive.Value);
        System.Linq.Expressions.Expression<Func<UserDocument, object>> field = query.Sort switch
        {
            "city" => x => x.City!, "country" => x => x.Country!, "education" => x => x.Education!, _ => x => x.Username
        };
        var sort = query.Direction == "asc" ? Builders<UserDocument>.Sort.Ascending(field) : Builders<UserDocument>.Sort.Descending(field);
        sort = sort.Ascending(x => x.Id);
        var total = await context.Users.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await context.Users.Find(filter).Sort(sort).Skip(query.Skip).Limit(query.Limit)
            .Project(x => new UserResponse(x.Id.ToString(), x.Username, x.Role, x.IsActive, x.Address, x.City, x.Country, x.Education, x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);
        return new UserListResponse(items, query.Limit, query.Skip, total);
    }
    // Atomically update supplied approved fields only; return the winning last-write state.
    public async Task<UserDocument?> UpdateAsync(ObjectId id, IReadOnlyDictionary<string, string?> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return await FindByIdAsync(id, ct);
        var u = Builders<UserDocument>.Update;
        var updates = new List<UpdateDefinition<UserDocument>> { u.Set(x => x.UpdatedAt, UserDocument.CanonicalizeTimestamp(DateTime.UtcNow)) };
        foreach (var (name, value) in changes)
            updates.Add(name switch
            {
                "username" => u.Set(x => x.Username, value!), "address" => u.Set(x => x.Address, value),
                "city" => u.Set(x => x.City, value), "country" => u.Set(x => x.Country, value),
                "education" => u.Set(x => x.Education, value),
                _ => throw new ApiException(400, "validation_error", "Unknown update field.")
            });
        try
        {
            return await context.Users.FindOneAndUpdateAsync(x => x.Id == id, u.Combine(updates),
                new FindOneAndUpdateOptions<UserDocument> { ReturnDocument = ReturnDocument.After }, ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { throw new ApiException(409, "duplicate_username", "Username is already in use."); }
        catch (MongoCommandException e) when (e.Code == 11000)
        { throw new ApiException(409, "duplicate_username", "Username is already in use."); }
    }
    // Filter active identity and role in the same atomic write, without touching credentials.
    public async Task<UserDocument?> UpdateProfileAsync(ObjectId id, string role, IReadOnlyDictionary<string, string?> changes, CancellationToken ct)
    {
        var filter = Builders<UserDocument>.Filter.Where(x => x.Id == id && x.IsActive && x.Role == role);
        if (changes.Count == 0) return await context.Users.Find(filter).FirstOrDefaultAsync(ct);
        var u = Builders<UserDocument>.Update;
        var updates = new List<UpdateDefinition<UserDocument>> { u.Set(x => x.UpdatedAt, UserDocument.CanonicalizeTimestamp(DateTime.UtcNow)) };
        foreach (var (name, value) in changes)
            updates.Add(name switch
            {
                "address" => u.Set(x => x.Address, value), "city" => u.Set(x => x.City, value),
                "country" => u.Set(x => x.Country, value), "education" => u.Set(x => x.Education, value),
                _ => throw new ApiException(400, "validation_error", "Unknown profile field.")
            });
        return await context.Users.FindOneAndUpdateAsync(filter, u.Combine(updates),
            new FindOneAndUpdateOptions<UserDocument> { ReturnDocument = ReturnDocument.After }, ct);
    }
    // Only the still-active account with the verified current hash can win this update.
    public async Task<bool> ChangePasswordAsync(ObjectId id, string role, string expectedHash, string newHash, CancellationToken ct)
    {
        var result = await context.Users.UpdateOneAsync(x => x.Id == id && x.IsActive && x.Role == role && x.PasswordHash == expectedHash,
            Builders<UserDocument>.Update.Set(x => x.PasswordHash, newHash).Set(x => x.UpdatedAt, UserDocument.CanonicalizeTimestamp(DateTime.UtcNow)), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
    // Change activity once without replacing credentials/profile or moving repeat-delete timestamps.
    public async Task<UserDocument?> DeactivateAsync(ObjectId id, CancellationToken ct)
    {
        var changed = await context.Users.FindOneAndUpdateAsync(x => x.Id == id && x.IsActive,
            Builders<UserDocument>.Update.Set(x => x.IsActive, false).Set(x => x.UpdatedAt, UserDocument.CanonicalizeTimestamp(DateTime.UtcNow)),
            new FindOneAndUpdateOptions<UserDocument> { ReturnDocument = ReturnDocument.After }, ct);
        return changed ?? await FindByIdAsync(id, ct);
    }
    // Insert without overwriting an existing account; map unique-index conflicts safely.
    public async Task InsertAsync(UserDocument user, CancellationToken ct)
    {
        try { await context.Users.InsertOneAsync(user, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { throw new ApiException(409, "duplicate_username", "Username is already in use."); }
    }
}
