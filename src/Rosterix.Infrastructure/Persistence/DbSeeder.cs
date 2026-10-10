using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rosterix.Application.Data;

namespace Rosterix.Infrastructure.Persistence;

public sealed class DbSeeder
{
    private readonly RosterixDbContext _db; private readonly ILogger<DbSeeder> _log;
    public DbSeeder(RosterixDbContext db, ILogger<DbSeeder> log) { _db = db; _log = log; }

    public async Task MigrateAndSeedAsync(bool inMemory, CancellationToken ct = default)
    {
        if (inMemory) await _db.Database.EnsureCreatedAsync(ct); else await _db.Database.MigrateAsync(ct);
        if (await _db.Employees.AnyAsync(ct)) return;
        var ds = CanonicalSeed.Build();
        _db.ChangeTracker.AutoDetectChangesEnabled = false;
        _db.Regions.AddRange(ds.Regions); _db.BusinessUnits.AddRange(ds.BusinessUnits); _db.ShiftTypes.AddRange(ds.ShiftTypes);
        _db.ShiftRules.AddRange(ds.ShiftRules); _db.Rates.AddRange(ds.Rates); _db.Holidays.AddRange(ds.Holidays);
        // employees reference managers (self-FK): insert in two passes
        var withMgr = ds.Employees.ToDictionary(e => e.Id, e => e.ManagerId);
        foreach (var e in ds.Employees) e.ManagerId = null;
        _db.Employees.AddRange(ds.Employees);
        await _db.SaveChangesAsync(ct);
        foreach (var e in ds.Employees) { e.ManagerId = withMgr[e.Id]; _db.Entry(e).Property(x => x.ManagerId).IsModified = true; }
        await _db.SaveChangesAsync(ct);
        _db.Preferences.AddRange(ds.Preferences); _db.Delegations.AddRange(ds.Delegations); _db.Leaves.AddRange(ds.Leaves);
        _db.Projects.AddRange(ds.Projects); await _db.SaveChangesAsync(ct);
        _db.ProjectShiftTags.AddRange(ds.ProjectShiftTags); _db.EmployeeTags.AddRange(ds.EmployeeTags); _db.Movements.AddRange(ds.Movements);
        _db.Rosters.AddRange(ds.Rosters); await _db.SaveChangesAsync(ct);
        foreach (var chunk in ds.RosterEntries.Chunk(2000)) { _db.RosterEntries.AddRange(chunk); await _db.SaveChangesAsync(ct); }
        _db.Chains.AddRange(ds.Chains); _db.ApprovalRequests.AddRange(ds.ApprovalRequests); await _db.SaveChangesAsync(ct);
        _db.ApprovalSteps.AddRange(ds.ApprovalSteps);
        _db.Runs.AddRange(ds.Runs); _db.Batches.AddRange(ds.Batches); await _db.SaveChangesAsync(ct);
        foreach (var chunk in ds.Lines.Chunk(1000)) { _db.Lines.AddRange(chunk); await _db.SaveChangesAsync(ct); }
        _db.Exceptions.AddRange(ds.Exceptions); _db.Arrears.AddRange(ds.Arrears); _db.Declarations.AddRange(ds.Declarations);
        _db.Settlements.AddRange(ds.Settlements); _db.Notifications.AddRange(ds.Notifications);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.AutoDetectChangesEnabled = true;
        _log.LogInformation("Seeded {Employees} employees, {Entries} roster entries, {Lines} allowance lines", ds.Employees.Count, ds.RosterEntries.Count, ds.Lines.Count);
    }
}
