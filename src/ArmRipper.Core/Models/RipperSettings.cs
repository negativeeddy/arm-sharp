namespace ArmRipper.Core.Models;

public sealed class RipperSettings
{
    public int Id { get; set; }
    public string SettingsJson { get; set; } = "{}";
}
