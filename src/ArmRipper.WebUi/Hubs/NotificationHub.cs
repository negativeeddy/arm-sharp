using System.Runtime.CompilerServices;
using ArmRipper.Core.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Hubs;

public class NotificationHub(IOptions<ArmSettings> settings) : Hub
{
    private string LogPath => ArmPaths.GetLogPath(settings.Value);

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

        long lastPos = 0;
        const int bufferSize = 8192; // 8 KB chunks – safe for SignalR message size limits

        do
        {
            var fi = new FileInfo(fullPath);
            if (fi.Exists && fi.Length > lastPos)
            {
                await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastPos, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);

                var buffer = new char[bufferSize];
                int charsRead;
                while ((charsRead = await reader.ReadAsync(buffer, 0, bufferSize)) > 0)
                {
                    yield return new string(buffer, 0, charsRead);
                }
                lastPos = fi.Length;
            }

            // "full" mode: send the entire file content once and stop
            if (mode == "full")
                break;

            // "tail" mode: poll for new content every second
            await Task.Delay(1000, cancellationToken);
        }
        while (!cancellationToken.IsCancellationRequested);
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
