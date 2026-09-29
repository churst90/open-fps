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

    /// <summary>
    /// The first run seeds an admin. With OPENFPS_ADMIN_PASSWORD set, that is its password — and on an
    /// existing database the admin's password is RESET to it, which is the only way to change it
    /// without a client. A server reachable from the internet must be started with it at least once:
    /// admin/admin123 is written in this repository for anyone to read.
    /// </summary>
    private void EnsureAdminSeed()
    {
        string? chosen = Environment.GetEnvironmentVariable("OPENFPS_ADMIN_PASSWORD");
        using var ctx = CreateContext();
        if (!ctx.Users.Any())
        {
            ctx.Users.Add(new UserRecord
            {
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(string.IsNullOrEmpty(chosen) ? "admin123" : chosen),
                Role = UserRole.Admin
            });
            ctx.SaveChanges();
            Log.Information("UserRepository: Seeded default admin user.");
        }
        else if (!string.IsNullOrEmpty(chosen) && ctx.Users.FirstOrDefault(u => u.Username == "admin") is { } admin)
        {
            admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(chosen);
            ctx.SaveChanges();
            Log.Information("UserRepository: admin password set from OPENFPS_ADMIN_PASSWORD.");
        }
        if (string.IsNullOrEmpty(chosen) && ctx.Users.FirstOrDefault(u => u.Username == "admin") is { } a
            && BCrypt.Net.BCrypt.Verify("admin123", a.PasswordHash))
            Log.Warning("UserRepository: the admin password is still the default, admin123. "
                      + "Start once with OPENFPS_ADMIN_PASSWORD set before anyone else can reach this server.");
    }

    public UserData? GetUser(string username)
    {
        // Folded here, not inside the expression tree: EF has to translate the predicate to SQL and
        // cannot translate a call into our own code.
        var key = Normalize(username);
        using var ctx = CreateContext();
        var record = ctx.Users.AsNoTracking()
            .FirstOrDefault(u => u.Username == key);
        if (record == null) return null;
        return new UserData
        {
            Username = record.Username,
            PasswordHash = record.PasswordHash,
            Role = record.Role
        };
    }

    public bool AddUser(string username, string password, UserRole role)
    {
        using var ctx = CreateContext();
        var key = Normalize(username);
        if (ctx.Users.Any(u => u.Username == key))
        {
            Log.Warning("UserRepository: Attempted to register duplicate username '{User}'.", key);
            return false;
        }

        ctx.Users.Add(new UserRecord
        {
            Username = key,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role
        });
        ctx.SaveChanges();
        Log.Information("UserRepository: Registered user '{User}' with role {Role}.", key, role);
        return true;
    }

    public bool VerifyPassword(string username, string password)
    {
        var user = GetUser(username);
        if (user == null) return false;
        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
    }

    private AppDbContext CreateContext() => new(_options);

    /// <summary>
    /// Case-folds a username for storage and lookup. Invariant, not current-culture: under a Turkish
    /// locale ToLower() maps 'I' to a dotless 'ı', so the same account name would hash to two different
    /// keys depending on where the server happens to be running.
    /// </summary>
    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
