using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace A7OS.AIFarm.Infrastructure.Persistence;

public sealed class AIFarmDbContextFactory : IDesignTimeDbContextFactory<AIFarmDbContext>
{
    public AIFarmDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AIFarmDbContext>()
            .UseNpgsql("Host=localhost;Database=a7os_aifarm;Username=a7os")
            .Options;
        return new AIFarmDbContext(options);
    }
}
