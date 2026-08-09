using ArmRipper.Core.Rip;

namespace ArmRipper.Core.Tests.Rip;

public sealed class HandBrakeServiceGpuPrefixTests
{
    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-01: null gpuIndex → no prefix, not missing
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_NullGpuIndex_ReturnsEmptyPrefixAndNotMissing()
    {
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(gpuIndex: null);

        Assert.Equal("", prefix);
        Assert.False(missing);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-02: GPU 0 exists → CUDA_VISIBLE_DEVICES=0 prefix
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_GpuExists_ReturnsCorrectPrefixAndNotMissing()
    {
        var deviceExists = new FakeDeviceExists(new HashSet<int> { 0 });
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 0, deviceExists: deviceExists.Check);

        Assert.Equal("CUDA_VISIBLE_DEVICES=0 ", prefix);
        Assert.False(missing);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-03: GPU 1 doesn't exist → empty prefix, missing=true
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_GpuMissing_ReturnsEmptyPrefixAndIsMissing()
    {
        var deviceExists = new FakeDeviceExists(new HashSet<int> { 0 });
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 1, deviceExists: deviceExists.Check);

        Assert.Equal("", prefix);
        Assert.True(missing);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-04: GPU removed between job creation and transcode
    //                ConfigSnapshot froze GpuIndex=1, but GPU 1 was pulled.
    //                Should fall back to auto-detect without crashing.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_GpuRemovedSinceJobFrozen_ReturnsEmptyPrefixAndIsMissing()
    {
        // Simulates: user had GPU 0 & GPU 1, created job with GpuIndex=1,
        // then removed GPU 1 before transcode started.
        var deviceExists = new FakeDeviceExists(new HashSet<int> { 0 }); // only GPU 0 remains
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 1, deviceExists: deviceExists.Check);

        Assert.Equal("", prefix);
        Assert.True(missing); // signals caller to log a warning
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-05: GPU 0 removed, GPU 1 present — uses GPU 1 correctly
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_Gpu1Available_ReturnsCorrectPrefix()
    {
        var deviceExists = new FakeDeviceExists(new HashSet<int> { 1 });
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 1, deviceExists: deviceExists.Check);

        Assert.Equal("CUDA_VISIBLE_DEVICES=1 ", prefix);
        Assert.False(missing);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-06: both GPUs available, GpuIndex=0 → uses GPU 0
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_BothGpusAvailable_Gpu0Selected_ReturnsCorrectPrefix()
    {
        var deviceExists = new FakeDeviceExists(new HashSet<int> { 0, 1 });
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 0, deviceExists: deviceExists.Check);

        Assert.Equal("CUDA_VISIBLE_DEVICES=0 ", prefix);
        Assert.False(missing);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-07: both GPUs available, GpuIndex=1 → uses GPU 1
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_BothGpusAvailable_Gpu1Selected_ReturnsCorrectPrefix()
    {
        var deviceExists = new FakeDeviceExists(new HashSet<int> { 0, 1 });
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 1, deviceExists: deviceExists.Check);

        Assert.Equal("CUDA_VISIBLE_DEVICES=1 ", prefix);
        Assert.False(missing);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-08: no GPUs at all (driver not loaded, container without
    //               --gpus), GpuIndex=0 → empty prefix, missing=true
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_NoGpusAvailable_ReturnsEmptyPrefixAndIsMissing()
    {
        var deviceExists = new FakeDeviceExists([]); // zero GPUs
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 0, deviceExists: deviceExists.Check);

        Assert.Equal("", prefix);
        Assert.True(missing);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-09: high GPU index (e.g. multi-GPU workstation with GPU 3)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_HighGpuIndexExists_ReturnsCorrectPrefix()
    {
        var deviceExists = new FakeDeviceExists(new HashSet<int> { 3 });
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 3, deviceExists: deviceExists.Check);

        Assert.Equal("CUDA_VISIBLE_DEVICES=3 ", prefix);
        Assert.False(missing);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TC-HB-GPU-10: high GPU index that's missing
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetGpuPrefix_HighGpuIndexMissing_ReturnsEmptyPrefixAndIsMissing()
    {
        var deviceExists = new FakeDeviceExists(new HashSet<int> { 0 });
        var (prefix, missing) = HandBrakeService.GetGpuPrefix(
            gpuIndex: 7, deviceExists: deviceExists.Check);

        Assert.Equal("", prefix);
        Assert.True(missing);
    }

    // ── Helper ───────────────────────────────────────────────────────────

    /// <summary>Fake device-existence check for deterministic unit tests.</summary>
    private sealed class FakeDeviceExists(HashSet<int> presentGpus)
    {
        public bool Check(string path)
        {
            // path is e.g. "/dev/nvidia0", extract the trailing digit
            var name = System.IO.Path.GetFileName(path.AsSpan());
            if (name.StartsWith("nvidia") && int.TryParse(name[6..], out var idx))
                return presentGpus.Contains(idx);
            return false;
        }
    }
}
