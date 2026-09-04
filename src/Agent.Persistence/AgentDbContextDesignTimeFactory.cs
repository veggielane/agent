using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agent.Persistence;

/// <summary>Used by <c>dotnet ef</c> to build the SQL Server model for migrations; it never connects.</summary>
public sealed class AgentDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public const string DesignTimeConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=Agent;Integrated Security=true;Encrypt=false";

    public AgentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlServer(DesignTimeConnectionString)
            .Options;
        return new AgentDbContext(options);
    }
}
