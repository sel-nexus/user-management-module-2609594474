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
    // Insert without overwriting an existing account; map unique-index conflicts safely.
    public async Task InsertAsync(UserDocument user, CancellationToken ct)
    {
        try { await context.Users.InsertOneAsync(user, cancellationToken: ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { throw new ApiException(409, "duplicate_username", "Username is already in use."); }
    }
}
