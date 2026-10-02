using BCrypt.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// SQLite-backed implementation of <see cref="IUserRepository"/>.
/// Uses EF Core for safe, transactional credential storage.
/// Creates a fresh DbContext per operation to avoid threading issues with a shared context.
///
/// Passwords are bcrypt hashes: a per-password random salt is part of the hash, and the cost is
/// <see cref="WorkFactor"/> (2^12 rounds unless a test asks for less). BCrypt.Net compares the
/// computed hash in constant time. A hash made at a lower cost is rehashed at the current one the
/// next time its owner logs in.
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
    }

    private void EnsureDatabaseCreated()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
        UpgradeSchema(ctx);
        Log.Information("UserRepository: SQLite database ready.");
    }

    /// <summary>
    /// The columns added after the first release, with the SQL that adds each to an older table.
    /// The types are what EF Core would have created them as, so an upgraded table and a new one read
    /// the same.
    /// </summary>
    private static readonly (string Column, string Definition)[] AddedColumns =
    {
        ("CreatedUtc", "TEXT NULL"),
        ("LastLoginUtc", "TEXT NULL"),
        ("LastLoginAddress", "TEXT NULL"),
        ("FailedLogins", "INTEGER NOT NULL DEFAULT 0"),
        ("LastFailedUtc", "TEXT NULL"),
        ("LastFailedAddress", "TEXT NULL"),
        ("RealName", "TEXT NULL"),
    };

    /// <summary>
    /// Brings a database made by an older server up to the current table.
    ///
    /// EnsureCreated makes a new database whole but never touches one that exists, and the VPS has
    /// accounts in one. Each missing column is added with ALTER TABLE, which keeps every row. Before
    /// the first change the file is copied beside itself (openfps.db.before-YYYYMMDD-HHMMSS), so an
    /// upgrade that went wrong can be undone by hand.
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
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(string.IsNullOrEmpty(chosen) ? "admin123" : chosen, WorkFactor),
                Role = UserRole.Admin,
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
            Role = record.Role,
            CreatedUtc = AsUtc(record.CreatedUtc),
            LastLoginUtc = AsUtc(record.LastLoginUtc),
            LastLoginAddress = record.LastLoginAddress,
            FailedLogins = record.FailedLogins,
            LastFailedUtc = AsUtc(record.LastFailedUtc),
            LastFailedAddress = record.LastFailedAddress,
            RealName = record.RealName,
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
            // Two registrations of one name at once: the check above passed for both, and the
            // primary key refused the second. Same answer as the check.
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

    /// <summary>
    /// Case-folds a username for storage and lookup. Invariant, not current-culture: under a Turkish
    /// locale ToLower() maps 'I' to a dotless 'ı', so the same account name would hash to two different
    /// keys depending on where the server happens to be running.
    /// </summary>
    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
