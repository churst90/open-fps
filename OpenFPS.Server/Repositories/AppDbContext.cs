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
}
