using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rosterix.Infrastructure.Persistence;

/// <summary>Lets `dotnet ef` build the model without starting the API or reaching a database. The connection string is a placeholder: it is never used to connect.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<RosterixDbContext>
{
    public RosterixDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RosterixDbContext>().UseSqlServer("Server=design-time;Database=Rosterix;Trusted_Connection=True;TrustServerCertificate=True").Options);
}
