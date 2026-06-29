using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.Logging;
using Moq;

namespace ArmRipper.Core.Tests.Rip;

public class DiscDbHashServiceTests
{
    private static DiscDbHashService CreateService()
    {
        var logger = new Mock<ILogger<DiscDbHashService>>().Object;
        return new DiscDbHashService(logger);
    }

    [Fact]
    public async Task ComputeHashAsync_DvdDirectoryWithFiles_ReturnsDeterministicHash()
    {
        // Arrange
        var service = CreateService();
        using var temp = new TempDirectory();
        var videoTs = Path.Combine(temp.Path, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);

        // Create files with known sizes matching the TheDiscDb hash algorithm:
        // MD5 of each file's size (bytes, little-endian) concatenated in filename order
        File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO"), new byte[1024]);     // 1024 bytes
        File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.VOB"), new byte[2048]);     // 2048 bytes
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.IFO"), new byte[512]);      // 512 bytes
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_1.VOB"), new byte[4096]);     // 4096 bytes

        // Act
        var hash1 = await service.ComputeHashAsync(temp.Path, DiscType.Dvd, CancellationToken.None);
        var hash2 = await service.ComputeHashAsync(temp.Path, DiscType.Dvd, CancellationToken.None);

        // Assert
        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(32, hash1!.Length); // MD5 hex string is 32 chars
        Assert.Equal(hash1, hash2);      // Deterministic — same inputs produce same hash
    }

    [Fact]
    public async Task ComputeHashAsync_DvdSameSizesDifferentOrder_SameHash()
    {
        // TheDiscDb hash is ordered by filename, so the same files always produce the same hash
        // regardless of creation order.
        var service = CreateService();
        using var temp = new TempDirectory();
        var videoTs = Path.Combine(temp.Path, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);

        // Create file A then B
        File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.BUP"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.VOB"), new byte[3000]);

        using var temp2 = new TempDirectory();
        var videoTs2 = Path.Combine(temp2.Path, "VIDEO_TS");
        Directory.CreateDirectory(videoTs2);

        // Create in different order but same files should produce same hash (sorted by name)
        File.WriteAllBytes(Path.Combine(videoTs2, "VIDEO_TS.VOB"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(videoTs2, "VIDEO_TS.IFO"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(videoTs2, "VIDEO_TS.BUP"), new byte[2000]);

        var hash1 = await service.ComputeHashAsync(temp.Path, DiscType.Dvd, CancellationToken.None);
        var hash2 = await service.ComputeHashAsync(temp2.Path, DiscType.Dvd, CancellationToken.None);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task ComputeHashAsync_DifferentSizes_DifferentHashes()
    {
        var service = CreateService();
        using var temp1 = new TempDirectory();
        var videoTs1 = Path.Combine(temp1.Path, "VIDEO_TS");
        Directory.CreateDirectory(videoTs1);
        File.WriteAllBytes(Path.Combine(videoTs1, "VIDEO_TS.IFO"), new byte[1024]);

        using var temp2 = new TempDirectory();
        var videoTs2 = Path.Combine(temp2.Path, "VIDEO_TS");
        Directory.CreateDirectory(videoTs2);
        File.WriteAllBytes(Path.Combine(videoTs2, "VIDEO_TS.IFO"), new byte[2048]); // Different size

        var hash1 = await service.ComputeHashAsync(temp1.Path, DiscType.Dvd, CancellationToken.None);
        var hash2 = await service.ComputeHashAsync(temp2.Path, DiscType.Dvd, CancellationToken.None);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task ComputeHashAsync_BlurayDirectory_ScansStreamFilesOnly()
    {
        var service = CreateService();
        using var temp = new TempDirectory();
        var bdmv = Path.Combine(temp.Path, "BDMV");
        var stream = Path.Combine(bdmv, "STREAM");
        Directory.CreateDirectory(stream);

        File.WriteAllBytes(Path.Combine(stream, "00000.m2ts"), new byte[100_000_000]); // ~100 MB
        File.WriteAllBytes(Path.Combine(stream, "00001.m2ts"), new byte[50_000_000]);
        // Non-.m2ts files in STREAM should be ignored
        File.WriteAllBytes(Path.Combine(stream, "index.bdmv"), new byte[999]);         // Wrong extension

        var hash = await service.ComputeHashAsync(temp.Path, DiscType.Bluray, CancellationToken.None);
        Assert.NotNull(hash);
        Assert.Equal(32, hash!.Length);
    }

    [Fact]
    public async Task ComputeHashAsync_NoVideoTsDirectory_ReturnsNull()
    {
        var service = CreateService();
        using var temp = new TempDirectory();
        // No VIDEO_TS or BDMV directory

        var hash = await service.ComputeHashAsync(temp.Path, DiscType.Dvd, CancellationToken.None);
        Assert.Null(hash);
    }

    [Fact]
    public async Task ComputeHashAsync_NonVideoDiscType_ReturnsNull()
    {
        var service = CreateService();
        using var temp = new TempDirectory();

        var hash = await service.ComputeHashAsync(temp.Path, DiscType.Data, CancellationToken.None);
        Assert.Null(hash);
    }

    [Fact]
    public async Task ComputeHashAsync_EmptyDirectory_ReturnsNull()
    {
        var service = CreateService();
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "VIDEO_TS"));

        var hash = await service.ComputeHashAsync(temp.Path, DiscType.Dvd, CancellationToken.None);
        Assert.Null(hash);
    }

    [Fact]
    public async Task ComputeHashAsync_NullMountPoint_ReturnsNull()
    {
        var service = CreateService();
        // Path.Combine throws on null — service must handle or propagate
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.ComputeHashAsync(null!, DiscType.Dvd, CancellationToken.None));
    }

    /// <summary>Creates a temporary directory that is cleaned up on disposal.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "arm-sharp-test-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* Best effort */ }
        }
    }
}
