using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

var yamlValues = ArmYamlConfigLoader.LoadYamlValues("/etc/arm/config/arm.yaml");
builder.Configuration.AddInMemoryCollection(yamlValues);

var connectionString = builder.Configuration["ConnectionStrings:ArmDb"] ?? "Data Source=/etc/arm/config/arm-sharp.db";
builder.Services.AddDbContext<ArmDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.Configure<ArmSettings>(builder.Configuration.GetSection(ArmSettings.SectionName));

builder.Services.AddSingleton<ICliProcessRunner, CliProcessRunner>();
builder.Services.AddScoped<IIdentifyService, IdentifyService>();
builder.Services.AddScoped<IHandBrakeService, HandBrakeService>();
builder.Services.AddScoped<IFfmpegService, FfmpegService>();
builder.Services.AddScoped<IArmRipperService, ArmRipperService>();
builder.Services.AddScoped<MakeMkvService>();
builder.Services.AddScoped<IMusicBrainzService, MusicBrainzService>();
builder.Services.AddTransient(_ =>
{
    var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
    return client;
});
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<IConductor, Conductor>();
builder.Services.AddSingleton<INotificationBroadcaster, NullNotificationBroadcaster>();
builder.Services.AddSingleton<IBackgroundRipService, BackgroundRipService>();

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
    try
    {
        db.Database.Migrate();
    }
    catch
    {
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL, \"ProductVersion\" TEXT NOT NULL);");
        try { db.Database.ExecuteSqlRaw("ALTER TABLE jobs ADD COLUMN Warnings TEXT NULL;"); } catch { }
        db.Database.ExecuteSqlRaw(
            "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260610044322_Initial', '10.0.0');");
    }
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
