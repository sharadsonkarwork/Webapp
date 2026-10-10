using Microsoft.EntityFrameworkCore;
using Rosterix.Application.Ai;
using Rosterix.Application.Data;
using Rosterix.Infrastructure.Persistence;

namespace Rosterix.Infrastructure.Services;

/// <summary>Read-only snapshot loader: every query is AsNoTracking and the data source exposes no write method.</summary>
public sealed class EfAiDataSource : IAiDataSource
{
    private readonly RosterixDbContext _db; private readonly IClock _clock;
    public EfAiDataSource(RosterixDbContext db, IClock clock) { _db = db; _clock = clock; }

    public async Task<Dataset> LoadAsync(CancellationToken ct = default)
    {
        var q = _db;
        var ds = new Dataset
        {
            Today = _clock.Today,
            Regions = await q.Regions.AsNoTracking().ToListAsync(ct), BusinessUnits = await q.BusinessUnits.AsNoTracking().ToListAsync(ct),
            Employees = await q.Employees.AsNoTracking().ToListAsync(ct), Preferences = await q.Preferences.AsNoTracking().ToListAsync(ct),
            Delegations = await q.Delegations.AsNoTracking().ToListAsync(ct), Leaves = await q.Leaves.AsNoTracking().ToListAsync(ct),
            Projects = await q.Projects.AsNoTracking().ToListAsync(ct), ProjectShiftTags = await q.ProjectShiftTags.AsNoTracking().ToListAsync(ct),
            EmployeeTags = await q.EmployeeTags.AsNoTracking().ToListAsync(ct), Movements = await q.Movements.AsNoTracking().ToListAsync(ct),
            ShiftTypes = await q.ShiftTypes.AsNoTracking().ToListAsync(ct), ShiftRules = await q.ShiftRules.AsNoTracking().ToListAsync(ct),
            Rates = await q.Rates.AsNoTracking().ToListAsync(ct), Holidays = await q.Holidays.AsNoTracking().ToListAsync(ct),
            Rosters = await q.Rosters.AsNoTracking().ToListAsync(ct), RosterEntries = await q.RosterEntries.AsNoTracking().ToListAsync(ct),
            ApprovalRequests = await q.ApprovalRequests.AsNoTracking().ToListAsync(ct), ApprovalSteps = await q.ApprovalSteps.AsNoTracking().ToListAsync(ct),
            Chains = await q.Chains.AsNoTracking().ToListAsync(ct), Runs = await q.Runs.AsNoTracking().ToListAsync(ct), Lines = await q.Lines.AsNoTracking().ToListAsync(ct),
            Exceptions = await q.Exceptions.AsNoTracking().ToListAsync(ct), Arrears = await q.Arrears.AsNoTracking().ToListAsync(ct),
            Batches = await q.Batches.AsNoTracking().ToListAsync(ct), Declarations = await q.Declarations.AsNoTracking().ToListAsync(ct),
            Settlements = await q.Settlements.AsNoTracking().ToListAsync(ct), Notifications = await q.Notifications.AsNoTracking().ToListAsync(ct),
            AiFeedback = await q.AiFeedback.AsNoTracking().ToListAsync(ct),
        };
        return ds;
    }
}
