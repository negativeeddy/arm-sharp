using System.Security.Claims;
using ArmRipper.Core;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Metadata;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using ArmRipper.WebUi.Services;
using ArmRipper.WebUi.Hubs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var yamlValues = ArmYamlConfigLoader.LoadYamlValues("/etc/arm/config/arm.yaml");
builder.Configuration.AddInMemoryCollection(yamlValues);

var connectionString = builder.Configuration.GetConnectionString("ArmDb") ?? "Data Source=/etc/arm/config/arm-sharp.db";
builder.Services.AddDbContext<ArmDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.Configure<ArmSettings>(builder.Configuration.GetSection(ArmSettings.SectionName));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/auth/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/auth/login";
    });

builder.Services.AddAuthorization();

builder.Services.AddRazorPages();
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<ICliProcessRunner, CliProcessRunner>();
builder.Services.AddSingleton<IHardwareEncoderInfoService, HardwareEncoderInfoService>();
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
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("DatabaseSubmitService", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
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
builder.Services.AddHttpClient<OmdbService>();
builder.Services.AddHttpClient<TmdbService>();
builder.Services.AddSingleton<INotificationBroadcaster, SignalRNotificationBroadcaster>();
builder.Services.AddSingleton<IBackgroundRipService, BackgroundRipService>();
builder.Services.AddSingleton<DiscPollingService>();
builder.Services.AddHostedService<DiscPollingService>(sp => sp.GetRequiredService<DiscPollingService>());
builder.Services.AddSingleton<IDiscPollingNotifier>(sp => sp.GetRequiredService<DiscPollingService>());
builder.Services.AddHostedService<ShutdownJobCancellationService>();

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

// Named HttpClient registrations (avoids socket exhaustion from per-call new HttpClient())
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

// Per-job file logging
var fileLogProvider = new JobFileLoggerProvider();
builder.Services.AddSingleton(fileLogProvider);
builder.Services.AddLogging(logging => logging.AddProvider(fileLogProvider));

var app = builder.Build();

var dbFile = connectionString.Replace("Data Source=", "").Split(';')[0];
var dbDir = Path.GetDirectoryName(dbFile);
if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
    Directory.CreateDirectory(dbDir);

using (var scope = app.Services.CreateScope())
{
    var initLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();

    var yamlPath = "/etc/arm/config/arm.yaml";
    initLogger.LogInformation("Database path: {DbPath}", dbFile);
    initLogger.LogInformation("Config file: {YamlPath} ({Status})",
        yamlPath, File.Exists(yamlPath) ? "found" : "not found");

    DatabaseHelper.EnsureMigrated(db);
    initLogger.LogInformation("Database migrated successfully");

    // Seed (or reset) DB RipperSettings from file config
    // Set ARM_RESET_SETTINGS=true to overwrite DB with file values on startup
    var seedSettings = scope.ServiceProvider.GetRequiredService<IOptions<ArmSettings>>().Value;
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

var armSettings = app.Services.GetRequiredService<IOptions<ArmSettings>>().Value;
if (armSettings.DisableLogin)
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
        {
            var claims = new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Admin") };
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        }
        await next();
    });
}

app.UseStatusCodePagesWithReExecute("/error", "?code={0}");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapControllers();
app.MapHub<NotificationHub>("/hubs/notifications");

var port = builder.Configuration.GetValue<int?>("WebServer:Port") ?? 8080;
app.Run($"http://0.0.0.0:{port}");

public partial class Program { }
