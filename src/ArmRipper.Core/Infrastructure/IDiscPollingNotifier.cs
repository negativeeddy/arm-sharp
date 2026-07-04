namespace ArmRipper.Core.Infrastructure;

/// <summary>
/// Minimal interface that allows notifying the disc polling service
/// when relevant settings (DiscPollingEnabled) change at runtime.
/// The controller depends on this, not on the concrete hosted service.
/// </summary>
public interface IDiscPollingNotifier
{
    /// <summary>
    /// Called after ripper settings are saved so the polling service
    /// can re-evaluate whether the UeventMonitor should be running.
    /// </summary>
    void NotifySettingChanged();
}
