using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rosterix.Api.Auth;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;
using Rosterix.Domain.Rules;
using Rosterix.Infrastructure.Services;

namespace Rosterix.Api.Controllers;

[Route("api/projects")]
public class ProjectsController : RxController
{
    private static object Item(Dataset ds, Project p) => new
    {
        p.Id, p.Code, p.Name, businessUnit = ds.BU(p.BusinessUnitId).Name, businessUnitId = p.BusinessUnitId, owner = ds.Emp(p.OwnerPmId).FullName, ownerId = p.OwnerPmId,
        status = p.Status.ToString(), p.IsShiftProject, p.StartDate, p.EndDate, p.CostCentre,
        teamSize = ds.EmployeeTags.Count(t => t.ProjectId == p.Id && t.Status is TagStatus.Approved or TagStatus.Backtagged && t.Covers(ds.Today)),
        pendingTags = ds.EmployeeTags.Count(t => t.ProjectId == p.Id && t.Status == TagStatus.PendingApproval),
        shiftTypes = p.ShiftTypeIds.Select(s => ds.Shift(s).Code)
    };

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ProjectStatus? status, [FromQuery] string? q, [FromQuery] Guid? businessUnitId, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var vis = VisibleProjects(ds);
        var items = ds.Projects.Where(p => vis.Contains(p.Id) && (status == null || p.Status == status) && (businessUnitId == null || p.BusinessUnitId == businessUnitId)
            && (string.IsNullOrWhiteSpace(q) || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Code.Contains(q, StringComparison.OrdinalIgnoreCase)));
        return Ok(items.OrderBy(p => p.Status).ThenBy(p => p.Code).Select(p => Item(ds, p)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var p = ds.Projects.FirstOrDefault(x => x.Id == id);
        if (p == null || !VisibleProjects(ds).Contains(id)) return NotFound();
        var tags = ds.EmployeeTags.Where(t => t.ProjectId == id).OrderBy(t => ds.Emp(t.EmployeeId).FirstName).Select(t => new { t.Id, employee = EmpRef(ds.Emp(t.EmployeeId)), t.ValidFrom, t.ValidTo, type = t.TagType.ToString(), status = t.Status.ToString() });
        var shiftTags = ds.ProjectShiftTags.Where(t => t.ProjectId == id).Select(t => new { t.Id, t.ValidFrom, t.ValidTo, status = t.Status.ToString(), shiftTypes = t.ShiftTypeIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => ds.Shift(Guid.Parse(s)).Name) });
        return Ok(new { project = Item(ds, p), tags, shiftTags, canEdit = Me.Id == p.OwnerPmId && p.Status is ProjectStatus.Draft or ProjectStatus.Active });
    }

    public sealed record ProjectDto(string Name, Guid BusinessUnitId, DateOnly StartDate, DateOnly? EndDate, string CostCentre, List<Guid> ShiftTypeIds, bool IsShiftProject, Guid? AiSuggestionId);

    private IActionResult? Validate(ProjectDto d)
    {
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 120) return Fail(400, "Give the project a name of up to 120 characters.");
        if (d.EndDate != null && d.EndDate < d.StartDate) return Fail(400, "The end date can't be before the start date.");
        if (string.IsNullOrWhiteSpace(d.CostCentre)) return Fail(400, "Enter a cost centre, for example CC-4101.");
        if (d.IsShiftProject && (d.ShiftTypeIds == null || d.ShiftTypeIds.Count == 0)) return Fail(400, "A shift project needs at least one shift type.");
        return null;
    }

    [HttpPost, Authorize(Policy = Policies.CanManageProjects)]
    public async Task<IActionResult> Create(ProjectDto d, CancellationToken ct)
    {
        if (Validate(d) is { } bad) return bad;
        var bu = await Db.BusinessUnits.AsNoTracking().FirstOrDefaultAsync(b => b.Id == d.BusinessUnitId, ct); if (bu == null) return Fail(400, "Choose a business unit.");
        var n = await Db.Projects.CountAsync(p => p.BusinessUnitId == bu.Id, ct) + 1;
        var code = $"P-{bu.Code}-{n:00}"; while (await Db.Projects.AnyAsync(p => p.Code == code, ct)) code = $"P-{bu.Code}-{++n:00}";
        var p = new Project { Code = code, Name = d.Name.Trim(), BusinessUnitId = bu.Id, OwnerPmId = Me.Id, StartDate = d.StartDate, EndDate = d.EndDate, CostCentre = d.CostCentre.Trim(), IsShiftProject = d.IsShiftProject, ShiftTypeIdsCsv = string.Join(',', d.ShiftTypeIds ?? new()), Status = ProjectStatus.Draft, CreatedBy = Me.Id.ToString() };
        Db.Projects.Add(p); await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = p.Id }, new { p.Id, p.Code });
    }

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.CanManageProjects)]
    public async Task<IActionResult> Update(Guid id, ProjectDto d, CancellationToken ct)
    {
        var p = await Db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct); if (p == null) return NotFound();
        if (p.OwnerPmId != Me.Id) return Fail(403, "Only the project's owner can edit it.");
        if (p.Status is ProjectStatus.PendingApproval or ProjectStatus.Closed) return Fail(409, $"A {p.Status} project can't be edited.");
        if (Validate(d) is { } bad) return bad;
        p.Name = d.Name.Trim(); p.StartDate = d.StartDate; p.EndDate = d.EndDate; p.CostCentre = d.CostCentre.Trim(); p.IsShiftProject = d.IsShiftProject; p.ShiftTypeIdsCsv = string.Join(',', d.ShiftTypeIds ?? new());
        p.ModifiedAtUtc = Clock.UtcNow; p.ModifiedBy = Me.Id.ToString();
        await Db.SaveChangesAsync(ct); return Ok(new { p.Id });
    }

    /// <summary>Tag the project as a shift project. This raises an approval; the project stays PendingApproval until the chain completes.</summary>
    [HttpPost("{id:guid}/shift-tag"), Authorize(Policy = Policies.CanManageProjects)]
    public async Task<IActionResult> ShiftTag(Guid id, CancellationToken ct)
    {
        var p = await Db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct); if (p == null) return NotFound();
        if (p.OwnerPmId != Me.Id) return Fail(403, "Only the project's owner can request shift tagging.");
        if (p.Status != ProjectStatus.Draft && !(p.Status == ProjectStatus.Active && !await Db.ProjectShiftTags.AnyAsync(t => t.ProjectId == id && t.Status == ShiftTagStatus.Approved, ct))) return Fail(409, "Only a draft project (or an active one without a shift tag) can be submitted.");
        if (string.IsNullOrEmpty(p.ShiftTypeIdsCsv)) return Fail(400, "Add at least one shift type to the project first.");
        var me = await Me.LoadAsync(ct);
        Db.ProjectShiftTags.Add(new ProjectShiftTag { ProjectId = id, ValidFrom = p.StartDate, ValidTo = p.EndDate, ShiftTypeIdsCsv = p.ShiftTypeIdsCsv, Status = ShiftTagStatus.PendingApproval, CreatedBy = Me.Id.ToString() });
        p.IsShiftProject = true; p.Status = ProjectStatus.PendingApproval;
        var req = await S<ApprovalService>().RaiseAsync(ApprovalType.ProjectShiftTagging, p.Id, $"Shift-tag {p.Name}", $"{p.Code}: {p.ShiftTypeIdsCsv.Split(',').Length} shift type(s) from {p.StartDate:d MMM yyyy}.", me, p.Id, null, p.StartDate, p.EndDate, null, null, ct);
        await Db.SaveChangesAsync(ct);
        return Ok(new { requestId = req.Id });
    }

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var p = ds.Projects.FirstOrDefault(x => x.Id == id);
        if (p == null || !VisibleProjects(ds).Contains(id)) return NotFound();
        var ev = new List<(DateTime At, string Kind, string Title, string Detail, string? Link)>();
        ev.Add((p.CreatedAtUtc, "Created", $"Project created ({p.Code})", $"Owner {ds.Emp(p.OwnerPmId).FullName}", null));
        foreach (var r in ds.ApprovalRequests.Where(r => r.ProjectId == id)) ev.Add((r.RaisedAtUtc, r.Type.ToString(), r.Title, $"{r.Status}, raised by {ds.Emp(r.RaisedBy).FullName}", "/approvals/" + r.Id));
        foreach (var r in ds.Rosters.Where(r => r.ProjectId == id)) ev.Add((r.PublishedAtUtc ?? r.CreatedAtUtc, "Roster", $"Roster {r.PeriodMonth} {r.Status.ToString().ToLowerInvariant()}", r.Source.ToString(), "/roster?project=" + id + "&month=" + r.PeriodMonth));
        foreach (var r in ds.Runs.Where(r => r.ProjectId == id)) ev.Add((r.CreatedAtUtc, "Allowance", $"Allowance run {r.PeriodMonth}", $"{r.Status}, ₹{r.TotalAmount:N0}", "/allowance/" + r.Id));
        return Ok(ev.OrderByDescending(e => e.At).Select(e => new { at = e.At, kind = e.Kind, title = e.Title, detail = e.Detail, link = e.Link }));
    }
}

[Route("api/employee-tags")]
public class TaggingController : RxController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? projectId, [FromQuery] Guid? employeeId, [FromQuery] TagStatus? status, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var vis = VisibleProjects(ds);
        var q = ds.EmployeeTags.Where(t => vis.Contains(t.ProjectId) && (projectId == null || t.ProjectId == projectId) && (employeeId == null || t.EmployeeId == employeeId) && (status == null || t.Status == status));
        return Ok(q.OrderBy(t => ds.Proj(t.ProjectId).Code).ThenBy(t => ds.Emp(t.EmployeeId).FirstName).Take(500).Select(t => new
        { t.Id, employee = EmpRef(ds.Emp(t.EmployeeId)), project = new { t.ProjectId, name = ds.Proj(t.ProjectId).Name, code = ds.Proj(t.ProjectId).Code }, t.ValidFrom, t.ValidTo, type = t.TagType.ToString(), status = t.Status.ToString(), t.ApprovalRequestId }));
    }

    public sealed record TagRequest(Guid ProjectId, List<Guid> EmployeeIds, DateOnly From, DateOnly? To, TagType TagType = TagType.Primary);
    public sealed record Viol(string RuleCode, string Severity, string Message, string? SuggestedFix);
    public sealed record TagCheck(Guid EmployeeId, string Name, List<Viol> Violations, bool Blocked);

    private static List<TagCheck> Check(Dataset ds, IRuleEngine rules, TagRequest r)
    {
        var res = new List<TagCheck>();
        var proj = ds.Proj(r.ProjectId); var backdated = r.From < ds.Today;
        foreach (var id in r.EmployeeIds.Distinct())
        {
            var e = ds.Emp(id); var list = new List<Viol>();
            var extra = new EmployeeTag { EmployeeId = id, ProjectId = r.ProjectId, ValidFrom = r.From, ValidTo = r.To, Status = TagStatus.PendingApproval };
            var ctx = RuleContextFactory.For(ds, id, r.ProjectId, ShiftMath.Month(r.From), null, new[] { extra }, backdated ? new BackdateInfo(r.ProjectId, r.From, r.To) : null);
            var existingSame = ctx.Tags.Count(t => t.ProjectId == r.ProjectId && t.From == r.From && t.To == r.To);
            foreach (var v in rules.Evaluate(ctx).Where(v => v.RuleCode is "BR-01" or "BR-12"))
                list.Add(new Viol(v.RuleCode, v.Severity.ToString(), v.Message, v.SuggestedFix));
            if (!e.IsShiftEligible) list.Add(new Viol("ELIG", "Block", $"{e.FullName} isn't shift-eligible.", "Ask HRSS to update the eligibility flag."));
            if (e.ExitedOn != null && e.ExitedOn < (r.To ?? DateOnly.MaxValue)) list.Add(new Viol("EXIT", "Warn", $"{e.FullName} exits on {e.ExitedOn:d MMM yyyy}, before the tag ends.", "Set the end date to their exit date."));
            if (r.From < proj.StartDate || (proj.EndDate != null && r.From > proj.EndDate)) list.Add(new Viol("BR-05", "Block", $"The tag starts outside the project's dates ({proj.StartDate:d MMM yyyy} – {(proj.EndDate?.ToString("d MMM yyyy") ?? "open")}).", "Choose a start date within the project's dates."));
            res.Add(new TagCheck(id, e.FullName, list, list.Any(x => x.Severity == "Block")));
        }
        return res;
    }

    [HttpPost("validate"), Authorize(Policy = Policies.CanManageProjects), ReadOnlyOperation]
    public async Task<IActionResult> Validate(TagRequest r, CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        return Ok(Check(ds, S<IRuleEngine>(), r));
    }

    [HttpPost, Authorize(Policy = Policies.CanManageProjects)]
    public async Task<IActionResult> Create(TagRequest r, CancellationToken ct)
    {
        if (r.EmployeeIds == null || r.EmployeeIds.Count == 0) return Fail(400, "Choose at least one employee.");
        if (r.To != null && r.To < r.From) return Fail(400, "The end date can't be before the start date.");
        var ds = await LoadAsync(ct); var proj = ds.Proj(r.ProjectId);
        if (!VisibleProjects(ds).Contains(proj.Id) || (proj.OwnerPmId != Me.Id && !Me.Has(Role.LM))) return Fail(403, "You can tag people only to projects you own or manage.");
        var checks = Check(ds, S<IRuleEngine>(), r);
        if (checks.Any(c => c.Blocked)) return Fail(400, "Some people can't be tagged. Fix the blocking conflicts first.", checks.Where(c => c.Blocked));
        var me = await Me.LoadAsync(ct);
        var names = string.Join(", ", r.EmployeeIds.Take(3).Select(i => ds.Emp(i).FirstName)) + (r.EmployeeIds.Count > 3 ? $" +{r.EmployeeIds.Count - 3}" : "");
        var req = await S<ApprovalService>().RaiseAsync(ApprovalType.EmployeeTagging, proj.Id, $"Tag {r.EmployeeIds.Count} people to {proj.Name}", $"{names}; from {r.From:d MMM yyyy}{(r.To != null ? $" to {r.To:d MMM yyyy}" : "")}.", me, proj.Id, null, r.From, r.To, null, null, ct);
        foreach (var id in r.EmployeeIds.Distinct())
            Db.EmployeeTags.Add(new EmployeeTag { EmployeeId = id, ProjectId = proj.Id, ValidFrom = r.From, ValidTo = r.To, TagType = r.TagType, Status = TagStatus.PendingApproval, ApprovalRequestId = req.Id, CreatedBy = Me.Id.ToString() });
        await Db.SaveChangesAsync(ct);
        return Ok(new { requestId = req.Id, count = r.EmployeeIds.Distinct().Count(), warnings = checks.SelectMany(c => c.Violations.Select(v => new { c.Name, v })).Count() });
    }

    public sealed record BulkRow(string EmployeeNumber, DateOnly From, DateOnly? To);
    public sealed record BulkRequest(Guid ProjectId, List<BulkRow> Rows, TagType TagType = TagType.Primary);

    /// <summary>Bulk CSV: row-level validation. Nothing is saved here; the client submits the valid rows through POST /api/employee-tags.</summary>
    [HttpPost("bulk-validate"), Authorize(Policy = Policies.CanManageProjects), ReadOnlyOperation]
    public async Task<IActionResult> BulkValidate(BulkRequest b, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var rules = S<IRuleEngine>();
        var byNo = ds.Employees.ToDictionary(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase);
        var results = new List<object>(); var row = 1;
        foreach (var r in b.Rows.Take(1000))
        {
            row++;
            if (!byNo.TryGetValue(r.EmployeeNumber?.Trim() ?? "", out var e)) { results.Add(new { row, r.EmployeeNumber, ok = false, employeeId = (Guid?)null, name = (string?)null, messages = new[] { $"No employee with number '{r.EmployeeNumber}'." }, r.From, r.To }); continue; }
            if (r.To != null && r.To < r.From) { results.Add(new { row, r.EmployeeNumber, ok = false, employeeId = (Guid?)e.Id, name = e.FullName, messages = new[] { "End date is before start date." }, r.From, r.To }); continue; }
            var chk = Check(ds, rules, new TagRequest(b.ProjectId, new List<Guid> { e.Id }, r.From, r.To, b.TagType))[0];
            results.Add(new { row, r.EmployeeNumber, ok = !chk.Blocked, employeeId = (Guid?)e.Id, name = e.FullName, messages = chk.Violations.Select(v => v.Message).ToArray(), r.From, r.To });
        }
        return Ok(results);
    }
}
