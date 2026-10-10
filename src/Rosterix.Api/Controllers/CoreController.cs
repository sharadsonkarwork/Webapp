using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rosterix.Api.Auth;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;

namespace Rosterix.Api.Controllers;

[Route("api")]
public class CoreController : RxController
{
    /// <summary>Dev role-switcher directory. Empty outside Development (SEC-08).</summary>
    [HttpGet("personas"), AllowAnonymous]
    public async Task<IActionResult> Personas([FromServices] IWebHostEnvironment env, CancellationToken ct)
    {
        if (!env.IsDevelopment()) return Ok(Array.Empty<object>());
        var ids = Application.Data.Personas.All.Select(p => p.Id).ToList();
        var emps = await Db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        return Ok(Application.Data.Personas.All.Select(p => { var e = emps.First(x => x.Id == p.Id); return new { key = p.Key, id = p.Id, name = e.FullName, title = p.Title, blurb = p.Blurb }; }));
    }

    /// <summary>Current user, roles and the delegations that drive the delegation banner.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        var e = ds.Emp(Me.Id); var bu = ds.BU(e.BusinessUnitId); var region = ds.Regions.First(r => r.Id == bu.RegionId);
        var today = ds.Today;
        object D(Delegation d) => new { d.Id, from = EmpRef(ds.Emp(d.FromEmployeeId)), to = EmpRef(ds.Emp(d.ToEmployeeId)), scope = d.Scope.ToString(), d.ValidFrom, d.ValidTo, d.Reason, daysLeft = d.ValidTo.DayNumber - today.DayNumber };
        var roles = Policies.Split(e.Roles).Where(r => r != Role.EMP).Select(r => r.ToString()).ToList();
        var primary = new[] { Role.HRSS, Role.RH, Role.HRBP, Role.LM, Role.PM }.FirstOrDefault(r => e.Roles.HasFlag(r));
        return Ok(new
        {
            id = e.Id, name = e.FullName, e.Email, e.Grade, number = e.EmployeeNumber, roles, primaryRole = primary == Role.None ? "EMP" : primary.ToString(),
            businessUnit = new { bu.Id, bu.Name }, region = region.Name, today,
            policies = Policies.Map.Where(kv => (kv.Value & e.Roles) != 0).Select(kv => kv.Key),
            delegationsGiven = ds.Delegations.Where(d => d.FromEmployeeId == e.Id && d.IsActiveOn(today)).Select(D),
            delegationsReceived = ds.Delegations.Where(d => d.ToEmployeeId == e.Id && d.IsActiveOn(today)).Select(D),
            unreadNotifications = ds.Notifications.Count(n => n.RecipientEmployeeId == e.Id && n.ReadAtUtc == null),
            pendingApprovals = ds.ApprovalRequests.Count(r => r.Status == ApprovalStatus.Pending && ds.ApprovalSteps.Any(s => s.ApprovalRequestId == r.Id && s.StepIndex == r.CurrentStepIndex && s.AssignedToEmployeeId == e.Id)),
            aiLayer = new { provider = S<Application.Ai.IAiProvider>().Name, autoApply = false }
        });
    }

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups(CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var vis = VisibleProjects(ds);
        return Ok(new
        {
            businessUnits = ds.BusinessUnits.Select(b => new { b.Id, b.Code, b.Name, regionId = b.RegionId }),
            shiftTypes = ds.ShiftTypes.OrderBy(s => s.StartTime).Select(s => new { s.Id, s.Code, s.Name, start = s.StartTime.ToString("HH:mm"), end = s.EndTime.ToString("HH:mm"), s.SpansMidnight, s.IsAllowanceEligible, s.IsActive, hours = s.DurationHours }),
            projects = ds.Projects.Where(p => vis.Contains(p.Id)).Select(p => new { p.Id, p.Code, p.Name, status = p.Status.ToString(), p.BusinessUnitId }),
            today = ds.Today,
        });
    }

    [HttpGet("employees")]
    public async Task<IActionResult> Employees([FromQuery] string? q, [FromQuery] Guid? businessUnitId, [FromQuery] Role? role, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var ds = await LoadAsync(ct);
        var query = ds.Employees.Where(e => e.IsActive);
        if (businessUnitId != null) query = query.Where(e => e.BusinessUnitId == businessUnitId);
        if (role != null) query = query.Where(e => e.Roles.HasFlag(role.Value));
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(e => e.FullName.Contains(q, StringComparison.OrdinalIgnoreCase) || e.EmployeeNumber.Contains(q, StringComparison.OrdinalIgnoreCase));
        return Ok(query.OrderBy(e => e.FirstName).Take(Math.Clamp(take, 1, 200)).Select(e => new { e.Id, name = e.FullName, e.Grade, number = e.EmployeeNumber, businessUnit = ds.BU(e.BusinessUnitId).Name, roles = Policies.Split(e.Roles).Where(r => r != Role.EMP).Select(r => r.ToString()), e.IsShiftEligible, managerId = e.ManagerId }));
    }

    [HttpGet("employees/{id:guid}")]
    public async Task<IActionResult> Employee(Guid id, CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        var e = ds.EmpOrNull(id); if (e == null) return NotFound();
        var pref = ds.Preferences.FirstOrDefault(p => p.EmployeeId == id);
        var tags = ds.EmployeeTags.Where(t => t.EmployeeId == id).OrderByDescending(t => t.ValidFrom).Select(t => new { t.Id, project = ds.Proj(t.ProjectId).Name, t.ValidFrom, t.ValidTo, status = t.Status.ToString(), type = t.TagType.ToString() });
        return Ok(new { e.Id, name = e.FullName, e.Grade, number = e.EmployeeNumber, businessUnit = ds.BU(e.BusinessUnitId).Name, manager = ds.EmpOrNull(e.ManagerId)?.FullName, e.IsShiftEligible, e.JoinedOn, e.ExitedOn, preference = pref == null ? null : new { pref.PreferredShiftTypeId, preferredOffDay = pref.PreferredOffDay?.ToString(), pref.Note }, tags });
    }

    public sealed record PreferenceDto(Guid? PreferredShiftTypeId, DayOfWeek? PreferredOffDay, string? Note);

    /// <summary>The editable override that AI-UC-08 and AI-UC-03 respect. A person can edit their own; PM/LM/HRSS can edit their team's.</summary>
    [HttpPut("employees/{id:guid}/preferences")]
    public async Task<IActionResult> SetPreference(Guid id, PreferenceDto dto, CancellationToken ct)
    {
        if (id != Me.Id && !Me.Has(Role.PM | Role.LM | Role.HRSS)) return Forbid();
        var pref = await Db.Preferences.FirstOrDefaultAsync(p => p.EmployeeId == id, ct);
        if (pref == null) Db.Preferences.Add(pref = new EmployeePreference { EmployeeId = id, CreatedBy = Me.Id.ToString() });
        pref.PreferredShiftTypeId = dto.PreferredShiftTypeId; pref.PreferredOffDay = dto.PreferredOffDay; pref.Note = dto.Note?[..Math.Min(300, dto.Note.Length)]; pref.ModifiedAtUtc = DateTime.UtcNow; pref.ModifiedBy = Me.Id.ToString();
        await Db.SaveChangesAsync(ct);
        return Ok(new { pref.Id });
    }

    // ---- Notifications (in-app centre)
    [HttpGet("notifications")]
    public async Task<IActionResult> Notifications([FromQuery] bool unreadOnly = false, CancellationToken ct = default)
    {
        var items = await Db.Notifications.AsNoTracking().Where(n => n.RecipientEmployeeId == Me.Id && (!unreadOnly || n.ReadAtUtc == null)).OrderByDescending(n => n.SentAtUtc).Take(100).ToListAsync(ct);
        return Ok(items.Select(n => new { n.Id, n.Type, n.Title, n.Body, severity = n.Severity.ToString(), n.LinkRoute, n.ReadAtUtc, n.SentAtUtc, n.Channel }));
    }

    [HttpPost("notifications/{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var n = await Db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.RecipientEmployeeId == Me.Id, ct); if (n == null) return NotFound();
        n.ReadAtUtc ??= Clock.UtcNow; await Db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpPost("notifications/read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        foreach (var n in await Db.Notifications.Where(x => x.RecipientEmployeeId == Me.Id && x.ReadAtUtc == null).ToListAsync(ct)) n.ReadAtUtc = Clock.UtcNow;
        await Db.SaveChangesAsync(ct); return NoContent();
    }

    // ---- Audit trail (HRSS only)
    [HttpGet("audit"), Authorize(Policy = Policies.IsHrss)]
    public async Task<IActionResult> Audit([FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? q = null, CancellationToken ct = default)
    {
        var query = Db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(a => a.EventType.Contains(q) || a.Endpoint.Contains(q));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.OccurredAtUtc).Skip((Math.Max(1, page) - 1) * pageSize).Take(Math.Clamp(pageSize, 1, 200)).ToListAsync(ct);
        var names = await Db.Employees.AsNoTracking().Select(e => new { e.Id, e.FirstName, e.LastName }).ToDictionaryAsync(e => e.Id, e => e.FirstName + " " + e.LastName, ct);
        return Ok(new { total, rows = rows.Select(a => new { a.Id, a.EventType, a.OccurredAtUtc, actor = a.ActorEmployeeId != null && names.TryGetValue(a.ActorEmployeeId.Value, out var n) ? n : null, a.Succeeded, a.OriginIp, a.Endpoint, a.ResourceType, a.ResourceId, a.Detail }) });
    }

    /// <summary>SEC-02: approval chains are seeded and read-only. There is deliberately no write verb routed here.</summary>
    [HttpGet("approval-chains")]
    public async Task<IActionResult> Chains(CancellationToken ct)
    {
        var rows = await Db.Chains.AsNoTracking().OrderBy(c => c.RequestType).ThenBy(c => c.Condition).ThenBy(c => c.StepIndex).ToListAsync(ct);
        return Ok(rows.GroupBy(c => (c.RequestType, c.Condition)).Select(g => new { requestType = g.Key.RequestType.ToString(), condition = g.Key.Condition, steps = g.Select(c => c.RequiredRole.ToString()) }));
    }
}
