namespace ArmRipper.Core.Models;

public class SystemInfo
{
    public int Id { get; init; }
    public string Hostname { get; set; } = string.Empty;
    public string? CpuInfo { get; set; }
    public string? RamInfo { get; set; }
    public string? OsInfo { get; set; }
    public string? ArmVersion { get; set; }
}
