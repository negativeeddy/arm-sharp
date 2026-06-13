namespace ArmRipper.Core.Infrastructure;

public interface IBackgroundRipService
{
    void StartRip(string devPath, CancellationToken ct = default);
    void CancelRip(string devPath);
}
