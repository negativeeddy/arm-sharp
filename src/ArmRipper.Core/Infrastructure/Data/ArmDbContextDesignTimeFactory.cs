using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ArmRipper.Core.Infrastructure.Data;

/// <summary>
/// Design-time factory for creating ArmDbContext instances during EF Core
/// migration operations (dotnet ef migrations add / update).
/// </summary>
public class ArmDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ArmDbContext>
{
    public ArmDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArmDbContext>();
        optionsBuilder.UseSqlite("Data Source=arm-sharp.db");

        return new ArmDbContext(optionsBuilder.Options);
    }
}
