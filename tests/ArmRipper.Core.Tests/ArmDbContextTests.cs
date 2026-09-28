using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.Core.Tests;

/// <summary>
/// Tests for the atomic stage-marking helper on <see cref="ArmDbContext"/>,
/// which protects Job.CompletedStages against concurrent read-modify-write races.
/// </summary>
public sealed class ArmDbContextTests
{
    private static (SqliteConnection Connection, DbContextOptions<ArmDbContext> Options) CreateSharedDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ArmDbContext>()
            .UseSqlite(connection)
            .Options;
        return (connection, options);
    }

    private static async Task<int> SeedJobAsync(DbContextOptions<ArmDbContext> options, string? completedStages = null)
    {
        await using var seed = new ArmDbContext(options);
        seed.Database.EnsureCreated();
        var job = new Job
        {
            DevPath = "/dev/sr0",
            Status = JobState.Active,
            StartTime = DateTime.UtcNow,
            CompletedStages = completedStages
        };
        seed.Jobs.Add(job);
        await seed.SaveChangesAsync();
        return job.Id;
    }

    [Fact]
    public async Task MarkStageCompleteAsync_TwoContextsMarkDifferentStages_NeitherWriteIsLost()
    {
        var (connection, options) = CreateSharedDb();
        using (connection)
        {
            var jobId = await SeedJobAsync(options);

            // Simulate two concurrent writers, each with its own context that read
            // the job before either wrote (the classic read-modify-write race).
            await using var ctxA = new ArmDbContext(options);
            await using var ctxB = new ArmDbContext(options);

            var jobA = await ctxA.Jobs.FirstAsync(j => j.Id == jobId);
            var jobB = await ctxB.Jobs.FirstAsync(j => j.Id == jobId);

            await ctxA.MarkStageCompleteAsync(jobA, RipStage.Identify, CancellationToken.None);
            await ctxB.MarkStageCompleteAsync(jobB, RipStage.Rip, CancellationToken.None);

            await using var verify = new ArmDbContext(options);
            var persisted = await verify.Jobs.FirstAsync(j => j.Id == jobId);
            Assert.True(persisted.IsStageComplete(RipStage.Identify));
            Assert.True(persisted.IsStageComplete(RipStage.Rip));
        }
    }

    [Fact]
    public async Task MarkStageCompleteAsync_AlreadyMarked_IsIdempotentNoOp()
    {
        var (connection, options) = CreateSharedDb();
        using (connection)
        {
            var jobId = await SeedJobAsync(options, completedStages: "setup|identify");

            await using var ctx = new ArmDbContext(options);
            var job = await ctx.Jobs.FirstAsync(j => j.Id == jobId);

            await ctx.MarkStageCompleteAsync(job, RipStage.Identify, CancellationToken.None);

            await using var verify = new ArmDbContext(options);
            var persisted = await verify.Jobs.FirstAsync(j => j.Id == jobId);
            Assert.Equal("setup|identify", persisted.CompletedStages);
        }
    }

    [Fact]
    public async Task LegacyPersistedTvString_ReadsAsSeries()
    {
        // Regression test for issue #177: the "Tv" enum value was produced by OVID
        // ("tvshow") and DiscDb mappings. Legacy rows may still contain the
        // lowercase "tv" string — it must normalize to "Series" on read so the UI
        // and persisted value agree.
        var (connection, options) = CreateSharedDb();
        using (connection)
        {
            await using (var seed = new ArmDbContext(options))
            {
                seed.Database.EnsureCreated();
                seed.Jobs.Add(new Job
                {
                    DevPath = "/dev/sr0",
                    Status = JobState.Active,
                    StartTime = DateTime.UtcNow,
                    VideoType = VideoContentType.Series
                });
                await seed.SaveChangesAsync();

                // Force the legacy representation directly in SQL — a row written by
                // an earlier build would store the enum name as lowercase "tv".
                await seed.Database.ExecuteSqlRawAsync(
                    "UPDATE jobs SET VideoType = 'tv' WHERE DevPath = '/dev/sr0'");
            }

            await using var verify = new ArmDbContext(options);
            var persisted = await verify.Jobs.FirstAsync(j => j.DevPath == "/dev/sr0");
            Assert.Equal(VideoContentType.Series, persisted.VideoType);
        }
    }
}
