using ArmRipper.Core.Models;

namespace ArmRipper.Core.Notifications;

public interface INotificationBroadcaster
{
    Task BroadcastAsync(Notification notification, CancellationToken ct = default);
    Task BroadcastJobUpdateAsync(JobUpdate update, CancellationToken ct = default);
}
