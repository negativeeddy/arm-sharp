namespace ArmRipper.Core.Models;

public class User
{
    public int Id { get; init; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
}
