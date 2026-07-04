using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Services;

/// <summary>
/// Handles graceful shutdown of active rip jobs.
///
/// On startup:
///   - Marks any lingering jobs in non-terminal states as Stopping (resumable),
///     since the previous process holding their cancellation tokens is gone.
///
/// On shutdown:
///   - For each active rip, sets the job status to Stopping and saves to DB
///     BEFORE cancelling the token. This ensures the job can be resumed on restart.
/// </summary>
public sealed class ShutdownJobCancellationService : IHostedService
{
    private readonly IBackgroundRipService _backgroundRipService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ShutdownJobCancellationService> _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    public ShutdownJobCancellationService(
        IBackgroundRipService backgroundRipService,
        IServiceScopeFactory scopeFactory,
        ILogger<ShutdownJobCancellationService> logger,
        IHostApplicationLifetime appLifetime)
    {
        _backgroundRipService = backgroundRipService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // On startup: mark any non-terminal jobs as Stopping.
        // These jobs were running when the app last shut down — their CTS tokens
        // are gone, so they need to be marked resumable.
        _ = Task.Run(async () =>
        {
            try
            {
                await MarkOrphanedJobsAsStoppingAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark orphaned jobs on startup");
            }
        }, cancellationToken);

        // Register shutdown hook
        _appLifetime.ApplicationStopping.Register(OnShutdown);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void OnShutdown()
    {
        _logger.LogInformation("Graceful shutdown initiated — cancelling {Count} active job(s)",
            _backgroundRipService.ActiveCount);

        if (_backgroundRipService.ActiveCount == 0)
            return;

        // Step 1: Persist Stopping state for all active jobs BEFORE cancelling
        PersistStoppingState();

        // Step 2: Cancel all active CTS tokens
        var cancelledKeys = _backgroundRipService.CancelAll();

        _logger.LogInformation(
            "Shutdown: cancelled {Count} active job(s): {Keys}",
            cancelledKeys.Count, string.Join(", ", cancelledKeys));

        // Brief pause to let cancellation propagate through the pipeline
        Thread.Sleep(500);
    }

    /// <summary>
    /// On startup, find any jobs that are in non-terminal states and mark them as Stopping.
    /// These are jobs from a previous run whose host process no longer exists.
    /// </summary>
    private async Task MarkOrphanedJobsAsStoppingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();

        var orphanedJobs = await db.Jobs
            .Where(j => j.Status != JobState.Success
                     && j.Status != JobState.Failure
                     && j.Status != JobState.Cancelled
                     && j.Status != JobState.Stopping)
            .ToListAsync(ct);

        if (orphanedJobs.Count == 0)
            return;

        foreach (var job in orphanedJobs)
        {
            _logger.LogInformation(
                "Startup: marking orphaned job {JobId} ({Status}) as Stopping for resumption",
                job.Id, job.Status);
            job.Status = JobState.Stopping;
            job.StopTime ??= DateTime.UtcNow;

            // Write to the job's log file
            AppendToJobLog(job, $"Job discovered during startup — marked as Stopping (was {job.Status}). It can be resumed.");
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Startup: marked {Count} orphaned job(s) as Stopping (resumable)",
            orphanedJobs.Count);
    }

    /// <summary>
    /// During shutdown, write Stopping status to the DB for all active jobs.
    /// This runs synchronously on the shutdown thread.
    /// </summary>
    private void PersistStoppingState()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();

            var activeJobs = db.Jobs
                .Where(j => j.Status != JobState.Success
                         && j.Status != JobState.Failure
                         && j.Status != JobState.Cancelled
                         && j.Status != JobState.Stopping)
                .ToList();

            foreach (var job in activeJobs)
            {
                _logger.LogInformation(
                    "Shutdown: marking job {JobId} ({Status}, stage {Stage}) as Stopping",
                    job.Id, job.Status, job.Stage);
                job.Status = JobState.Stopping;
                job.StopTime ??= DateTime.UtcNow;
                job.ProgressMessage = "Stopped during shutdown — can be resumed";
            }

            db.SaveChanges();
            _logger.LogInformation(
                "Shutdown: saved Stopping state for {Count} job(s)", activeJobs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist Stopping state during shutdown");
        }
    }

    /// <summary>Append a timestamped line to the job's log file. Best-effort only.</summary>
    private static void AppendToJobLog(Job job, string message)
    {
        try
        {
            var logPath = job.GetLogFilePath();
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var entry = $"[{timestamp}] INFO: ShutdownJobCancellationService: {message}";
            System.IO.File.AppendAllText(logPath, entry + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Best-effort — non-critical failure
            Console.Error.WriteLine($"Failed to write to job log file: {ex.Message}");
        }
    }
}
