using System.Security.Claims;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Metadata;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using ArmRipper.WebUi.Hubs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

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
builder.Services.AddHttpClient("IdentifyService", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
});
builder.Services.AddScoped<IIdentifyService, IdentifyService>();
builder.Services.AddScoped<IHandBrakeService, HandBrakeService>();
builder.Services.AddScoped<IFfmpegService, FfmpegService>();
builder.Services.AddScoped<IArmRipperService, ArmRipperService>();
builder.Services.AddScoped<IMakeMkvService, MakeMkvService>();
builder.Services.AddHttpClient<IMusicBrainzService, MusicBrainzService>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<IConductor, Conductor>();
builder.Services.AddHttpClient<OmdbService>();
builder.Services.AddHttpClient<TmdbService>();
builder.Services.AddSingleton<INotificationBroadcaster, SignalRNotificationBroadcaster>();
builder.Services.AddSingleton<IBackgroundRipService, BackgroundRipService>();

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
    var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
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
        try { db.Database.ExecuteSqlRaw("ALTER TABLE jobs ADD COLUMN ProgressMessage TEXT NULL;"); } catch { }
        db.Database.ExecuteSqlRaw(
            "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260610044322_Initial', '10.0.0');");
        try { db.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260610053400_AddProgressMessage', '10.0.0');"); } catch { }
    }

    db.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
}

var armSettings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArmSettings>>().Value;
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
