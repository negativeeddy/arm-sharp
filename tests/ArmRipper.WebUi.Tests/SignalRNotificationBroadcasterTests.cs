using ArmRipper.Core.Models;
using ArmRipper.WebUi.Hubs;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace ArmRipper.WebUi.Tests;

public class SignalRNotificationBroadcasterTests
{
    [Fact]
    public async Task BroadcastAsync_SendsNotificationToAllClients()
    {
        var notification = new Notification
        {
            Id = 1,
            Timestamp = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc),
            EventType = "test",
            Message = "Hello from test",
            Read = false
        };

        var mockClientProxy = new Mock<IClientProxy>();
        mockClientProxy
            .Setup(p => p.SendCoreAsync("Notification",
                It.Is<object?[]>(a => a.Length == 1 && a[0] is Notification),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

        var mockHubContext = new Mock<IHubContext<NotificationHub>>();
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var broadcaster = new SignalRNotificationBroadcaster(mockHubContext.Object);
        await broadcaster.BroadcastAsync(notification, CancellationToken.None);

        mockClientProxy.Verify(
            p => p.SendCoreAsync("Notification",
                It.Is<object?[]>(a => a.Length == 1 && a[0] is Notification),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BroadcastAsync_WithCancellationToken_PassesItThrough()
    {
        var notification = new Notification { Id = 2, Message = "test" };
        var cts = new CancellationTokenSource();

        var mockClientProxy = new Mock<IClientProxy>();
        mockClientProxy
            .Setup(p => p.SendCoreAsync("Notification",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

        var mockHubContext = new Mock<IHubContext<NotificationHub>>();
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var broadcaster = new SignalRNotificationBroadcaster(mockHubContext.Object);
        await broadcaster.BroadcastAsync(notification, cts.Token);

        mockClientProxy.Verify(
            p => p.SendCoreAsync("Notification",
                It.IsAny<object?[]>(),
                cts.Token),
            Times.Once);
    }
}
