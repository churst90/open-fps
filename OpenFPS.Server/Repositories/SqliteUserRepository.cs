using BCrypt.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// The accounts, in SQLite through EF Core. A fresh DbContext per operation, because a shared one is
/// not thread-safe.
///
/// Passwords are salted bcrypt hashes at <see cref="WorkFactor"/>, compared in constant time; a hash
/// made at a lower cost is rehashed at the current one the next time its owner logs in.
/// </summary>
public class SqliteUserRepository : IUserRepository
{
    /// <summary>The bcrypt cost for new and upgraded hashes. Each step doubles the time.</summary>
    public const int DefaultWorkFactor = 12;

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _dummyHash;

    public int WorkFactor { get; }

    public SqliteUserRepository(DbContextOptions<AppDbContext> options, int workFactor = DefaultWorkFactor)
    {
        _options = options;
        WorkFactor = workFactor;
        // Checked against when the name is unknown, so a refusal costs the same either way.
        _dummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), WorkFactor);
        EnsureDatabaseCreated();
        EnsureAdminSeed();
        EnsureOwner();
    }

    private void EnsureDatabaseCreated()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
        UpgradeSchema(ctx);
        Log.Information("UserRepository: SQLite database ready.");
    }

    /// <summary>The columns added after the first release, typed as EF Core would create them, so an
    /// upgraded table and a new one read the same.</summary>
    private static readonly (string Column, string Definition)[] AddedColumns =
    {
        ("CreatedUtc", "TEXT NULL"),
        ("LastLoginUtc", "TEXT NULL"),
        ("LastLoginAddress", "TEXT NULL"),
        ("FailedLogins", "INTEGER NOT NULL DEFAULT 0"),
        ("LastFailedUtc", "TEXT NULL"),
        ("LastFailedAddress", "TEXT NULL"),
        ("RealName", "TEXT NULL"),
        ("Permissions", "TEXT NULL"),
        ("CustomRole", "TEXT NULL"),
        ("PlayerState", "TEXT NULL"),
        ("Belongings", "TEXT NULL"),
        ("BannedUtc", "TEXT NULL"),
        ("BannedUntilUtc", "TEXT NULL"),
        ("BannedBy", "TEXT NULL"),
        ("BanReason", "TEXT NULL"),
    };

    /// <summary>
    /// Adds the missing columns to a database an older server made: EnsureCreated never touches one
    /// that exists. ALTER TABLE keeps every row, and the file is first copied beside itself
    /// (openfps.db.before-YYYYMMDD-HHMMSS) so a bad upgrade can be undone by hand.
    /// </summary>
    private static void UpgradeSchema(AppDbContext ctx)
    {
        var conn = ctx.Database.GetDbConnection();
        bool opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(\"Users\");";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) existing.Add(reader.GetString(1));
            }

            var missing = AddedColumns.Where(c => !existing.Contains(c.Column)).ToList();
            if (missing.Count == 0) return;

            string file = new SqliteConnectionStringBuilder(conn.ConnectionString).DataSource;
            if (!string.IsNullOrEmpty(file) && file != ":memory:" && File.Exists(file))
            {
                string backup = $"{Path.GetFullPath(file)}.before-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                using var copy = conn.CreateCommand();
                copy.CommandText = "VACUUM INTO $path;";
                copy.Parameters.Add(new SqliteParameter("$path", backup));
                copy.ExecuteNonQuery();
                Log.Information("UserRepository: copied the accounts database to {Backup} before upgrading it.", backup);
            }

            foreach (var (column, definition) in missing)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"Users\" ADD COLUMN \"{column}\" {definition};";
                alter.ExecuteNonQuery();
            }
            Log.Information("UserRepository: added {Columns} to the Users table.", string.Join(", ", missing.Select(m => m.Column)));
        }
        finally
        {
            if (opened) conn.Close();
        }
    }

    /// <summary>
    /// Seeds the admin account, an Owner, on the first run. With OPENFPS_ADMIN_PASSWORD set, that is its password, and on
    /// an existing database the admin's password is reset to it. A server reachable from the internet
    /// must be started with it at least once: admin/admin123 is in this repository for anyone to read.
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
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(string.IsNullOrEmpty(chosen) ? "admin123" : chosen, WorkFactor),
                Role = UserRole.Owner,
                CreatedUtc = DateTime.UtcNow,
            });
            ctx.SaveChanges();
            Log.Information("UserRepository: Seeded default admin user.");
        }
        else if (!string.IsNullOrEmpty(chosen) && ctx.Users.FirstOrDefault(u => u.Username == "admin") is { } admin)
        {
            admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(chosen, WorkFactor);
            ctx.SaveChanges();
            Log.Information("UserRepository: admin password set from OPENFPS_ADMIN_PASSWORD.");
        }
        if (string.IsNullOrEmpty(chosen) && ctx.Users.FirstOrDefault(u => u.Username == "admin") is { } a
            && BCrypt.Net.BCrypt.Verify("admin123", a.PasswordHash))
            Log.Warning("UserRepository: the admin password is still the default, admin123. "
                      + "Start once with OPENFPS_ADMIN_PASSWORD set before anyone else can reach this server.");
    }

    /// <summary>
    /// A server always has an owner. A database from before the Owner role (2026-10-09), or one whose
    /// owners were taken off by hand, makes the seeded admin account the owner again.
    /// </summary>
    private void EnsureOwner()
    {
        using var ctx = CreateContext();
        if (ctx.Users.Any(u => u.Role == UserRole.Owner)) return;
        if (ctx.Users.FirstOrDefault(u => u.Username == "admin") is not { } admin)
        {
            Log.Warning("UserRepository: no account is an owner and there is no admin account to make one. "
                      + "Set an account's Role to Owner in the Users table.");
            return;
        }
        admin.Role = UserRole.Owner;
        admin.CustomRole = null;
        ctx.SaveChanges();
        Log.Information("UserRepository: no account was an owner; admin is the owner now.");
    }

    public IReadOnlyList<string> UsernamesWithRole(UserRole role)
    {
        using var ctx = CreateContext();
        return ctx.Users.AsNoTracking().Where(u => u.Role == role).Select(u => u.Username).ToList();
    }

    public UserData? GetUser(string username)
    {
        // Folded here, not in the predicate: EF cannot translate a call into our own code to SQL.
        var key = Normalize(username);
        using var ctx = CreateContext();
        var record = ctx.Users.AsNoTracking()
            .FirstOrDefault(u => u.Username == key);
        return record == null ? null : ToData(record);
    }

    private static UserData ToData(UserRecord record)
        => new()
        {
            Username = record.Username,
            PasswordHash = record.PasswordHash,
            Role = record.Role,
            CreatedUtc = AsUtc(record.CreatedUtc),
            LastLoginUtc = AsUtc(record.LastLoginUtc),
            LastLoginAddress = record.LastLoginAddress,
            FailedLogins = record.FailedLogins,
            LastFailedUtc = AsUtc(record.LastFailedUtc),
            LastFailedAddress = record.LastFailedAddress,
            RealName = record.RealName,
            Permissions = record.Permissions,
            CustomRole = record.CustomRole,
            PlayerState = record.PlayerState,
            Belongings = record.Belongings,
            BannedUtc = AsUtc(record.BannedUtc),
            BannedUntilUtc = AsUtc(record.BannedUntilUtc),
            BannedBy = record.BannedBy,
            BanReason = record.BanReason,
        };

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
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, WorkFactor),
            Role = role,
            CreatedUtc = DateTime.UtcNow,
        });
        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // Two registrations of one name at once: the primary key refused the second.
            Log.Warning("UserRepository: Attempted to register duplicate username '{User}'.", key);
            return false;
        }
        Log.Information("UserRepository: Registered user '{User}' with role {Role}.", key, role);
        return true;
    }

    public bool VerifyPassword(string username, string password)
    {
        var user = GetUser(username);
        if (user == null)
        {
            BCrypt.Net.BCrypt.Verify(password, _dummyHash);
            return false;
        }
        bool ok;
        try { ok = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash); }
        catch (SaltParseException)
        {
            Log.Error("UserRepository: the stored hash for '{User}' is not a bcrypt hash.", user.Username);
            return false;
        }
        if (ok && BCrypt.Net.BCrypt.PasswordNeedsRehash(user.PasswordHash, WorkFactor))
            Update(user.Username, r => r.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, WorkFactor));
        return ok;
    }

    public void RecordLogin(string username, string address, DateTime utc) => Update(username, r =>
    {
        r.LastLoginUtc = utc;
        r.LastLoginAddress = Clip(address);
        r.FailedLogins = 0;
    });

    public void RecordFailedLogin(string username, string address, DateTime utc) => Update(username, r =>
    {
        r.FailedLogins++;
        r.LastFailedUtc = utc;
        r.LastFailedAddress = Clip(address);
    });

    public bool SetRealName(string username, string? realName)
        => Update(username, r => r.RealName = string.IsNullOrWhiteSpace(realName) ? null : Clip(realName.Trim()));

    public bool SetRole(string username, UserRole role) => Update(username, r => r.Role = role);

    public bool SetCustomRole(string username, string? role)
        => Update(username, r => r.CustomRole = string.IsNullOrWhiteSpace(role) ? null : role);

    public int ClearCustomRole(string role)
    {
        using var ctx = CreateContext();
        var holders = ctx.Users.Where(u => u.CustomRole == role).ToList();
        foreach (var u in holders) u.CustomRole = null;
        ctx.SaveChanges();
        return holders.Count;
    }

    public bool SetGrants(string username, string grants)
        => Update(username, r => r.Permissions = string.IsNullOrEmpty(grants) ? null : grants);

    public bool SavePlayer(string username, string? state, string? belongings) => Update(username, r =>
    {
        r.PlayerState = state;
        r.Belongings = belongings;
    });

    public string? TakeBelongings(string username)
    {
        string? taken = null;
        Update(username, r =>
        {
            taken = r.Belongings;
            r.Belongings = null;
        });
        return taken;
    }

    public bool SetBan(string username, DateTime atUtc, DateTime? untilUtc, string by, string? reason) => Update(username, r =>
    {
        r.BannedUtc = atUtc;
        r.BannedUntilUtc = untilUtc;
        r.BannedBy = Clip(by);
        r.BanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    });

    public bool ClearBan(string username)
    {
        bool was = false;
        bool found = Update(username, r =>
        {
            was = r.BannedUtc != null;
            r.BannedUtc = null;
            r.BannedUntilUtc = null;
            r.BannedBy = null;
            r.BanReason = null;
        });
        return found && was;
    }

    public IReadOnlyList<UserData> Banned()
    {
        using var ctx = CreateContext();
        return ctx.Users.AsNoTracking().Where(u => u.BannedUtc != null).OrderBy(u => u.Username)
                  .AsEnumerable().Select(ToData).ToList();
    }

    private bool Update(string username, Action<UserRecord> change)
    {
        var key = Normalize(username);
        using var ctx = CreateContext();
        var record = ctx.Users.FirstOrDefault(u => u.Username == key);
        if (record == null) return false;
        change(record);
        ctx.SaveChanges();
        return true;
    }

    private static string Clip(string text) => text.Length <= 64 ? text : text[..64];

    /// <summary>SQLite hands a DateTime back with no kind; everything written here is UTC.</summary>
    private static DateTime? AsUtc(DateTime? value)
        => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private AppDbContext CreateContext() => new(_options);

    /// <summary>Case-folds a username, invariantly: under a Turkish locale ToLower() maps 'I' to a
    /// dotless 'ı', and one account would have two keys.</summary>
    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
