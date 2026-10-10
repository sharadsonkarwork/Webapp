using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Application.Data;

/// <summary>Builds the one RuleContext shape shared by the roster editor, the allowance calculator and AI-UC-04.</summary>
public static class RuleContextFactory
{
    public static RuleContext For(Dataset ds, Guid employeeId, Guid projectId, string month,
        IEnumerable<RosterEntry>? overrideEntries = null, IEnumerable<EmployeeTag>? extraTags = null, BackdateInfo? backdate = null)
    {
        var emp = ds.Emp(employeeId);
        var proj = ds.Proj(projectId);
        var from = ShiftMath.MonthStart(month); var to = ShiftMath.MonthEnd(month);
        var regionId = ds.BU(emp.BusinessUnitId).RegionId;
        var shifts = ds.ShiftTypes.ToDictionary(s => s.Id, ShiftInfo.From);

        // Entries: the employee's entries from every roster overlapping the window (±7d for rest/consecutive edges), with overrides replacing same (project,date).
        var winFrom = from.AddDays(-7); var winTo = to.AddDays(7);
        var rosterProject = ds.Rosters.ToDictionary(r => r.Id, r => r.ProjectId);
        var entries = ds.RosterEntries
            .Where(e => e.EmployeeId == employeeId && e.WorkDate >= winFrom && e.WorkDate <= winTo && rosterProject.ContainsKey(e.RosterId))
            .Select(e => new { e, ProjectId = rosterProject[e.RosterId] }).ToList();
        var list = entries.Select(x => new EntryInfo(x.e.WorkDate, x.e.EntryType, x.e.ShiftTypeId is { } sid && shifts.TryGetValue(sid, out var si) ? si : null, x.ProjectId)).ToList();
        if (overrideEntries != null)
        {
            var ov = overrideEntries.Where(o => o.EmployeeId == employeeId).ToList();
            list.RemoveAll(x => x.ProjectId == projectId && ov.Any(o => o.WorkDate == x.Date));
            list.AddRange(ov.Select(o => new EntryInfo(o.WorkDate, o.EntryType, o.ShiftTypeId is { } sid && shifts.TryGetValue(sid, out var si) ? si : null, projectId)));
        }
        // de-duplicate (project,date) keeping the last
        list = list.GroupBy(x => (x.ProjectId, x.Date)).Select(g => g.Last()).ToList();

        var tags = ds.EmployeeTags.Where(t => t.EmployeeId == employeeId && t.Status is TagStatus.Approved or TagStatus.Backtagged or TagStatus.PendingApproval)
            .Select(t => new TagInfo(t.ProjectId, t.ValidFrom, t.ValidTo, t.Status is TagStatus.Approved or TagStatus.Backtagged)).ToList();
        if (extraTags != null) tags.AddRange(extraTags.Select(t => new TagInfo(t.ProjectId, t.ValidFrom, t.ValidTo, t.Status is TagStatus.Approved or TagStatus.Backtagged)));

        return new RuleContext
        {
            EmployeeId = employeeId, EmployeeName = emp.FullName, Grade = emp.Grade, BusinessUnitId = emp.BusinessUnitId,
            ProjectId = projectId, Project = new ProjectInfo(proj.Id, proj.StartDate, proj.EndDate), Today = ds.Today,
            PeriodStart = from, PeriodEnd = to, Entries = list, Tags = tags,
            LeaveDays = ds.Leaves.Where(l => l.EmployeeId == employeeId && l.Approved).Select(l => l.Date).ToHashSet(),
            RestrictedHolidays = ds.Holidays.Where(h => h.RegionId == regionId && h.IsRestricted).Select(h => h.Date).ToHashSet(),
            Rates = ds.Rates.Select(r => new RateInfo(r.ShiftTypeId, r.BusinessUnitId, r.Grade, r.Amount, r.EffectiveFrom, r.EffectiveTo)).ToList(),
            Parameters = RuleParameters.From(ds.ShiftRules, from), Backdate = backdate
        };
    }
}
