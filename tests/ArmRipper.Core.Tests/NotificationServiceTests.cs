using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArmRipper.Core.Tests;

public sealed class NotificationServiceTests : IDisposable
{
    private readonly ArmDbContext _db;
    private readonly CliProcessRunner _runner;
    private readonly NotificationService _service;

    public NotificationServiceTests()
    {
        _db = TestHelpers.CreateDbContext();
        _runner = new CliProcessRunner(NullLogger<CliProcessRunner>.Instance);
        _service = new NotificationService(NullLogger<NotificationService>.Instance, _db, _runner, []);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task NotifyAsync_WithJob_AddsNotificationToDatabase()
    {
        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        _db.SaveChanges();

        await _service.NotifyAsync(job, "Test Title", "Test Body");

        var notifications = _db.Notifications.ToList();
        Assert.Single(notifications);
        Assert.Equal("Test Title", notifications[0].EventType);
        Assert.Equal("Test Body", notifications[0].Message);
        Assert.False(notifications[0].Read);
    }

    [Fact]
    public async Task NotifyAsync_WithoutJob_AddsNotificationToDatabase()
    {
        await _service.NotifyAsync(null, "Test Title", "Test Body");

        var notifications = _db.Notifications.ToList();
        Assert.Single(notifications);
        Assert.Equal("Test Title", notifications[0].EventType);
    }

    [Fact]
    public async Task NotifyEntryAsync_ForVideoDisc_CreatesEntryNotification()
    {
        var job = TestHelpers.CreateTestJob(j => j.DiscType = DiscType.Dvd);
        _db.Jobs.Add(job);
        _db.SaveChanges();

        await _service.NotifyEntryAsync(job);

        var notifications = _db.Notifications.ToList();
        Assert.NotEmpty(notifications);
        Assert.Contains(notifications, n => n.EventType!.Contains("New Job"));
        Assert.Contains(notifications, n => n.Message!.Contains("dvd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NotifyEntryAsync_ForMusicDisc_CreatesMusicNotification()
    {
        var job = TestHelpers.CreateTestJob(
            j => { j.DiscType = DiscType.Music; j.Label = "MusicAlbum"; });
        _db.Jobs.Add(job);
        _db.SaveChanges();

        await _service.NotifyEntryAsync(job);

        var notifications = _db.Notifications.ToList();
        Assert.NotEmpty(notifications);
        Assert.Contains(notifications, n => n.Message!.Contains("music CD"));
    }

    [Fact]
    public async Task NotifyEntryAsync_ForDataDisc_CreatesDataNotification()
    {
        var job = TestHelpers.CreateTestJob(j => j.DiscType = DiscType.Data);
        _db.Jobs.Add(job);
        _db.SaveChanges();

        await _service.NotifyEntryAsync(job);

        var notifications = _db.Notifications.ToList();
        Assert.NotEmpty(notifications);
        Assert.Contains(notifications, n => n.Message!.Contains("data disc"));
    }

    [Fact]
    public async Task NotifyEntryAsync_ForUnknownDiscType_Throws()
    {
        var job = TestHelpers.CreateTestJob(j => j.DiscType = DiscType.Unknown);
        _db.Jobs.Add(job);
        _db.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.NotifyEntryAsync(job));
    }
}
