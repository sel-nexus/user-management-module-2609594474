using System.Text.RegularExpressions;
using UserManagement.Api.Discovery;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Common;
namespace UserManagement.Api.Users;

// Query typed scalar filters through the shared Mongo client; never accept client operators.
public sealed class MongoUserRepository(MongoContext context) : IUserRepository
{
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
    // Insert without overwriting an existing account; map unique-index conflicts safely.
    public async Task InsertAsync(UserDocument user, CancellationToken ct)
    {
        try { await context.Users.InsertOneAsync(user, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { throw new ApiException(409, "duplicate_username", "Username is already in use."); }
    }
}
