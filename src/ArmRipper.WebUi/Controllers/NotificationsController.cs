using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("notifications")]
public class NotificationsController(ArmDbContext db, IEnumerable<INotificationBroadcaster> broadcasters) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? filter = "unread", CancellationToken ct = default)
    {
        var query = db.Notifications.AsQueryable();

        if (filter == "unread")
            query = query.Where(n => !n.Read);
        else if (filter == "read")
            query = query.Where(n => n.Read);

        var notifications = await query
            .OrderByDescending(n => n.Timestamp)
            .Take(50)
            .ToListAsync(ct);

        ViewBag.CurrentFilter = filter;
        return View(notifications);
    }

    [HttpGet("~/api/notifications/unread")]
    public async Task<IActionResult> UnreadCount(CancellationToken ct = default)
    {
        var count = await db.Notifications.CountAsync(n => !n.Read, ct);
        return Json(new { count });
    }

    [HttpPost("markread/{id}")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct = default)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (notification is not null)
        {
            notification.Read = true;
            await db.SaveChangesAsync(ct);
        }

        return RedirectToAction("Index");
    }

    [HttpPost("markallread")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct = default)
    {
        await db.Notifications
            .Where(n => !n.Read)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.Read, true), ct);

        return RedirectToAction("Index");
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestNotification(CancellationToken ct = default)
    {
        var notification = new Notification
        {
            Timestamp = DateTime.UtcNow,
            EventType = "Test Notification",
            Message = "This is a sample notification. If you can see this, notifications are working correctly!",
            Read = false
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);

        foreach (var broadcaster in broadcasters)
            await broadcaster.BroadcastAsync(notification, ct);

        return Ok(new { id = notification.Id });
    }
}
