using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Notifications;

public sealed class NotificationService(
    ILogger<NotificationService> logger,
    ArmDbContext db,
    CliProcessRunner runner,
    IEnumerable<INotificationBroadcaster> broadcasters)
{
    public const string NotifyTitle = "ARM notification";

    public async Task NotifyAsync(Job? job, string title, string body, CancellationToken ct = default)
    {
        var cfg = job?.Config;

        // Prepend site name if configured
        if (!string.IsNullOrEmpty(cfg?.InstallPath))
            title = $"[{cfg.InstallPath}] - {title}";

        // Append Job ID if configured
        if (cfg?.OmdbApiKey is not null && job is not null)
            title = $"{title} - {job.Id}";

        logger.LogDebug("Apprise message, title: {Title} body: {Body}", title, body);

        // Save to local DB
        var notification = new Notification
        {
            Timestamp = DateTime.UtcNow,
            EventType = title,
            Message = body
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);

        foreach (var broadcaster in broadcasters)
            await broadcaster.BroadcastAsync(notification, ct);

        // Bash notification
        await BashNotifyAsync(cfg, title, body, ct);

        // Remote notifications via HTTP
        await SendRemoteNotificationsAsync(cfg, title, body, ct);
    }

    private async Task BashNotifyAsync(ConfigSnapshot? cfg, string title, string body, CancellationToken ct)
    {
        var bashScript = cfg?.BashScript;
        if (string.IsNullOrEmpty(bashScript))
            return;

        try
        {
            await runner.RunAsync("bash", $"\"{bashScript}\" \"{title}\" \"{body}\"", timeoutMs: 30_000, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bash notification script failed");
        }
    }

    private async Task SendRemoteNotificationsAsync(ConfigSnapshot? cfg, string title, string body, CancellationToken ct)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        // Pushbullet
        if (!string.IsNullOrEmpty(cfg?.PbKey))
        {
            await SendPushbulletAsync(client, cfg.PbKey, title, body, ct);
        }

        // IFTTT
        if (!string.IsNullOrEmpty(cfg?.IftttKey))
        {
            await SendIftttAsync(client, cfg.IftttKey, string.Empty, title, body, ct);
        }

        // JSON URL
        if (!string.IsNullOrEmpty(cfg?.JsonUrl))
        {
            await SendJsonWebhookAsync(client, cfg.JsonUrl, title, body, ct);
        }
    }

    private async Task SendPushbulletAsync(HttpClient client, string apiKey, string title, string body, CancellationToken ct)
    {
        try
        {
            var payload = new { type = "note", title, body };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            await client.PostAsync("https://api.pushbullet.com/v2/pushes", content, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pushbullet notification failed");
        }
    }

    private async Task SendIftttAsync(HttpClient client, string key, string @event, string title, string body, CancellationToken ct)
    {
        try
        {
            var eventName = string.IsNullOrEmpty(@event) ? "arm_notification" : @event;
            var payload = new { value1 = title, value2 = body };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await client.PostAsync($"https://maker.ifttt.com/trigger/{eventName}/with/key/{key}", content, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "IFTTT notification failed");
        }
    }

    private async Task SendJsonWebhookAsync(HttpClient client, string url, string title, string body, CancellationToken ct)
    {
        try
        {
            var payload = new { title, body };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await client.PostAsync(url, content, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "JSON webhook notification failed");
        }
    }

    public async Task NotifyEntryAsync(Job job, CancellationToken ct = default)
    {
        var cfg = job.Config;
        var baseUrl = !string.IsNullOrEmpty(cfg?.UiBaseUrl)
            ? cfg.UiBaseUrl
            : $"http://{GetLocalIpAddress()}:{cfg?.WebServerPort ?? 8080}";

        var title = job.DiscType switch
        {
            DiscType.Dvd or DiscType.Bluray =>
                $"Found disc: {job.Title}. Disc type is {job.DiscType}. Main Feature is {cfg?.MainFeature}. "
                + $"Edit entry here: {baseUrl}/jobdetail?job_id={job.Id}",
            DiscType.Music =>
                $"Found music CD: {job.Label}. Ripping all tracks.",
            DiscType.Data =>
                "Found data disc. Copying data.",
            _ => throw new InvalidOperationException("Could not determine disc type")
        };

        var notification = new Notification
        {
            Timestamp = DateTime.UtcNow,
            EventType = $"New Job: {job.Id} has started. Disctype: {job.DiscType}",
            Message = $"New job has started to rip - {job.Label}, {job.DiscType} at {DateTime.Now}"
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);

        await NotifyAsync(job, NotifyTitle, title, ct);
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(ip) &&
                    !ip.ToString().StartsWith("172."))
                {
                    return ip.ToString();
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }
}
