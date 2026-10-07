using Microsoft.EntityFrameworkCore;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>The accounts database (SQLite, through EF Core).</summary>
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

/// <summary>A row of the accounts table. The fields are described on <see cref="UserData"/>.</summary>
public class UserRecord
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Player;

    // EnsureCreated does not alter a table that already exists: every column below reaches an older
    // database through SqliteUserRepository.UpgradeSchema. Keep the two in step.
    public DateTime? CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public string? LastLoginAddress { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LastFailedUtc { get; set; }
    public string? LastFailedAddress { get; set; }
    public string? RealName { get; set; }
    public string? Permissions { get; set; }
    public string? CustomRole { get; set; }
    public string? PlayerState { get; set; }
    public string? Belongings { get; set; }
}
