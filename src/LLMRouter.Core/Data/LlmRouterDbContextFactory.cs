using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LLMRouter.Core.Data;

/// <summary>
/// Design-time factory (SPEC-109): lets `dotnet ef` build the context without the
/// web host. The connection string is a placeholder — migrations describe the
/// schema, the path never matters.
/// </summary>
public class LlmRouterDbContextFactory : IDesignTimeDbContextFactory<LlmRouterDbContext>
{
    public LlmRouterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new LlmRouterDbContext(options);
    }
}
