using ArmRipper.Core.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ArmDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ArmDb") ?? "Data Source=/etc/arm/config/arm.db"));

builder.Services.AddRazorPages();
builder.Services.AddControllers();

var app = builder.Build();

var dbPath = builder.Configuration.GetConnectionString("ArmDb") ?? "Data Source=/etc/arm/config/arm.db";
var dbFile = dbPath.Replace("Data Source=", "").Split(';')[0];
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

var port = builder.Configuration.GetValue<int?>("WebServer:Port") ?? 8080;
app.Run($"http://0.0.0.0:{port}");
