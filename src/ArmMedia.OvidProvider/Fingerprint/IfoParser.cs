using System.Buffers.Binary;
using System.Text;

namespace ArmMedia.OvidProvider.Fingerprint;

/// <summary>
/// Pure-C# IFO binary parser for DVD VIDEO_TS.IFO (VMG) and VTS_XX_0.IFO files.
/// Extracts the structural metadata needed for OVID-DVD-1 fingerprinting:
/// VTS count, title count, PGC durations, chapter counts, audio/subtitle streams.
///
/// Reference: DVD-Video specification (ECMA-267 / ISO 9660 + UDF bridge).
/// IFO files use big-endian byte order throughout.
/// </summary>
public static class IfoParser
{
    private const int SectorSize = 2048;
    private static readonly byte[] VmgId = Encoding.UTF8.GetBytes("DVDVIDEO-VMG");
    private static readonly byte[] VtsId = Encoding.UTF8.GetBytes("DVDVIDEO-VTS");

    // Audio codec mapping (VTS audio stream coding mode → codec name)
    private static readonly Dictionary<int, string> AudioCodecMap = new()
    {
        [0] = "AC3",
        [2] = "MPEG-1",
        [3] = "MPEG-2",
        [4] = "LPCM",
        [6] = "DTS",
    };

    /// <summary>
    /// Parse a VIDEO_TS.IFO (VMG) binary blob.
    /// </summary>
    public static VmgInfo ParseVmg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x100)
            throw new ArgumentException(
                $"VMG data too short ({data.Length} bytes); minimum ~256 bytes required");

        if (!data[..12].SequenceEqual(VmgId))
            throw new ArgumentException("Invalid VMG identifier — missing DVDVIDEO-VMG header.");

        var vtsCount = BinaryPrimitives.ReadUInt16BigEndian(data[0x3E..]);
        var titleCount = 0;

        // TT_SRPT sector offset at 0x00C4 (4 bytes BE)
        var ttSrptSector = BinaryPrimitives.ReadUInt32BigEndian(data[0xC4..]);
        var ttSrptOffset = (int)ttSrptSector * SectorSize;

        if (ttSrptOffset + 2 <= data.Length)
        {
            titleCount = BinaryPrimitives.ReadUInt16BigEndian(data[ttSrptOffset..]);
        }

        return new VmgInfo
        {
            VtsCount = vtsCount,
            TitleCount = titleCount,
        };
    }

    /// <summary>
    /// Parse a VTS_XX_0.IFO binary blob.
    /// </summary>
    public static VtsInfo ParseVts(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x100)
            throw new ArgumentException(
                $"VTS data too short ({data.Length} bytes); minimum ~256 bytes required");

        if (!data[..12].SequenceEqual(VtsId))
            throw new ArgumentException("Invalid VTS identifier — missing DVDVIDEO-VTS header.");

        var audioStreams = ParseAudioStreams(data);
        var subtitleStreams = ParseSubtitleStreams(data);

        // VTS_PGCI sector offset at 0x00CC (4 bytes BE)
        var pgciSector = BinaryPrimitives.ReadUInt32BigEndian(data[0xCC..]);
        var pgciOffset = (int)pgciSector * SectorSize;

        var pgcList = ParsePgci(data, pgciOffset);

        return new VtsInfo
        {
            PgcList = pgcList,
            AudioStreams = audioStreams,
            SubtitleStreams = subtitleStreams,
        };
    }

    /// <summary>
    /// Parse audio stream attributes from VTS IFO at offset 0x0200.
    /// </summary>
    private static IReadOnlyList<AudioStream> ParseAudioStreams(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x202)
            return Array.Empty<AudioStream>();

        var streamCount = BinaryPrimitives.ReadUInt16BigEndian(data[0x200..]);
        if (streamCount == 0)
            return Array.Empty<AudioStream>();

        var streams = new List<AudioStream>(streamCount);
        const int audioBaseOffset = 0x204; // count(2) + reserved(2)

        for (int i = 0; i < streamCount; i++)
        {
            var offset = audioBaseOffset + i * 8;
            if (offset + 8 > data.Length)
                break;

            var codingByte = data[offset];
            var codingMode = (codingByte >> 5) & 0x07;
            var codec = AudioCodecMap.GetValueOrDefault(codingMode, $"unknown({codingMode})");

            // Language code: 2 bytes at offset + 2
            var langHi = data[offset + 2];
            var langLo = data[offset + 3];
            var language = langHi > 0x20 && langHi < 0x7F && langLo > 0x20 && langLo < 0x7F
                ? $"{(char)langHi}{(char)langLo}"
                : "";

            // Channels: low 3 bits of byte 1 = channels - 1
            var channels = (data[offset + 1] & 0x07) + 1;

            streams.Add(new AudioStream
            {
                Codec = codec,
                Language = language,
                Channels = channels,
            });
        }

        return streams;
    }

    /// <summary>
    /// Parse subtitle stream attributes from VTS IFO at offset 0x0254.
    /// </summary>
    private static IReadOnlyList<SubtitleStream> ParseSubtitleStreams(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x256)
            return Array.Empty<SubtitleStream>();

        var streamCount = BinaryPrimitives.ReadUInt16BigEndian(data[0x254..]);
        if (streamCount == 0)
            return Array.Empty<SubtitleStream>();

        var streams = new List<SubtitleStream>(streamCount);
        const int subBaseOffset = 0x258; // after count(2) + reserved(2)

        for (int i = 0; i < streamCount; i++)
        {
            var offset = subBaseOffset + i * 6;
            if (offset + 6 > data.Length)
                break;

            var langHi = data[offset + 2];
            var langLo = data[offset + 3];
            var language = langHi > 0x20 && langHi < 0x7F && langLo > 0x20 && langLo < 0x7F
                ? $"{(char)langHi}{(char)langLo}"
                : "";

            streams.Add(new SubtitleStream { Language = language });
        }

        return streams;
    }

    /// <summary>
    /// Parse the VTS_PGCI (Program Chain Information) table.
    /// </summary>
    private static IReadOnlyList<PgcInfo> ParsePgci(ReadOnlySpan<byte> data, int pgciOffset)
    {
        if (pgciOffset + 8 > data.Length)
            return Array.Empty<PgcInfo>();

        var pgcCount = BinaryPrimitives.ReadUInt16BigEndian(data[pgciOffset..]);
        if (pgcCount == 0)
            return Array.Empty<PgcInfo>();

        var pgcs = new List<PgcInfo>(pgcCount);
        var searchPtrBase = pgciOffset + 8; // after header (2+2+4 bytes)

        for (int i = 0; i < pgcCount; i++)
        {
            var entryOffset = searchPtrBase + i * 8;
            if (entryOffset + 8 > data.Length)
                break;

            // PGC block offset relative to pgci_offset
            var pgcRelOffset = BinaryPrimitives.ReadUInt32BigEndian(data[(entryOffset + 4)..]);
            var pgcAbsOffset = pgciOffset + (int)pgcRelOffset;

            if (pgcAbsOffset + 8 > data.Length)
                break;

            // nr_of_programs at PGC+0x02 (= chapter count)
            var nrOfPrograms = data[pgcAbsOffset + 0x02];
            var chapterCount = nrOfPrograms;

            // Playback time: 4 bytes BCD at PGC+0x04
            var timeBytes = data.Slice(pgcAbsOffset + 0x04, 4);
            var duration = DecodeBcdTime(timeBytes);

            pgcs.Add(new PgcInfo
            {
                DurationSeconds = duration,
                ChapterCount = chapterCount,
            });
        }

        return pgcs;
    }

    /// <summary>
    /// Decode a 4-byte BCD-encoded PGC playback time to total seconds.
    /// </summary>
    private static int DecodeBcdTime(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4)
            throw new ArgumentException("BCD time requires 4 bytes.");

        var hours = DecodeBcdByte(b[0]);
        var minutes = DecodeBcdByte(b[1]);
        var seconds = DecodeBcdByte(b[2]);
        // byte 3: frames — ignored per spec (round to whole seconds)
        return hours * 3600 + minutes * 60 + seconds;
    }

    /// <summary>
    /// Decode a single BCD-encoded byte. Invalid nibbles (>9) clamped to 0.
    /// </summary>
    private static int DecodeBcdByte(int b)
    {
        var hi = (b >> 4) & 0x0F;
        var lo = b & 0x0F;
        if (hi > 9) hi = 0;
        if (lo > 9) lo = 0;
        return hi * 10 + lo;
    }
}
