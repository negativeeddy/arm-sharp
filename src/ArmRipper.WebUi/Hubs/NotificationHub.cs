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
        while (!cancellationToken.IsCancellationRequested)
        {
            var fi = new FileInfo(fullPath);
            if (fi.Exists && fi.Length > lastPos)
            {
                await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastPos, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                var newText = await reader.ReadToEndAsync(cancellationToken);

                if (newText.Length > 0)
                {
                    yield return newText;
                }
                lastPos = fi.Length;
            }

            await Task.Delay(1000, cancellationToken);
        }
    }
}
