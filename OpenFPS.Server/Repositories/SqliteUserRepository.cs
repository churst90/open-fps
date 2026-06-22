using BCrypt.Net;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// SQLite-backed implementation of <see cref="IUserRepository"/>.
/// Uses EF Core for safe, transactional credential storage.
/// Creates a fresh DbContext per operation to avoid threading issues with a shared context.
/// </summary>
public class SqliteUserRepository : IUserRepository
{
    private readonly DbContextOptions<AppDbContext> _options;

    public SqliteUserRepository(DbContextOptions<AppDbContext> options)
    {
        _options = options;
        EnsureDatabaseCreated();
        EnsureAdminSeed();
    }

    private void EnsureDatabaseCreated()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
        Log.Information("UserRepository: SQLite database ready.");
    }

    private void EnsureAdminSeed()
    {
        using var ctx = CreateContext();
        if (!ctx.Users.Any())
        {
            ctx.Users.Add(new UserRecord
            {
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                Role = UserRole.Admin
            });
            ctx.SaveChanges();
            Log.Information("UserRepository: Seeded default admin user.");
        }
    }

    public UserData? GetUser(string username)
    {
        using var ctx = CreateContext();
        var record = ctx.Users.AsNoTracking()
            .FirstOrDefault(u => u.Username == username.ToLower());
        if (record == null) return null;
        return new UserData
        {
            Username = record.Username,
            PasswordHash = record.PasswordHash,
            Role = record.Role
        };
    }

    public void AddUser(string username, string password, UserRole role)
    {
        using var ctx = CreateContext();
        var key = username.ToLower();
        if (ctx.Users.Any(u => u.Username == key))
        {
            Log.Warning("UserRepository: Attempted to register duplicate username '{User}'.", key);
            return;
        }

        ctx.Users.Add(new UserRecord
        {
            Username = key,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role
        });
        ctx.SaveChanges();
        Log.Information("UserRepository: Registered user '{User}' with role {Role}.", key, role);
    }

    public bool VerifyPassword(string username, string password)
    {
        var user = GetUser(username);
        if (user == null) return false;
        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
    }

    private AppDbContext CreateContext() => new(_options);
}
