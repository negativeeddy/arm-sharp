using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmRipper.Core;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

var yamlValues = ArmYamlConfigLoader.LoadYamlValues("/etc/arm/config/arm.yaml");
builder.Configuration.AddInMemoryCollection(yamlValues);

// Allow --db to override the database path
var dbOverride = args.FirstOrDefault(a => a.StartsWith("--db="))?.Split('=', 2)[1];
if (dbOverride is null)
{
    var dbIdx = Array.IndexOf(args, "--db");
    if (dbIdx >= 0 && dbIdx + 1 < args.Length)
        dbOverride = args[dbIdx + 1];
}

var connectionString = dbOverride is not null
    ? $"Data Source={dbOverride}"
    : builder.Configuration["ConnectionStrings:ArmDb"] ?? "Data Source=/etc/arm/config/arm-sharp.db";
builder.Services.AddDbContext<ArmDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.Configure<ArmSettings>(builder.Configuration.GetSection(ArmSettings.SectionName));

builder.Services.AddSingleton<ICliProcessRunner, CliProcessRunner>();
builder.Services.AddHttpClient("IdentifyService", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
});
builder.Services.AddScoped<IIdentifyService, IdentifyService>();
builder.Services.AddSingleton<ITranscodeSlotLimiter, TranscodeSlotLimiter>();
builder.Services.AddScoped<IHandBrakeService, HandBrakeService>();
builder.Services.AddScoped<IFfmpegService, FfmpegService>();
builder.Services.AddScoped<IArmRipperService, ArmRipperService>();
builder.Services.AddScoped<IMakeMkvService, MakeMkvService>();
builder.Services.AddScoped<IDatabaseSubmitService, DatabaseSubmitService>();
builder.Services.AddScoped<IOvidSubmitService, OvidSubmitService>();
builder.Services.AddHttpClient<IMusicBrainzService, MusicBrainzService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
});
builder.Services.AddHttpClient("DatabaseSubmitService", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
});
builder.Services.AddHttpClient("Notifications", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
});
builder.Services.AddHttpClient("MakeMkv", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
});
builder.Services.AddHttpClient("TheDiscDb", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm-sharp/1.0 (discdb-integration)");
});
builder.Services.AddScoped<IDiscDbHashService, DiscDbHashService>();
builder.Services.AddScoped<IDiscDbQueryService, DiscDbQueryService>();
builder.Services.AddScoped<IDiscDbMappingService, DiscDbMappingService>();
builder.Services.AddScoped<ITrackMapperService, TrackMapperService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<IConductor, Conductor>();
builder.Services.AddSingleton<INotificationBroadcaster, NullNotificationBroadcaster>();
builder.Services.AddSingleton<IBackgroundRipService, BackgroundRipService>();

// ── ArmMedia TV series identification pipeline ──
builder.Services.AddHttpClient("Tmdb", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm-sharp/1.0 (tmdb-provider)");
});
builder.Services.AddHttpClient("Tvdb", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm-sharp/1.0 (tvdb-provider)");
});
builder.Services.AddHttpClient("Omdb", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm-sharp/1.0 (omdb-provider)");
});
builder.Services.AddHttpClient("DvdCompare", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
});
builder.Services.AddArmMediaTvPipeline(builder.Configuration);

// Per-job file logging
var fileLogProvider = new JobFileLoggerProvider();
builder.Services.AddSingleton(fileLogProvider);
builder.Services.AddLogging(logging => logging.AddProvider(fileLogProvider));

builder.Services.AddLogging(logging => logging.AddConsole());

var host = builder.Build();

var dbPath = connectionString;
var dbFile = dbPath.Replace("Data Source=", "").Split(';')[0];
var dbDir = Path.GetDirectoryName(dbFile);
if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
    Directory.CreateDirectory(dbDir);

using (var initScope = host.Services.CreateScope())
{
    var initLogger = initScope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ArmRipper.Cli.Program");
    var db = initScope.ServiceProvider.GetRequiredService<ArmDbContext>();

    var yamlPath = "/etc/arm/config/arm.yaml";
    initLogger.LogInformation("Database path: {DbPath}", dbFile);
    initLogger.LogInformation("Config file: {YamlPath} ({Status})",
        yamlPath, File.Exists(yamlPath) ? "found" : "not found");

    DatabaseHelper.EnsureMigrated(db);
    initLogger.LogInformation("Database migrated successfully");

    // Seed (or reset) DB RipperSettings from file config
    // Set ARM_RESET_SETTINGS=true to overwrite DB with file values on startup
    var seedSettings = initScope.ServiceProvider.GetRequiredService<IOptions<ArmSettings>>().Value;
    var reset = Environment.GetEnvironmentVariable("ARM_RESET_SETTINGS") == "true";

    var existingRow = db.RipperSettings.OrderBy(x => x.Id).FirstOrDefault();
    if (existingRow is null)
    {
        initLogger.LogInformation("No existing DB settings — seeding from file config on first boot");
    }
    else if (reset)
    {
        initLogger.LogInformation("ARM_RESET_SETTINGS=true — overwriting DB settings with file config values");
    }
    else
    {
        initLogger.LogInformation("DB settings exist and are authoritative (set ARM_RESET_SETTINGS=true to re-seed from file)");
    }

    SettingsHelper.SeedFromFileAsync(db, seedSettings, reset).GetAwaiter().GetResult();
}

var testMode = args.Any(a => a is "--test" or "-t");
builder.Configuration["Arm:TestMode"] = testMode ? "true" : "false";

var deviceArg = args.FirstOrDefault(a => a.StartsWith("--device="))?.Split('=')[1]
    ?? args.FirstOrDefault(a => a.StartsWith("-d="))?.Split('=')[1]
    ?? args.Select((a, i) => a is "--device" or "-d" && i + 1 < args.Length ? args[i + 1] : null).FirstOrDefault(a => a is not null)
    ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null);

// ── Re-identify mode: re-run episode IDs against raw files without re-ripping ──
var reidentifyJobStr = args.FirstOrDefault(a => a.StartsWith("--reidentify-job="))?.Split('=')[1]
    ?? args.Select((a, i) => a == "--reidentify-job" && i + 1 < args.Length ? args[i + 1] : null).FirstOrDefault(a => a is not null);

if (reidentifyJobStr is not null && int.TryParse(reidentifyJobStr, out int reidentifyJobId))
{
    bool save = args.Any(a => a is "--save" or "-s");
    return await RunReidentifyJobAsync(host.Services, reidentifyJobId, save);
}

if (deviceArg is null && reidentifyJobStr is null)
{
    Console.Error.WriteLine("Usage: ArmRipper.Cli --device /dev/sr0 [--test]");
    Console.Error.WriteLine("       ArmRipper.Cli --reidentify-job <jobId> [--save] [--db <path>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  --device /dev/sr0     CD/DVD/BD device to rip");
    Console.Error.WriteLine("  --test                Rip only first title and transcode 2 minutes per track");
    Console.Error.WriteLine("  --reidentify-job <id> Re-run episode identification on a completed job");
    Console.Error.WriteLine("  --save                Write re-identification results to the database");
    Console.Error.WriteLine("  --db <path>           Override the SQLite database file path");
    return 1;
}

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ArmRipper.Cli.Program");
logger.LogInformation("ARM .NET Ripper starting for device {Device}", deviceArg);

using var scope = host.Services.CreateScope();
var conductor = scope.ServiceProvider.GetRequiredService<IConductor>();

var exitCode = await conductor.RunAsync(deviceArg!);
logger.LogInformation("ARM ripper exiting with code {ExitCode}", exitCode);
return exitCode;

// ── Re-identification helper ────────────────────────────────────────────────

static async Task<int> RunReidentifyJobAsync(IServiceProvider services, int jobId, bool save)
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ArmRipper.Cli.Program");
    var db = services.GetRequiredService<ArmDbContext>();
    var orchestrator = services.GetRequiredService<IEpisodeIdentificationOrchestrator>();
    var settings = services.GetRequiredService<IOptions<ArmSettings>>().Value;

    using var scope = services.CreateScope();
    var scopedDb = scope.ServiceProvider.GetRequiredService<ArmDbContext>();

    // Ensure schema is up to date (handle older DBs missing newer columns)
    DatabaseHelper.EnsureMigrated(scopedDb);

    var job = await scopedDb.Jobs
        .Include(j => j.Tracks)
        .FirstOrDefaultAsync(j => j.Id == jobId);

    if (job is null)
    {
        logger.LogError("Job {JobId} not found in database.", jobId);
        return 1;
    }

    // Only run on series/tv jobs
    if (job.VideoType != "series" && job.VideoType != "tv")
    {
        logger.LogError("Job {JobId} is not a TV series (type={Type}).", jobId, job.VideoType);
        return 1;
    }

    var rippedTracks = job.Tracks
        .Where(t => t.Ripped)
        .OrderBy(t => t.TrackNumberInt ?? 0)
        .ToList();

    if (rippedTracks.Count == 0)
    {
        logger.LogError("Job {JobId} has no ripped tracks.", jobId);
        return 1;
    }

    Console.WriteLine($"\n=== Re-identifying Job {jobId}: {job.Title} (S{job.SeasonNumber}) Disc {job.Label} ===\n");
    Console.WriteLine($"Tracks ripped: {rippedTracks.Count}");
    Console.WriteLine();

    // Build DiscContext from job data
    var trackContexts = rippedTracks.Select(t =>
    {
        var rawProps = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(t.FileName))
            rawProps["FileName"] = t.FileName;
        if (!string.IsNullOrEmpty(t.TrackNumber))
            rawProps["TrackNumber"] = t.TrackNumber;

        return new TrackContext
        {
            TrackIndex    = t.TrackNumberInt ?? 0,
            Duration      = TimeSpan.FromSeconds(t.Length ?? 0),
            SizeBytes     = t.FileSize ?? 0,
            ChapterCount  = t.Chapters,
            DiscDbTrackId = t.DiscDbItemSlug,
            RawProperties = rawProps
        };
    }).ToList().AsReadOnly();

    var discNumber = ParseDiscNumber(job.Label);
    var seriesTitle = CleanSeriesTitle(job.Title ?? job.Label ?? "Unknown");
    var season = job.SeasonNumber ?? 1;

    var ctx = new DiscContext
    {
        DiscId      = job.DiscDbHash ?? job.Label ?? job.DevPath ?? "unknown",
        SeriesTitle = seriesTitle,
        Season      = season,
        Tracks      = trackContexts,
        DiscDbHint  = null,
        DiscNumber  = discNumber
    };

    logger.LogInformation("Running episode identification for '{Title}' S{Season} disc {Disc}...",
        ctx.SeriesTitle, ctx.Season, ctx.DiscNumber);

    var episodeMap = await orchestrator.IdentifyAsync(ctx, CancellationToken.None);

    // ── Display results ──
    Console.WriteLine($"Results: {episodeMap.Tracks.Count} tracks mapped\n");

    // ── Table header ──
    Console.WriteLine($"{"Idx",-4} {"SxE",-8} {"Title",-35} {"Duration",-10} {"Provider",-15} {"Confidence",-12}");
    Console.WriteLine(new string('-', 84));

    foreach (var mapped in episodeMap.Tracks.OrderBy(t => t.TrackIndex))
    {
        var trackCtx = ctx.Tracks.FirstOrDefault(t => t.TrackIndex == mapped.TrackIndex);
        var dur = trackCtx?.Duration ?? TimeSpan.Zero;
        var epStr = mapped.IsExtra
            ? $"S00E{mapped.Episodes.FirstOrDefault():D2}"
            : $"S{mapped.Season:D2}E{mapped.Episodes.FirstOrDefault():D2}";

        Console.WriteLine(
            $"{mapped.TrackIndex,-4} {epStr,-8} {Truncate(mapped.Title ?? "", 35),-35} {FormatDuration(dur),-10} {Truncate(mapped.WinningProvider ?? "", 15),-15} {mapped.Confidence,-12}");
    }

    Console.WriteLine();

    // ── Per-track details ──
    Console.WriteLine("=== Track Details ===\n");
    foreach (var mapped in episodeMap.Tracks.OrderBy(t => t.TrackIndex))
    {
        var trackCtx = ctx.Tracks.FirstOrDefault(t => t.TrackIndex == mapped.TrackIndex);
        var fileName = trackCtx?.RawProperties?.TryGetValue("FileName", out var fn) == true ? fn : "?";
        Console.WriteLine($"Track {mapped.TrackIndex}: {fileName}");
        Console.WriteLine($"  Duration: {FormatDuration(trackCtx?.Duration ?? TimeSpan.Zero)}");
        if (mapped.IsExtra)
            Console.WriteLine($"  → Extra S00E{mapped.Episodes.FirstOrDefault():D2} '{mapped.Title}' ({mapped.WinningProvider} / {mapped.Confidence})");
        else
            Console.WriteLine($"  → S{mapped.Season:D2}E{mapped.Episodes.FirstOrDefault():D2} '{mapped.Title}' ({mapped.WinningProvider} / {mapped.Confidence})");
        Console.WriteLine();
    }

    // ── Save results back to DB if requested ──
    if (save)
    {
        foreach (var mapped in episodeMap.Tracks)
        {
            var track = rippedTracks.FirstOrDefault(t => t.TrackNumberInt == mapped.TrackIndex);
            if (track is not null)
            {
                track.EpisodeNumber      = mapped.Episodes.Length > 0 ? mapped.Episodes[0] : null;
                track.EpisodeTitle       = mapped.Title;
                track.TrackSeasonNumber  = mapped.Season;
            }
        }

        await scopedDb.SaveChangesAsync();
        Console.WriteLine($"✓ Saved {episodeMap.Tracks.Count} track mappings to DB.");
        Console.WriteLine("  Run with --dry-run (omit --save) to preview without saving.");
    }
    else
    {
        Console.WriteLine("  (Preview only — add --save to write results to DB)");
    }

    return 0;
}

// ── Helpers shared with re-identification ──────────────────────────────────

static string Truncate(string value, int maxLen) =>
    value.Length <= maxLen ? value : value[..(maxLen - 3)] + "...";

static string FormatDuration(TimeSpan d) =>
    d.TotalHours >= 1
        ? $"{(int)d.TotalHours}h{d.Minutes:D2}m"
        : $"{d.Minutes}m{d.Seconds:D2}s";

static int ParseDiscNumber(string? label)
{
    if (string.IsNullOrWhiteSpace(label))
        return 1;

    // Match _D1, _D2, _D3 etc. at end of label
    var match = System.Text.RegularExpressions.Regex.Match(label, @"_D(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return match.Success && int.TryParse(match.Groups[1].Value, out int discNum)
        ? discNum
        : 1;
}

static string CleanSeriesTitle(string title)
{
    // Strip _S1_D2 suffix then title-case
    var cleaned = System.Text.RegularExpressions.Regex.Replace(title, @"_S\d+_D\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    cleaned = cleaned.Replace('_', ' ');
    // Title case: capitalise first letter of each word
    var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < words.Length; i++)
    {
        if (words[i].Length > 0)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();
    }
    return string.Join(' ', words);
}
