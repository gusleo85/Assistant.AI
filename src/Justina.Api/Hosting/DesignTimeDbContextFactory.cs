using Justina.Core.Infrastructure.Persistence;
using Justina.Expense.Infrastructure.Persistence;
using Justina.Recruitment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Justina.Api.Hosting;

/// <summary>
/// Used only by <c>dotnet ef</c>. The runtime context is built by DI; this supplies the same set of domain
/// model configurations so generated migrations match what the application actually maps.
///
/// Every configuration the application registers must be listed here. A missing one does not fail: the
/// design-time model simply lacks those tables, <c>migrations add</c> writes an empty migration because
/// it can see no difference, and the application then refuses to start with "the model has pending
/// changes" — pointing at a migration that exists and appears to have been applied. That cost a while to
/// see, which is why this list is worth checking whenever a domain gains its first table.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<JustinaDbContext>
{
    public JustinaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<JustinaDbContext>()
            .UseSqlServer("Server=localhost;Database=Justina;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new JustinaDbContext(
            options,
            [new ExpenseModelConfiguration(), new RecruitmentModelConfiguration()]);
    }
}
