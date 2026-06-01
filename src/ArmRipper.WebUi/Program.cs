using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Metadata;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using ArmRipper.WebUi.Hubs;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ArmDb") ?? "Data Source=/etc/arm/config/arm.db";
builder.Services.AddDbContext<ArmDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.Configure<ArmSettings>(builder.Configuration.GetSection(ArmSettings.SectionName));

builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddSingleton<ICliProcessRunner, CliProcessRunner>();
builder.Services.AddScoped<IIdentifyService, IdentifyService>();
builder.Services.AddScoped<IHandBrakeService, HandBrakeService>();
builder.Services.AddScoped<IFfmpegService, FfmpegService>();
builder.Services.AddScoped<IArmRipperService, ArmRipperService>();
builder.Services.AddScoped<MakeMkvService>();
builder.Services.AddScoped<IMusicBrainzService, MusicBrainzService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<Conductor>();
builder.Services.AddHttpClient<OmdbService>();
builder.Services.AddHttpClient<TmdbService>();
builder.Services.AddSingleton<INotificationBroadcaster, SignalRNotificationBroadcaster>();

var app = builder.Build();

var dbFile = connectionString.Replace("Data Source=", "").Split(';')[0];
var dbDir = Path.GetDirectoryName(dbFile);
if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
    Directory.CreateDirectory(dbDir);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
    db.Database.EnsureCreated();
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapControllers();
app.MapHub<NotificationHub>("/hubs/notifications");

var port = builder.Configuration.GetValue<int?>("WebServer:Port") ?? 8080;
app.Run($"http://0.0.0.0:{port}");
