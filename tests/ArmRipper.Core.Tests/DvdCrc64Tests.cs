using ArmRipper.Core.Rip;

namespace ArmRipper.Core.Tests;

public sealed class DvdCrc64Tests
{
    [Fact]
    public void ComputeHash_Empty_ReturnsAllF()
    {
        var result = DvdCrc64.ComputeHash([]);
        Assert.Equal("ffffffffffffffff", result);
    }

    [Fact]
    public void ComputeHash_HelloWorld_MatchesPython()
    {
        var data = "hello world"u8.ToArray();
        var result = DvdCrc64.ComputeHash(data);
        Assert.Equal("0400d4dd1671c811", result);
    }

    [Fact]
    public void ComputeHash_ArmTestData_MatchesPython()
    {
        var data = "ARM Test Data"u8.ToArray();
        var result = DvdCrc64.ComputeHash(data);
        Assert.Equal("6bf4882871be1c3c", result);
    }

    [Fact]
    public void ComputeHash_LargeData_IsStable()
    {
        var data = new byte[100_000];
        new Random(42).NextBytes(data);

        var first = DvdCrc64.ComputeHash(data);
        var second = DvdCrc64.ComputeHash(data);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeHash_DifferentData_DifferentHash()
    {
        var a = DvdCrc64.ComputeHash("The quick brown fox"u8);
        var b = DvdCrc64.ComputeHash("The quick brown fox."u8);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_CreatesConsistentHashForDirectory()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var videoTs = Path.Combine(tmpDir, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);

        try
        {
            var files = new Dictionary<string, byte[]>
            {
                ["VIDEO_TS.BUP"] = "BUP data here"u8.ToArray(),
                ["VIDEO_TS.IFO"] = new byte[100_000],
                ["VIDEO_TS.VOB"] = "VOB data"u8.ToArray(),
                ["VTS_01_0.BUP"] = "VTS BUP"u8.ToArray(),
                ["VTS_01_0.IFO"] = new byte[50_000],
                ["VTS_01_0.VOB"] = "VTS VOB"u8.ToArray(),
                ["VTS_01_1.VOB"] = "More VOB"u8.ToArray(),
            };

            var now = DateTime.UtcNow;
            foreach (var (name, content) in files)
            {
                var path = Path.Combine(videoTs, name);
                File.WriteAllBytes(path, content);
                File.SetCreationTimeUtc(path, now);
                File.SetLastWriteTimeUtc(path, now);
            }

            var first = DvdCrc64.Compute(tmpDir);
            var second = DvdCrc64.Compute(tmpDir);
            Assert.Equal(first, second);
            Assert.Matches("^[0-9a-f]{16}$", first);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void Compute_MatchesPythonPydvdid()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var videoTs = Path.Combine(tmpDir, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);

        try
        {
            var files = new Dictionary<string, byte[]>
            {
                ["VIDEO_TS.BUP"] = "BUP data here"u8.ToArray(),
                ["VIDEO_TS.IFO"] = new byte[100_000],
                ["VIDEO_TS.VOB"] = "VOB data"u8.ToArray(),
                ["VTS_01_0.BUP"] = "VTS BUP"u8.ToArray(),
                ["VTS_01_0.IFO"] = new byte[50_000],
                ["VTS_01_0.VOB"] = "VTS VOB"u8.ToArray(),
                ["VTS_01_1.VOB"] = "More VOB"u8.ToArray(),
            };

            // Fixed timestamp so CRC is reproducible
            var fixedTime = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            foreach (var (name, content) in files)
            {
                var path = Path.Combine(videoTs, name);
                File.WriteAllBytes(path, content);
                File.SetCreationTimeUtc(path, fixedTime);
                File.SetLastWriteTimeUtc(path, fixedTime);
            }

            // Cross-validated against Python pydvdid with identical inputs
            var result = DvdCrc64.Compute(tmpDir);
            Assert.Equal("571d8fb21eb8fe4b", result);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }
}
