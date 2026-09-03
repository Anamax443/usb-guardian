// ============================================================
// AppDbContext.cs
// Entity Framework DbContext – připojení na SQL Server
// Autentizace přes Windows Authentication (gMSA účet)
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Models;

namespace USBGuardian.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Computer> Computers => Set<Computer>();
    public DbSet<WhitelistDevice> WhitelistDevices => Set<WhitelistDevice>();
    public DbSet<WhitelistVersion> WhitelistVersions => Set<WhitelistVersion>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ActivityEntry> ActivityLog => Set<ActivityEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Unikátní klíče
        modelBuilder.Entity<Computer>()
            .HasIndex(c => c.Hostname).IsUnique();

        modelBuilder.Entity<WhitelistDevice>()
            .HasIndex(w => new { w.VendorId, w.ProductId, w.SerialNumber }).IsUnique();

        modelBuilder.Entity<WhitelistVersion>()
            .HasIndex(v => v.Version).IsUnique();

        // Indexy pro výkon
        modelBuilder.Entity<Incident>()
            .HasIndex(i => i.Timestamp);
        modelBuilder.Entity<Incident>()
            .HasIndex(i => i.Hostname);
        modelBuilder.Entity<Incident>()
            .HasIndex(i => i.Username);

        // Deník se čte skoro vždy "od konce a za posledních N hodin".
        modelBuilder.Entity<ActivityEntry>()
            .HasIndex(a => a.Timestamp);
        modelBuilder.Entity<ActivityEntry>()
            .HasIndex(a => new { a.Source, a.Timestamp });
    }
}
