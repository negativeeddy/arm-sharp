using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.Core.Infrastructure.Data;

public class ArmDbContext : DbContext
{
    public ArmDbContext(DbContextOptions<ArmDbContext> options) : base(options) { }

    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<ConfigSnapshot> ConfigSnapshots => Set<ConfigSnapshot>();
    public DbSet<SystemDrive> SystemDrives => Set<SystemDrive>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<User> Users => Set<User>();
    public DbSet<SystemInfo> SystemInfos => Set<SystemInfo>();
    public DbSet<UiSettings> UiSettings => Set<UiSettings>();
    public DbSet<DiscMetadata> DiscMetadata => Set<DiscMetadata>();
    public DbSet<DiscTrack> DiscTracks => Set<DiscTrack>();
    public DbSet<DiscTrackStream> DiscTrackStreams => Set<DiscTrackStream>();
    public DbSet<RipperSettings> RipperSettings => Set<RipperSettings>();
    public DbSet<DiscDbMapping> DiscDbMappings => Set<DiscDbMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("jobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ArmVersion).HasMaxLength(20);
            entity.Property(e => e.CrcId).HasMaxLength(63);
            entity.Property(e => e.LogFile).HasMaxLength(256);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Stage).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Title).HasMaxLength(256);
            entity.Property(e => e.TitleAuto).HasMaxLength(256);
            entity.Property(e => e.TitleManual).HasMaxLength(256);
            entity.Property(e => e.Year).HasMaxLength(4);
            entity.Property(e => e.YearAuto).HasMaxLength(4);
            entity.Property(e => e.YearManual).HasMaxLength(4);
            entity.Property(e => e.VideoType).HasMaxLength(20);
            entity.Property(e => e.VideoTypeAuto).HasMaxLength(20);
            entity.Property(e => e.VideoTypeManual).HasMaxLength(20);
            entity.Property(e => e.ImdbId).HasMaxLength(15);
            entity.Property(e => e.ImdbIdAuto).HasMaxLength(15);
            entity.Property(e => e.ImdbIdManual).HasMaxLength(15);
            entity.Property(e => e.PosterUrl).HasMaxLength(256);
            entity.Property(e => e.PosterUrlAuto).HasMaxLength(256);
            entity.Property(e => e.PosterUrlManual).HasMaxLength(256);
            entity.Property(e => e.DevPath).HasMaxLength(15);
            entity.Property(e => e.DiscFingerprint).HasMaxLength(128);
            entity.Property(e => e.CompletedStages).HasMaxLength(256);
            entity.Property(e => e.DiscDbHash).HasMaxLength(64);
            entity.Property(e => e.SeriesTmdbId);
            entity.Property(e => e.SeasonNumber);
            entity.Ignore(e => e.MakeMkvProgress);
            entity.Ignore(e => e.TranscodeProgress);
            entity.Ignore(e => e.ProgressMessage);
            entity.Property(e => e.MountPoint).HasMaxLength(20);
            entity.Property(e => e.Label).HasMaxLength(256);
            entity.Property(e => e.Path).HasMaxLength(256);
            entity.Property(e => e.StageErrors);
            entity.Property(e => e.JobLength).HasMaxLength(12);
            entity.Property(e => e.DiscType).HasConversion<string>().HasMaxLength(20);
            entity.HasMany(e => e.Tracks).WithOne(t => t.Job).HasForeignKey(t => t.JobId);
            entity.HasOne(e => e.Config).WithOne(c => c.Job).HasForeignKey<ConfigSnapshot>(c => c.JobId);
        });

        modelBuilder.Entity<Track>(entity =>
        {
            entity.ToTable("tracks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TrackNumber).HasMaxLength(4);
            entity.Property(e => e.AspectRatio).HasMaxLength(20);
            entity.Property(e => e.BaseName).HasMaxLength(256);
            entity.Property(e => e.FileName).HasMaxLength(256);
            entity.Property(e => e.OrigFileName).HasMaxLength(256);
            entity.Property(e => e.NewFileName).HasMaxLength(256);
            entity.Property(e => e.Status).HasMaxLength(32);
            entity.Property(e => e.Source).HasMaxLength(32);
            entity.Property(e => e.EpisodeTitle).HasMaxLength(256);
            entity.Property(e => e.ContentType).HasMaxLength(32);
            entity.Property(e => e.DiscDbItemSlug).HasMaxLength(128);
        });

        modelBuilder.Entity<ConfigSnapshot>(entity =>
        {
            entity.ToTable("config");
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<SystemDrive>(entity =>
        {
            entity.ToTable("system_drives");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SerialId).HasMaxLength(100);
            entity.Property(e => e.Maker).HasMaxLength(100);
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.Serial).HasMaxLength(100);
            entity.Property(e => e.Mount).HasMaxLength(100);
            entity.Property(e => e.Firmware).HasMaxLength(10);
            entity.Property(e => e.DriveMode).HasMaxLength(100);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(256);
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("notifications");
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<SystemInfo>(entity =>
        {
            entity.ToTable("system_info");
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<UiSettings>(entity =>
        {
            entity.ToTable("ui_settings");
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<DiscMetadata>(entity =>
        {
            entity.ToTable("disc_metadata");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Fingerprint).HasMaxLength(128);
            entity.HasIndex(e => e.Fingerprint).IsUnique();
            entity.Property(e => e.VolumeLabel).HasMaxLength(256);
            entity.Property(e => e.DiscType).HasMaxLength(20);
            entity.HasMany(e => e.Tracks).WithOne(t => t.DiscMetadata).HasForeignKey(t => t.DiscMetadataId);
        });

        modelBuilder.Entity<DiscTrack>(entity =>
        {
            entity.ToTable("disc_tracks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TrackNumber).HasMaxLength(4);
            entity.Property(e => e.FileName).HasMaxLength(256);
            entity.Property(e => e.AspectRatio).HasMaxLength(20);
            entity.Property(e => e.Resolution).HasMaxLength(20);
            entity.HasMany(e => e.Streams).WithOne(s => s.DiscTrack).HasForeignKey(s => s.DiscTrackId);
        });

        modelBuilder.Entity<DiscTrackStream>(entity =>
        {
            entity.ToTable("disc_track_streams");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StreamType).HasMaxLength(10);
            entity.Property(e => e.LanguageCode).HasMaxLength(10);
            entity.Property(e => e.Codec).HasMaxLength(50);
        });

        modelBuilder.Entity<RipperSettings>(entity =>
        {
            entity.ToTable("ripper_settings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SettingsJson);
        });

        modelBuilder.Entity<DiscDbMapping>(entity =>
        {
            entity.ToTable("discdb_mappings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.ContentHash).IsUnique();
            entity.Property(e => e.MediaSlug).HasMaxLength(128);
            entity.Property(e => e.MediaTitle).HasMaxLength(256);
            entity.Property(e => e.MediaYear).HasMaxLength(4);
            entity.Property(e => e.MediaType).HasMaxLength(20);
            entity.Property(e => e.ImageUrl).HasMaxLength(256);
            entity.Property(e => e.TrackMappingsJson);
        });
    }
}
