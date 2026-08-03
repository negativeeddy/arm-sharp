using System.Security.Claims;
using ArmRipper.Core;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Metadata;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using ArmRipper.WebUi.Services;
using ArmRipper.WebUi.Services.Mcp;
using ArmRipper.WebUi.Hubs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// DB-first settings: /etc/arm/config/arm.yaml is NOT loaded as a config overlay
// anymore. Legacy ARM values can be pulled into the DB explicitly via the
// "Import ARM settings" action in the Settings UI (ArmSettingsImporter).
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
builder.Services.AddScoped<IOvidSubmitService, OvidSubmitService>();
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

// ── MCP (Model Context Protocol) server ──
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "arm-sharp", Version = "1.0.0" };
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability(),
        };
    })
    .WithHttpTransport(options =>
    {
        // Stateless mode: no session management needed for diagnostic tools.
        // Each request is independent — no session ID required.
        options.Stateless = true;
    })
    .WithTools<ArmRipperTools>();

// Per-job file logging
var fileLogProvider = new JobFileLoggerProvider();
builder.Services.AddSingleton(fileLogProvider);
builder.Services.AddLogging(logging => logging.AddProvider(fileLogProvider));

var app = builder.Build();

// Non-job-scoped logs (BackgroundRipService, DiscPollingService, startup, etc.)
// are written to a general ARM log file instead of being silently discarded.
// The path is finalized inside the scope below, once the DB is available, so the
// DB-stored LogPath wins over the file config when they conflict.
var initArmSettings = app.Services.GetRequiredService<IOptions<ArmSettings>>().Value;

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

    // Prefer the DB-stored LogPath (effective settings) over the file config when
    // they conflict, so the general ARM log (arm.log) lands alongside the per-job
    // logs instead of a dev-only ./data/logs directory.
    var effectiveSettings = await SettingsHelper.GetEffectiveSettingsAsync(db, initArmSettings, CancellationToken.None);
    fileLogProvider.FallbackFilePath = Path.Combine(
        effectiveSettings.LogPath ?? ArmPaths.DefaultLogPath, "arm.log");

    // DB-first: the ripper_settings row stores ONLY user overrides (deltas).
    // Files never write into the DB at boot. Legacy full-snapshot rows (from older
    // builds) are migrated to deltas on first boot after upgrade. Existing ARM config
    // can be pulled into the DB explicitly via "Import ARM settings" in the UI.
    var seedSettings = scope.ServiceProvider.GetRequiredService<IOptions<ArmSettings>>().Value;
    var hadRow = await db.RipperSettings.AnyAsync();
    await SettingsHelper.EnsureSeededAsync(db, CancellationToken.None);
    await SettingsHelper.NormalizeLegacyRowAsync(db, seedSettings, CancellationToken.None);

    initLogger.LogInformation(hadRow
        ? "DB settings row exists — DB overrides are authoritative"
        : "No DB settings row found — created empty overrides row (file defaults apply)");
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
app.MapMcp("/mcp");

var port = builder.Configuration.GetValue<int?>("WebServer:Port") ?? 8080;
app.Run($"http://0.0.0.0:{port}");

public partial class Program { }
