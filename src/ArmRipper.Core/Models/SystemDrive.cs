namespace ArmRipper.Core.Models;

public class SystemDrive
{
    public int Id { get; init; }
    public string SerialId { get; set; } = string.Empty;
    public string? Maker { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? Mount { get; set; }
    public string? Firmware { get; set; }
    public int? Mdisc { get; set; }
    public bool ReadCd { get; set; }
    public bool ReadDvd { get; set; }
    public bool ReadBd { get; set; }
    public string? DriveMode { get; set; }
    public bool Stale { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? JobIdCurrent { get; set; }
    public int? JobIdPrevious { get; set; }
}
