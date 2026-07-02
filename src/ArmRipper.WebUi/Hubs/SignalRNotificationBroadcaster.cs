using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace ArmRipper.WebUi.Hubs;

public sealed class SignalRNotificationBroadcaster(IHubContext<NotificationHub> hubContext) : INotificationBroadcaster
{
    public async Task BroadcastAsync(Notification notification, CancellationToken ct = default)
    {
        await hubContext.Clients.All.SendAsync("Notification", notification, ct);
    }

    public async Task BroadcastJobUpdateAsync(JobUpdate update, CancellationToken ct = default)
    {
        await hubContext.Clients.All.SendAsync("JobUpdate", update, ct);
        // Also broadcast to the job-specific group for clients who subscribed
        await hubContext.Clients.Group($"job-{update.JobId}").SendAsync("JobUpdate", update, ct);
    }
}
