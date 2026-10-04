using Microsoft.EntityFrameworkCore;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// EF Core context for server-side persistence (users, future: player progress).
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<UserRecord> Users => Set<UserRecord>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserRecord>(b =>
        {
            b.HasKey(u => u.Username);
            b.Property(u => u.Username).HasMaxLength(64);
            b.Property(u => u.PasswordHash).IsRequired();
            b.Property(u => u.Role).HasConversion<string>();
            b.Property(u => u.LastLoginAddress).HasMaxLength(64);
            b.Property(u => u.LastFailedAddress).HasMaxLength(64);
            b.Property(u => u.RealName).HasMaxLength(64);
        });
    }
}

/// <summary>
/// Persistent user record stored in SQLite.
/// </summary>
public class UserRecord
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Player;

    // Added 2026-10-02. EnsureCreated does not alter a table that already exists, so an older
    // database gets these columns from SqliteUserRepository.UpgradeSchema; keep the two in step.
    public DateTime? CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public string? LastLoginAddress { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LastFailedUtc { get; set; }
    public string? LastFailedAddress { get; set; }
    public string? RealName { get; set; }
    // Added 2026-10-03: single permissions granted on top of the role, comma separated (Permissions).
    public string? Permissions { get; set; }
    // Added 2026-10-03: a role an administrator made (RoleRepository), on top of Player. Null for none.
    public string? CustomRole { get; set; }
}
