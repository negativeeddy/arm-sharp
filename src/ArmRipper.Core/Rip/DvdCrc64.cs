namespace ArmRipper.Core.Rip;

internal static class DvdCrc64
{
    // Polynomial: x^63 + x^60 + x^57 + x^55 + x^54 + x^50 + x^49 + x^46 + x^41 + x^38 + x^37 +
    //             x^34 + x^32 + x^31 + x^30 + x^28 + x^25 + x^24 + x^21 + x^16 + x^13 + x^12 +
    //             x^11 + x^8 + x^7 + x^5 + x^2
    private const ulong Polynomial = 0x92c64265d32139a4;
    private const ulong InitialXor = 0xFFFFFFFFFFFFFFFF;

    // .NET ticks (100ns since 0001-01-01) to FILETIME (100ns since 1601-01-01)
    private static readonly long FileTimeEpochTicks = new DateTime(1601, 1, 1).Ticks;

    private static readonly ulong[] LookupTable = BuildLookupTable();

    private static ulong[] BuildLookupTable()
    {
        var table = new ulong[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = (ulong)i;
            for (int j = 0; j < 8; j++)
            {
                if ((value & 1) == 1)
                    value = (value >> 1) ^ Polynomial;
                else
                    value >>= 1;
            }
            table[i] = value;
        }
        return table;
    }

    public static string ComputeHash(byte[] data)
    {
        var crc = InitialXor;
        foreach (var b in data)
            crc = (crc >> 8) ^ LookupTable[(crc & 0xFF) ^ b];
        return crc.ToString("x16");
    }

    public static string ComputeHash(ReadOnlySpan<byte> data)
    {
        var crc = InitialXor;
        foreach (var b in data)
            crc = (crc >> 8) ^ LookupTable[(crc & 0xFF) ^ b];
        return crc.ToString("x16");
    }

    public static string Compute(string dvdPath)
    {
        var crc = InitialXor;
        var videoTs = Path.Combine(dvdPath, "VIDEO_TS");

        var files = Directory.GetFiles(videoTs).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);

            var fileTimeTicks = fileInfo.CreationTimeUtc.Ticks - FileTimeEpochTicks;
            var fileTimeBytes = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(fileTimeBytes, (ulong)fileTimeTicks);
            crc = Update(crc, fileTimeBytes);

            var sizeBytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)fileInfo.Length);
            crc = Update(crc, sizeBytes);

            var nameBytes = System.Text.Encoding.UTF8.GetBytes(fileInfo.Name + '\0');
            crc = Update(crc, nameBytes);
        }

        var vmgiPath = Path.Combine(videoTs, "VIDEO_TS.IFO");
        if (File.Exists(vmgiPath))
            crc = Update(crc, ReadFirst64K(vmgiPath));

        var vts01iPath = Path.Combine(videoTs, "VTS_01_0.IFO");
        if (File.Exists(vts01iPath))
            crc = Update(crc, ReadFirst64K(vts01iPath));

        return crc.ToString("x16");
    }

    private static ulong Update(ulong crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = (crc >> 8) ^ LookupTable[(crc & 0xFF) ^ b];
        return crc;
    }

    private static byte[] ReadFirst64K(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var size = Math.Min(bytes.Length, 0x10000);
        return bytes[..size];
    }
}
