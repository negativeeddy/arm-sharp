using ArmRipper.Core.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Route("notifications")]
public class NotificationsController(ArmDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var notifications = await db.Notifications
            .OrderByDescending(n => n.Timestamp)
            .Take(50)
            .ToListAsync();

        return View(notifications);
    }

    [HttpGet("~/api/notifications/unread")]
    public async Task<IActionResult> UnreadCount()
    {
        var count = await db.Notifications.CountAsync(n => !n.Read);
        return Json(new { count });
    }

    [HttpPost("markread/{id}")]
    public async Task<IActionResult> MarkRead(int id)
    {
        var notification = await db.Notifications.FindAsync(id);
        if (notification is not null)
        {
            notification.Read = true;
            await db.SaveChangesAsync();
        }

        return RedirectToAction("Index");
    }
}
