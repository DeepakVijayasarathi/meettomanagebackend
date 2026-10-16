using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace iucs.meettomanage.api.Data;

public sealed class MeetToManageDbContextFactory : IDesignTimeDbContextFactory<MeetToManageDbContext>
{
    public MeetToManageDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<MeetToManageDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__ReaderNestDb")
            ?? "Host=localhost;Database=meettomanage_design;Username=postgres").Options);
}
