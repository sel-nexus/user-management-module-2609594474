using System.ComponentModel.DataAnnotations;
namespace UserManagement.Api.Users;

// Define safe wire projections and allowlisted future account input, never entity serialization.
public sealed record UserResponse(string Id, string Username, string Role, bool IsActive, string? Address, string? City, string? Country, string? Education, DateTime CreatedAt, DateTime UpdatedAt)
{
    // Project only public fields from the persisted account.
    public static UserResponse From(UserDocument user) => new(user.Id.ToString(), user.Username, user.Role, user.IsActive, user.Address, user.City, user.Country, user.Education, user.CreatedAt, user.UpdatedAt);
}
public sealed record CreateUserRequest([Required, StringLength(64, MinimumLength = 3)] string Username, [Required] string Password, [StringLength(256)] string? Address = null, [StringLength(100)] string? City = null, [StringLength(100)] string? Country = null, [StringLength(100)] string? Education = null);
public sealed record UpdateUserRequest([StringLength(64, MinimumLength = 3)] string? Username, [StringLength(256)] string? Address, [StringLength(100)] string? City, [StringLength(100)] string? Country, [StringLength(100)] string? Education);
