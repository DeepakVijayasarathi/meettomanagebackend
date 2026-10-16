using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace iucs.meettomanage.api.Data;

/// <summary>Allows offline migration generation without starting the web host or workers.</summary>
public sealed class MasterDbContextFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__MasterDb")
            ?? "Host=localhost;Database=meettomanage_master;Username=postgres";
        return new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(connection).Options);
    }
}
