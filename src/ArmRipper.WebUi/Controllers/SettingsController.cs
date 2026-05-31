using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Route("settings")]
public class SettingsController(ArmDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var drives = await db.SystemDrives.ToListAsync();
        return View(drives);
    }

    [HttpPost("save")]
    public async Task<IActionResult> Save(SystemDrive drive)
    {
        if (drive.Id > 0)
            db.SystemDrives.Update(drive);
        else
            db.SystemDrives.Add(drive);

        await db.SaveChangesAsync();
        return RedirectToAction("Index");
    }

    [HttpPost("drives")]
    public async Task<IActionResult> UpdateDrives()
    {
        // Rescan drives
        return RedirectToAction("Index");
    }
}
