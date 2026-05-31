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
}
