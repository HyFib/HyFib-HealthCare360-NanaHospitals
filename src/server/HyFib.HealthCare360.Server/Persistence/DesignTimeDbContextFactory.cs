using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HyFib.HealthCare360.Server.Persistence;

/// <summary>
/// Used by <c>dotnet ef migrations add</c>. The model is built from every module in <see cref="ServerModules"/>;
/// no database connection is needed to add a migration. Set HC360_MIGRATIONS_CONNECTION to apply them manually.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HospitalDbContext>
{
    public HospitalDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("HC360_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=57360;Database=hc360;Username=hc360;Password=design-time-only";

        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(connection, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform");
            })
            .UseSnakeCaseNamingConvention()
            .Options;

        return new HospitalDbContext(options, new ModuleCatalog(ServerModules.All));
    }
}
