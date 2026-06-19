using ArmRipper.Core.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("notifications")]
public class NotificationsController(ArmDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? filter = "unread")
    {
        var query = db.Notifications.AsQueryable();

        if (filter == "unread")
            query = query.Where(n => !n.Read);
        else if (filter == "read")
            query = query.Where(n => n.Read);

        var notifications = await query
            .OrderByDescending(n => n.Timestamp)
            .Take(50)
            .ToListAsync();

        ViewBag.CurrentFilter = filter;
        return View(notifications);
    }

    [HttpGet("~/api/notifications/unread")]
    public async Task<IActionResult> UnreadCount()
    {
        var count = await db.Notifications.CountAsync(n => !n.Read);
        return Json(new { count });
    }

    [HttpPost("markread/{id}")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct = default)
    {
        var notification = await db.Notifications.FindAsync(id);
        if (notification is not null)
        {
            notification.Read = true;
            await db.SaveChangesAsync(ct);
        }

        return RedirectToAction("Index");
    }

    [HttpPost("markallread")]
    public async Task<IActionResult> MarkAllRead()
    {
        await db.Notifications
            .Where(n => !n.Read)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.Read, true));

        return RedirectToAction("Index");
    }
}
