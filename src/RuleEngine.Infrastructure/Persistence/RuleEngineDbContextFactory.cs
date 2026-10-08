using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RuleEngine.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> tooling (migrations add / script).</summary>
public sealed class RuleEngineDbContextFactory : IDesignTimeDbContextFactory<RuleEngineDbContext>
{
    public RuleEngineDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__RuleEngine")
            ?? "Server=localhost,1433;Database=RuleEngine;User Id=sa;Password=Your_strong_password1;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<RuleEngineDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new RuleEngineDbContext(options);
    }
}
