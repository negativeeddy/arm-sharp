using ArmRipper.Core.Models;

namespace ArmRipper.Core.Notifications;

public sealed class NullNotificationBroadcaster : INotificationBroadcaster
{
    public Task BroadcastAsync(Notification notification, CancellationToken ct = default)
        => Task.CompletedTask;
}
