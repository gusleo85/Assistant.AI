using Justina.Core.Infrastructure.Persistence;
using Justina.Expense.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Justina.Api.Hosting;

/// <summary>
/// Used only by <c>dotnet ef</c>. The runtime context is built by DI; this supplies the same set of domain
/// model configurations so generated migrations match what the application actually maps.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<JustinaDbContext>
{
    public JustinaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<JustinaDbContext>()
            .UseSqlServer("Server=localhost;Database=Justina;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new JustinaDbContext(options, [new ExpenseModelConfiguration()]);
    }
}
