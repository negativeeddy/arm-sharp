using ArmRipper.Core.Rip;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Parses MakeMKV robot output lines into typed records.
/// Extracted from <see cref="MakeMkvService"/> for testability.
/// </summary>
public static class MakeMkvOutputParser
{
    public record ParsedLine(MakeMkvOutputType Type, object Data);

    public static ParsedLine? ParseLine(string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return null;

        var typeStr = line[..colonIdx];
        var content = line[(colonIdx + 1)..];

        if (!Enum.TryParse<MakeMkvOutputType>(typeStr, ignoreCase: true, out var type))
            return null;

        return type switch
        {
            MakeMkvOutputType.Msg => ParseMsg(content),
            MakeMkvOutputType.Drv => ParseDrv(content),
            MakeMkvOutputType.TCount => new ParsedLine(type, new Titles(int.Parse(content))),
            MakeMkvOutputType.CinFo => ParseCInfo(content),
            MakeMkvOutputType.TInfo => ParseTInfo(content),
            MakeMkvOutputType.SinFo => ParseSInfo(content),
            MakeMkvOutputType.PrgV => ParsePrgV(content),
            MakeMkvOutputType.PrgC => ParsePrgC(content),
            MakeMkvOutputType.PrgT => ParsePrgT(content),
            _ => null
        };
    }

    public static ParsedLine ParseMsg(string content)
    {
        var parts = SplitCsv(content);
        var code = int.Parse(parts[0]);
        var flags = int.Parse(parts[1]);
        var count = int.Parse(parts[2]);
        var message = parts[3];
        var sprintf = parts.Length > 4 ? parts[4] : "";
        var @params = parts.Length > 5 ? parts[5..] : [];

        return new ParsedLine(MakeMkvOutputType.Msg,
            new MakeMkvMessage(code, flags, count, message, sprintf, @params));
    }

    public static ParsedLine ParseDrv(string content)
    {
        var parts = SplitCsv(content);
        var index = int.Parse(parts[0]);
        var visible = int.Parse(parts[1]);
        var enabled = int.Parse(parts[2]) == 999;
        var flags = int.Parse(parts[3]);
        var info = parts[4];
        var disc = parts[5];
        var mount = parts.Length > 6 ? parts[6] : "";

        var driveVisible = (DriveVisible)visible;
        return new ParsedLine(MakeMkvOutputType.Drv, new DriveInfo(index, visible != 0, enabled, flags, info, disc, mount)
        {
            Attached = driveVisible != DriveVisible.NotAttached,
            Loaded = driveVisible is DriveVisible.Loaded or DriveVisible.Loading,
            IsOpen = driveVisible == DriveVisible.Open,
            MediaCd = flags == (int)DriveType.Cd,
            MediaDvd = flags == (int)DriveType.Dvd,
            MediaBd = flags is (int)DriveType.BdType1 or (int)DriveType.BdType2
        });
    }

    public static ParsedLine ParseCInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.CinFo,
            new CinFo(int.Parse(parts[0]), int.Parse(parts[1]), parts[2]));
    }

    public static ParsedLine ParseTInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.TInfo,
            new TInfo(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), parts[3]));
    }

    public static ParsedLine ParseSInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.SinFo,
            new SinFo(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), parts[4]));
    }

    public static ParsedLine ParsePrgV(string content)
    {
        var parts = content.Split(',');
        if (parts.Length >= 4)
        {
            // 4-field: current_title,total_titles,current_progress,total_progress
            var ct = int.TryParse(parts[0], out var v0) ? v0 : 0;
            var tt = int.TryParse(parts[1], out var v1) ? v1 : 0;
            var cp = int.TryParse(parts[2], out var v2) ? v2 : 0;
            var tp = int.TryParse(parts[3], out var v3) ? v3 : 0;
            return new ParsedLine(MakeMkvOutputType.PrgV, new PrgV(ct, tt, cp, tp));
        }
        else
        {
            // 3-field (MakeMKV v1.18+): ambiguous format — skip progress,
            // let the file-size monitor handle it reliably
            return new ParsedLine(MakeMkvOutputType.PrgV, new PrgV(0, 0, 0, 0));
        }
    }

    public static ParsedLine ParsePrgC(string content)
    {
        var parts = content.Split(',');
        var cp = parts.Length > 0 && int.TryParse(parts[0], out var v0) ? v0 : 0;
        var tp = parts.Length > 1 && int.TryParse(parts[1], out var v1) ? v1 : 0;
        return new ParsedLine(MakeMkvOutputType.PrgC, new PrgC(cp, tp));
    }

    public static ParsedLine ParsePrgT(string content)
    {
        // PRGT format: code,total,"text" — e.g. 5018,0,"Scanning CD-ROM devices"
        var parts = SplitCsv(content);
        if (parts.Length >= 2 && int.TryParse(parts[1], out var total2))
            return new ParsedLine(MakeMkvOutputType.PrgT, new PrgT(total2));

        // Fallback: try parsing as single int (legacy)
        if (int.TryParse(content, out var total1))
            return new ParsedLine(MakeMkvOutputType.PrgT, new PrgT(total1));

        return new ParsedLine(MakeMkvOutputType.PrgT, new PrgT(0));
    }

    public static string[] SplitCsv(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return [.. result];
    }

    public static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }

    public static int HmsToSeconds(string hms)
    {
        var parts = hms.Split(':');
        return int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]);
    }
}
