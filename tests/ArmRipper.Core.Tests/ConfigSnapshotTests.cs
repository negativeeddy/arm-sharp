using ArmRipper.Core.Configuration;
using ArmRipper.Core.Models;

namespace ArmRipper.Core.Tests;

public sealed class ConfigSnapshotTests
{
    /// <summary>
    /// PreferWidescreen must be copied from settings into the per-job snapshot.
    /// When it isn't, the rip-time fallback in ArmRipperService
    /// (<c>config?.PreferWidescreen ?? settings.Value.PreferWidescreen</c>) reads
    /// the snapshot's default-false value, so the 16:9 preference is silently
    /// disabled and the main-feature selection picks the 4:3 cut on dual-cut discs.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromSettings_CopiesPreferWidescreenFromSettings(bool preferWidescreen)
    {
        var settings = new ArmSettings { PreferWidescreen = preferWidescreen };

        var snapshot = ConfigSnapshot.FromSettings(settings, jobId: 42);

        Assert.Equal(preferWidescreen, snapshot.PreferWidescreen);
        Assert.Equal(42, snapshot.JobId);
    }

    [Fact]
    public void FromSettings_DefaultsPreferWidescreenToTrue_WhenSettingUnchanged()
    {
        // ArmSettings defaults PreferWidescreen to true; the snapshot must match,
        // otherwise every job silently runs with widescreen preference off.
        var snapshot = ConfigSnapshot.FromSettings(new ArmSettings(), jobId: 1);

        Assert.True(snapshot.PreferWidescreen);
    }

    [Fact]
    public void FromSettings_CarriesForwardPreferWidescreenFromPreviousSnapshot()
    {
        var settings = new ArmSettings { PreferWidescreen = false };
        var previous = new ConfigSnapshot { PreferWidescreen = true };

        var snapshot = ConfigSnapshot.FromSettings(settings, jobId: 7, carryForward: previous);

        // Current settings win over the carried-forward value (the flag is not a
        // disc-specific behavioural override like MainFeature/RipMethod).
        Assert.False(snapshot.PreferWidescreen);
    }

    [Fact]
    public void FromSettings_CopiesCoreBehaviouralSettings()
    {
        var settings = new ArmSettings
        {
            MainFeature = true,
            Prevent99 = false,
            MinLength = 900,
            MaxLength = 20000,
            DiscDbEnabled = true,
        };

        var snapshot = ConfigSnapshot.FromSettings(settings, jobId: 3);

        Assert.True(snapshot.MainFeature);
        Assert.False(snapshot.Prevent99);
        Assert.Equal(900, snapshot.MinLength);
        Assert.Equal(20000, snapshot.MaxLength);
        Assert.True(snapshot.DiscDbEnabled);
    }
}
