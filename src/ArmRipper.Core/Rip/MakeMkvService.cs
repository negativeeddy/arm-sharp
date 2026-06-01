using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public partial class MakeMkvService
{
    private const int UnknownDrv = 999;
    private const int MaxDevices = 16;
    private const int StreamCodeTypeVideo = 6201;
    private const string Source = "MakeMKV";
    private const string BetaKeyUrl = "https://cable.ayra.ch/MakeMKV/api.php?raw";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".MakeMKV", "settings.conf");

    private readonly ICliProcessRunner _runner;
    private readonly ILogger<MakeMkvService> _logger;
    private readonly IOptions<ArmSettings> _settings;

    public MakeMkvService(ICliProcessRunner runner, ILogger<MakeMkvService> logger, IOptions<ArmSettings> settings)
    {
        _runner = runner;
        _logger = logger;
        _settings = settings;
    }

    public async Task EnsureKeyAsync(CancellationToken ct = default)
    {
        var configuredKey = _settings.Value.MakeMkvPermaKey;
        if (!string.IsNullOrEmpty(configuredKey))
        {
            await RegisterKeyAsync(configuredKey, ct);
            return;
        }

        var key = await FetchBetaKeyAsync(ct);
        if (!string.IsNullOrEmpty(key))
        {
            await RegisterKeyAsync(key, ct);
            _logger.LogInformation("Auto-updated MakeMKV beta key");
        }
    }

    private static async Task<string?> FetchBetaKeyAsync(CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // Try the API first
            try
            {
                var key = (await httpClient.GetStringAsync(BetaKeyUrl, ct)).Trim();
                if (!string.IsNullOrEmpty(key) && key.StartsWith("T-"))
                    return key;
            }
            catch { }

            // Fallback: scrape the MakeMKV forum
            var html = await httpClient.GetStringAsync(
                "https://forum.makemkv.com/forum/viewtopic.php?f=5&t=1053", ct);
            var match = Regex.Match(html, @"<code>(T-[A-Za-z0-9@]+)</code>");
            if (match.Success)
                return match.Groups[1].Value;
        }
        catch { }

        return null;
    }

    private async Task RegisterKeyAsync(string key, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(SettingsPath, $"app_Key = \"{key}\"\n", ct);
        _logger.LogInformation("MakeMKV key saved to {Path}", SettingsPath);
    }

    public async IAsyncEnumerable<T> RunAsync<T>(
        string[] options,
        MakeMkvOutputType select,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureKeyAsync(ct);

        var cmd = new[] { "makemkvcon", "--robot", "--messages=-stdout" }.Concat(options).ToArray();

        await foreach (var line in _runner.RunStreamingAsync(cmd[0], string.Join(" ", cmd[1..]), ct: ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parsed = ParseLine(line);
            if (parsed is null) continue;

            if (select.HasFlag(parsed.Type) && parsed.Data is T result)
                yield return result;
        }
    }

    public record ParsedLine(MakeMkvOutputType Type, object Data);

    public ParsedLine? ParseLine(string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return null;

        var typeStr = line[..colonIdx];
        var content = line[(colonIdx + 1)..];

        if (!Enum.TryParse<MakeMkvOutputType>(typeStr, out var type))
            return null;

        return type switch
        {
            MakeMkvOutputType.Msg => ParseMsg(content),
            MakeMkvOutputType.Drv => ParseDrv(content),
            MakeMkvOutputType.TCount => new ParsedLine(type, new Titles(int.Parse(content))),
            MakeMkvOutputType.CinFo => ParseCInfo(content),
            MakeMkvOutputType.TInfo => ParseTInfo(content),
            MakeMkvOutputType.SinFo => ParseSInfo(content),
            _ => null
        };
    }

    private ParsedLine ParseMsg(string content)
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

    private ParsedLine ParseDrv(string content)
    {
        var parts = SplitCsv(content);
        var index = int.Parse(parts[0]);
        var visible = int.Parse(parts[1]);
        var enabled = int.Parse(parts[2]) == UnknownDrv;
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

    private static ParsedLine ParseCInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.CinFo,
            new CinFo(int.Parse(parts[0]), int.Parse(parts[1]), parts[2]));
    }

    private static ParsedLine ParseTInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.TInfo,
            new TInfo(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), parts[3]));
    }

    private static ParsedLine ParseSInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.SinFo,
            new SinFo(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), parts[4]));
    }

    private static string[] SplitCsv(string input)
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

    private static int HmsToSeconds(string hms)
    {
        var parts = hms.Split(':');
        return int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]);
    }
}
