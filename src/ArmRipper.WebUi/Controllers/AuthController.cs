using Microsoft.AspNetCore.Mvc;

namespace ArmRipper.WebUi.Controllers;

[Route("auth")]
public class AuthController : Controller
{
    [HttpGet("login")]
    public IActionResult Login()
    {
        return View();
    }

    [HttpPost("login")]
    public IActionResult Login(string username, string password)
    {
        // Simple auth - placeholder
        return RedirectToAction("Index", "Home");
    }

    [HttpGet("logout")]
    public IActionResult Logout()
    {
        return RedirectToAction("Login");
    }
}
