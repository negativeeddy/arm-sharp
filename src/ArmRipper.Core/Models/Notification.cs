namespace ArmRipper.Core.Models;

public class Notification
{
    public int Id { get; init; }
    public DateTime Timestamp { get; set; }
    public string? EventType { get; set; }
    public string? Message { get; set; }
    public bool Read { get; set; }
}
