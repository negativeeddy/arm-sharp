using System.Runtime.CompilerServices;
using ArmRipper.Core.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Hubs;

public class NotificationHub(IOptions<ArmSettings> settings) : Hub
{
    private string LogPath => ArmPaths.GetLogPath(settings.Value);

    /// <summary>
    /// Streams log file content over SignalR.  Yields in ~16 KB chunks so large
    /// files don't exceed the default SignalR message size limit (32 KB).
    /// </summary>
    /// <param name="fileName">Log file name (e.g. "arm.log", "42.log").</param>
    /// <param name="mode">
    /// "full" — send the entire file from the start, then stream new lines.
    /// "tail" — send only the last ~4 KB, then stream new lines.
    /// </param>
    public async IAsyncEnumerable<string> StreamLog(
        string fileName,
        string mode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeFileName))
            yield break;

        var fullPath = Path.Combine(LogPath, safeFileName);
        if (!System.IO.File.Exists(fullPath))
            yield break;

        // Determine starting position based on mode
        var fi = new FileInfo(fullPath);
        long lastPos = mode == "tail"
            ? Math.Max(0, fi.Length - 4096)  // last ~4 KB for "tail"
            : 0;                              // entire file for "full"

        while (!cancellationToken.IsCancellationRequested)
        {
            fi.Refresh();
            if (fi.Exists && fi.Length > lastPos)
            {
                await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastPos, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);

                // Yield in ~16 KB chunks to stay well within SignalR's 32 KB limit
                var buffer = new char[16 * 1024];
                int bytesRead;
                while ((bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    yield return new string(buffer, 0, bytesRead);
                }

                lastPos = fi.Length;
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    /// <summary>Ping the hub to verify connectivity.</summary>
    public Task<string> Ping()
    {
        return Task.FromResult($"pong ({Context.ConnectionId})");
    }

    /// <summary>Subscribe to real-time updates for a specific job.</summary>
    public async Task SubscribeJob(int jobId)
    {
        var groupName = $"job-{jobId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    /// <summary>Unsubscribe from job-specific updates.</summary>
    public async Task UnsubscribeJob(int jobId)
    {
        var groupName = $"job-{jobId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
