using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration["ConnectionStrings:ArmDb"] ?? "Data Source=/etc/arm/config/arm.db";
builder.Services.AddDbContext<ArmDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.Configure<ArmSettings>(builder.Configuration.GetSection(ArmSettings.SectionName));

builder.Services.AddSingleton<CliProcessRunner>();
builder.Services.AddScoped<IIdentifyService, IdentifyService>();
builder.Services.AddScoped<IHandBrakeService, HandBrakeService>();
builder.Services.AddScoped<IFfmpegService, FfmpegService>();
builder.Services.AddScoped<IArmRipperService, ArmRipperService>();
builder.Services.AddScoped<MakeMkvService>();
builder.Services.AddScoped<IMusicBrainzService, MusicBrainzService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<Conductor>();

builder.Services.AddLogging(logging => logging.AddConsole());

var host = builder.Build();

using (var initScope = host.Services.CreateScope())
{
    var db = initScope.ServiceProvider.GetRequiredService<ArmDbContext>();
    db.Database.EnsureCreated();
}

var deviceArg = args.FirstOrDefault(a => a.StartsWith("--device="))?.Split('=')[1]
    ?? args.FirstOrDefault(a => a.StartsWith("-d="))?.Split('=')[1]
    ?? (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null);

if (deviceArg is null)
{
    Console.Error.WriteLine("Usage: ArmRipper.Cli --device /dev/sr0");
    return 1;
}

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("ARM .NET Ripper starting for device {Device}", deviceArg);

using var scope = host.Services.CreateScope();
var conductor = scope.ServiceProvider.GetRequiredService<Conductor>();

var exitCode = await conductor.RunAsync(deviceArg);
logger.LogInformation("ARM ripper exiting with code {ExitCode}", exitCode);
return exitCode;
