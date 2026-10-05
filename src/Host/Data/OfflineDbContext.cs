using Microsoft.EntityFrameworkCore;
using PrecisionScoresDesktop.Host.Data.Entities;

namespace PrecisionScoresDesktop.Host.Data;

// EF Core context backed by a single SQLite file. The schema is
// deliberately minimal: four tables, all keyed by GUID or GUID
// composite. The migration story is "EnsureCreated on first run" —
// we don't ship schema migrations because the shape is stable and
// upstream (cloud) entity changes don't propagate here (DataJson is
// opaque to us).
public sealed class OfflineDbContext : DbContext
{
    public OfflineDbContext(DbContextOptions<OfflineDbContext> options) : base(options) { }

    public DbSet<CachedMatch> CachedMatches => Set<CachedMatch>();
    public DbSet<CachedShooter> CachedShooters => Set<CachedShooter>();
    public DbSet<OfflineScorecard> OfflineScorecards => Set<OfflineScorecard>();
    public DbSet<CachedProfile> CachedProfiles => Set<CachedProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CachedMatch>(e =>
        {
            e.HasKey(x => x.MatchId);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<CachedShooter>(e =>
        {
            e.HasKey(x => new { x.MatchId, x.ShooterId });
        });

        modelBuilder.Entity<OfflineScorecard>(e =>
        {
            e.HasKey(x => x.Id);
            // Natural key for upsert from EditScoreCard. SQLite does
            // support unique indexes.
            e.HasIndex(x => new { x.EventId, x.ShooterId, x.TargetId, x.CardNumber })
                .IsUnique();
            e.HasIndex(x => x.SyncedAt);
        });

        modelBuilder.Entity<CachedProfile>(e =>
        {
            e.HasKey(x => x.Email);
        });
    }
}
