using ArmMedia.ArmSharpExtensions;
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

var connectionString = builder.Configuration["ConnectionStrings:ArmDb"] ?? "Data Source=/etc/arm/config/arm-sharp.db";
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
    client.Timeout = TimeSpan.FromSeconds(15);
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
    var db = initScope.ServiceProvider.GetRequiredService<ArmDbContext>();
    DatabaseHelper.EnsureMigrated(db);

    // Seed (or reset) DB RipperSettings from file config
    // Set ARM_RESET_SETTINGS=true to overwrite DB with file values on startup
    var seedSettings = initScope.ServiceProvider.GetRequiredService<IOptions<ArmSettings>>().Value;
    var reset = Environment.GetEnvironmentVariable("ARM_RESET_SETTINGS") == "true";
    SettingsHelper.SeedFromFileAsync(db, seedSettings, reset).GetAwaiter().GetResult();
}

var testMode = args.Any(a => a is "--test" or "-t");
builder.Configuration["Arm:TestMode"] = testMode ? "true" : "false";

var deviceArg = args.FirstOrDefault(a => a.StartsWith("--device="))?.Split('=')[1]
    ?? args.FirstOrDefault(a => a.StartsWith("-d="))?.Split('=')[1]
    ?? args.Select((a, i) => a is "--device" or "-d" && i + 1 < args.Length ? args[i + 1] : null).FirstOrDefault(a => a is not null)
    ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null);

if (deviceArg is null)
{
    Console.Error.WriteLine("Usage: ArmRipper.Cli --device /dev/sr0 [--test]");
    Console.Error.WriteLine("  --test         Rip only first title and transcode 2 minutes per track");
    return 1;
}

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("ARM .NET Ripper starting for device {Device}", deviceArg);

using var scope = host.Services.CreateScope();
var conductor = scope.ServiceProvider.GetRequiredService<IConductor>();

var exitCode = await conductor.RunAsync(deviceArg);
logger.LogInformation("ARM ripper exiting with code {ExitCode}", exitCode);
return exitCode;
