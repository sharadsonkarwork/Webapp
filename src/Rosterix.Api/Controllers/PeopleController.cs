using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rosterix.Application.Ai;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;
using Rosterix.Domain.Rules;
using Rosterix.Infrastructure.Services;

namespace Rosterix.Api.Controllers;

[Route("api/movements"), Authorize(Policy = Policies.IsHrss)]
public class MovementsController : RxController
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        return Ok(ds.Movements.OrderByDescending(m => m.EffectiveFrom).Select(m =>
        {
            var req = ds.ApprovalRequests.FirstOrDefault(r => r.Type == ApprovalType.Movement && r.SubjectId == m.Id);
            return new { m.Id, employee = EmpRef(ds.Emp(m.EmployeeId)), from = m.FromProjectId == null ? null : ds.Proj(m.FromProjectId.Value).Name, to = ds.Proj(m.ToProjectId).Name, m.EffectiveFrom, m.EffectiveTo, m.IsBackdated, m.Reason, status = m.Status.ToString(), approvalStatus = req?.Status.ToString(), approvalId = req?.Id, canCommit = m.Status == MovementStatus.Staged && (!m.IsBackdated || req?.Status == ApprovalStatus.Approved) };
        }));
    }

    public sealed record MoveDto(Guid EmployeeId, Guid? FromProjectId, Guid ToProjectId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Reason);

    /// <summary>Step 1 of a two-step commit: stage the movement (and raise approval if backdated). Nothing downstream changes yet.</summary>
    [HttpPost]
    public async Task<IActionResult> Stage(MoveDto d, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(d.Reason)) return Fail(400, "Give a reason for the movement.");
        if (d.EffectiveTo != null && d.EffectiveTo < d.EffectiveFrom) return Fail(400, "The end date can't be before the start date.");
        var ds = await LoadAsync(ct);
        if (d.FromProjectId == d.ToProjectId) return Fail(400, "Choose a different destination project.");
        var rip = await S<ConflictRippleAi>().SuggestAsync(new RippleRequest(d.EmployeeId, d.FromProjectId, d.ToProjectId, d.EffectiveFrom, d.EffectiveTo), ct);
        if (rip.Payload.HasBlock) return Fail(400, "This movement breaks a blocking rule.", rip.Payload.Violations);
        var backdated = d.EffectiveFrom < ds.Today;
        var m = new Movement { EmployeeId = d.EmployeeId, FromProjectId = d.FromProjectId, ToProjectId = d.ToProjectId, EffectiveFrom = d.EffectiveFrom, EffectiveTo = d.EffectiveTo, IsBackdated = backdated, Reason = d.Reason.Trim(), Status = MovementStatus.Staged, RippleJson = JsonSerializer.Serialize(rip.Payload.Rows), StagedBy = Me.Id.ToString(), CreatedBy = Me.Id.ToString() };
        Db.Movements.Add(m); Guid? reqId = null;
        if (backdated)
        {
            var me = await Me.LoadAsync(ct); var emp = ds.Emp(d.EmployeeId);
            var req = await S<ApprovalService>().RaiseAsync(ApprovalType.Movement, m.Id, $"Move {emp.FullName} to {ds.Proj(d.ToProjectId).Name}", $"Backdated to {d.EffectiveFrom:d MMM yyyy}. {d.Reason}", me, d.ToProjectId, null, d.EffectiveFrom, d.EffectiveTo, d.EmployeeId, null, ct);
            reqId = req.Id;
        }
        await Db.SaveChangesAsync(ct);
        return Ok(new { m.Id, backdated, approvalId = reqId });
    }

    /// <summary>Step 2: commit. Backdated movements need their approval first.</summary>
    [HttpPost("{id:guid}/commit")]
    public async Task<IActionResult> Commit(Guid id, CancellationToken ct)
    {
        var m = await Db.Movements.FirstOrDefaultAsync(x => x.Id == id, ct); if (m == null) return NotFound();
        if (m.Status != MovementStatus.Staged) return Fail(409, $"This movement is already {m.Status.ToString().ToLowerInvariant()}.");
        if (m.IsBackdated && !await Db.ApprovalRequests.AnyAsync(r => r.Type == ApprovalType.Movement && r.SubjectId == m.Id && r.Status == ApprovalStatus.Approved, ct)) return Fail(409, "A backdated movement needs its approval to complete before it can be committed.");
        var ds = await LoadAsync(ct);
        var rip = await S<ConflictRippleAi>().SuggestAsync(new RippleRequest(m.EmployeeId, m.FromProjectId, m.ToProjectId, m.EffectiveFrom, m.EffectiveTo), ct);
        if (rip.Payload.HasBlock) return Fail(400, "A rule conflict appeared since staging. Re-check the preview.", rip.Payload.Violations);
        var fromId = m.FromProjectId;
        foreach (var t in await Db.EmployeeTags.Where(t => t.EmployeeId == m.EmployeeId && (fromId == null || t.ProjectId == fromId) && t.Status != TagStatus.Rejected && t.Status != TagStatus.Withdrawn && (t.ValidTo == null || t.ValidTo >= m.EffectiveFrom)).ToListAsync(ct))
        {
            if (t.ValidFrom >= m.EffectiveFrom) t.Status = TagStatus.Withdrawn; else t.ValidTo = m.EffectiveFrom.AddDays(-1);
            t.ModifiedAtUtc = Clock.UtcNow; t.ModifiedBy = Me.Id.ToString();
        }
        Db.EmployeeTags.Add(new EmployeeTag { EmployeeId = m.EmployeeId, ProjectId = m.ToProjectId, ValidFrom = m.EffectiveFrom, ValidTo = m.EffectiveTo, TagType = TagType.Primary, Status = m.IsBackdated ? TagStatus.Backtagged : TagStatus.Approved, SourceMovementId = m.Id, CreatedBy = Me.Id.ToString() });
        var removed = 0;
        if (fromId != null)
        {
            var rosterIds = await Db.Rosters.Where(r => r.ProjectId == fromId && r.Status != RosterStatus.Locked).Select(r => r.Id).ToListAsync(ct);
            var entries = await Db.RosterEntries.Where(e => e.EmployeeId == m.EmployeeId && rosterIds.Contains(e.RosterId) && e.WorkDate >= m.EffectiveFrom && (m.EffectiveTo == null || e.WorkDate <= m.EffectiveTo)).ToListAsync(ct);
            removed = entries.Count; Db.RosterEntries.RemoveRange(entries);
            var runIds = ds.Runs.Where(r => r.ProjectId == fromId && r.Status is RunStatus.Submitted or RunStatus.CompensationApproved).Select(r => r.Id).ToHashSet();
            foreach (var g in ds.Lines.Where(l => l.EmployeeId == m.EmployeeId && runIds.Contains(l.AllowanceRunId) && l.WorkDate >= m.EffectiveFrom).GroupBy(l => ShiftMath.Month(l.WorkDate)))
                Db.Arrears.Add(new Arrear { EmployeeId = m.EmployeeId, ProjectId = fromId.Value, ForPeriodMonth = g.Key, Amount = g.Sum(l => l.Amount), Status = ArrearStatus.Raised, Reason = $"Recovery review after movement effective {m.EffectiveFrom:d MMM yyyy}.", RaisedAtUtc = Clock.UtcNow, CreatedBy = Me.Id.ToString() });
        }
        m.Status = MovementStatus.Committed; m.CommittedBy = Me.Id.ToString(); m.ModifiedAtUtc = Clock.UtcNow;
        Notifier.Notify(m.EmployeeId, "Movement", "Your project assignment changed", $"You move to {ds.Proj(m.ToProjectId).Name} from {m.EffectiveFrom:d MMM yyyy}.", NotificationSeverity.Info, "/me");
        await Db.SaveChangesAsync(ct);
        return Ok(new { m.Id, rosterEntriesRemoved = removed });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var m = await Db.Movements.FirstOrDefaultAsync(x => x.Id == id, ct); if (m == null) return NotFound();
        if (m.Status != MovementStatus.Staged) return Fail(409, "Only a staged movement can be cancelled.");
        m.Status = MovementStatus.Cancelled; await Db.SaveChangesAsync(ct); return NoContent();
    }
}

[Route("api/delegations")]
public class DelegationsController : RxController
{
    private object Row(Dataset ds, Delegation d) => new { d.Id, from = EmpRef(ds.Emp(d.FromEmployeeId)), to = EmpRef(ds.Emp(d.ToEmployeeId)), scope = d.Scope.ToString(), d.ValidFrom, d.ValidTo, d.Reason, d.IsRevoked, active = d.IsActiveOn(ds.Today), daysLeft = d.ValidTo.DayNumber - ds.Today.DayNumber, expiringSoon = d.IsActiveOn(ds.Today) && d.ValidTo.DayNumber - ds.Today.DayNumber <= 3 };

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        var mine = ds.Delegations.Where(d => d.FromEmployeeId == Me.Id || d.ToEmployeeId == Me.Id || Me.Has(Role.HRSS)).OrderByDescending(d => d.ValidTo);
        return Ok(new { given = mine.Where(d => d.FromEmployeeId == Me.Id).Select(d => Row(ds, d)), received = mine.Where(d => d.ToEmployeeId == Me.Id).Select(d => Row(ds, d)), others = Me.Has(Role.HRSS) ? mine.Where(d => d.FromEmployeeId != Me.Id && d.ToEmployeeId != Me.Id).Select(d => Row(ds, d)) : Enumerable.Empty<object>() });
    }

    /// <summary>Everyone I could consider, each with the BR-11 verdict, so the form can say why someone isn't eligible.</summary>
    [HttpGet("candidates"), Authorize(Policy = Policies.CanDelegate)]
    public async Task<IActionResult> Candidates(CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var me = ds.Emp(Me.Id);
        var pool = ds.Employees.Where(e => e.IsActive && e.Id != me.Id && (e.Roles & me.Roles & (Role.PM | Role.LM)) != 0);
        return Ok(pool.Select(e => { var c = DelegationValidator.Validate(ds, me.Id, e.Id); return new { e.Id, name = e.FullName, e.Grade, number = e.EmployeeNumber, businessUnit = ds.BU(e.BusinessUnitId).Name, eligible = c.Allowed, reason = c.Violations.FirstOrDefault()?.Message }; }).OrderByDescending(x => x.eligible).ThenBy(x => x.name));
    }

    public sealed record DelegDto(Guid ToEmployeeId, DelegationScope Scope, DateOnly ValidFrom, DateOnly ValidTo, string Reason);

    /// <summary>SEC-03: only to a same-role, same-grade peer who isn't a subordinate. Violations return 400.</summary>
    [HttpPost, Authorize(Policy = Policies.CanDelegate)]
    public async Task<IActionResult> Create(DelegDto d, CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        if (ds.EmpOrNull(d.ToEmployeeId) == null) return Fail(400, "Choose who to delegate to.");
        var check = DelegationValidator.Validate(ds, Me.Id, d.ToEmployeeId);
        if (!check.Allowed) return Fail(400, check.Violations[0].Message, check.Violations);
        if (d.ValidTo < d.ValidFrom) return Fail(400, "The end date can't be before the start date.");
        if (d.ValidTo.DayNumber - d.ValidFrom.DayNumber > 90) return Fail(400, "A delegation can last at most 90 days. Renew it if you need longer.");
        if (d.ValidTo < ds.Today) return Fail(400, "The delegation would already have expired.");
        if (string.IsNullOrWhiteSpace(d.Reason)) return Fail(400, "Add a short reason, for example 'Annual leave'.");
        if (ds.Delegations.Any(x => x.FromEmployeeId == Me.Id && x.Scope == d.Scope && !x.IsRevoked && x.ValidFrom <= d.ValidTo && x.ValidTo >= d.ValidFrom)) return Fail(409, "You already have a delegation for this scope in those dates.");
        var del = new Delegation { FromEmployeeId = Me.Id, ToEmployeeId = d.ToEmployeeId, Scope = d.Scope, ValidFrom = d.ValidFrom, ValidTo = d.ValidTo, Reason = d.Reason.Trim(), CreatedBy = Me.Id.ToString() };
        Db.Delegations.Add(del);
        Notifier.Notify(d.ToEmployeeId, "DelegationStarted", $"{ds.Emp(Me.Id).FullName} delegated {d.Scope} to you", $"{d.ValidFrom:d MMM} to {d.ValidTo:d MMM yyyy}. {d.Reason}", NotificationSeverity.Info, "/delegation");
        await Db.SaveChangesAsync(ct);
        return Ok(new { del.Id });
    }

    [HttpPost("{id:guid}/revoke"), Authorize(Policy = Policies.CanDelegate)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var d = await Db.Delegations.FirstOrDefaultAsync(x => x.Id == id && x.FromEmployeeId == Me.Id, ct); if (d == null) return NotFound();
        d.IsRevoked = true; d.ModifiedAtUtc = Clock.UtcNow; d.ModifiedBy = Me.Id.ToString();
        Notifier.Notify(d.ToEmployeeId, "DelegationEnded", "A delegation to you was withdrawn", d.Reason, NotificationSeverity.Warning, "/delegation");
        await Db.SaveChangesAsync(ct); return NoContent();
    }
}

[Route("api/arrears")]
public class ArrearsController : RxController
{
    static string ConditionFor(int age) => age <= 3 ? "0-3 months" : age <= 6 ? "4-6 months" : ">6 months";
    static string RouteText(int age) => age <= 3 ? "HRBP" : age <= 6 ? "Regional Head" : "Regional Head, then HRSS";

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? bucket, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var vis = VisibleProjects(ds);
        var risk = S<ApprovalRiskAi>().Score(ds, new RiskRequest()).Payload.Items.ToDictionary(i => i.RequestId);
        var q = ds.Arrears.Where(a => vis.Contains(a.ProjectId) && (bucket == null || a.AgeBucket(ds.Today) == bucket));
        return Ok(q.OrderByDescending(a => a.AgeMonths(ds.Today)).Select(a =>
        {
            var age = a.AgeMonths(ds.Today); var r = a.ApprovalRequestId != null && risk.TryGetValue(a.ApprovalRequestId.Value, out var ri) ? ri : null;
            return new { a.Id, employee = EmpRef(ds.Emp(a.EmployeeId)), project = ds.Proj(a.ProjectId).Name, forMonth = a.ForPeriodMonth, a.Amount, a.Reason, status = a.Status.ToString(), ageMonths = age, bucket = a.AgeBucket(ds.Today), route = RouteText(age), a.ApprovalRequestId, risk = r == null ? null : new { score = r.Score, band = r.Band, headline = r.Headline }, canRoute = a.Status == ArrearStatus.Raised };
        }));
    }

    public sealed record ArrearDto(Guid EmployeeId, Guid ProjectId, string ForPeriodMonth, decimal Amount, string Reason);

    [HttpPost, Authorize(Policy = Policies.CanManageProjects)]
    public async Task<IActionResult> Raise(ArrearDto d, CancellationToken ct)
    {
        if (!ValidMonth(d.ForPeriodMonth)) return Fail(400, "Use a month like 2026-08.");
        if (d.Amount <= 0) return Fail(400, "Enter an amount greater than zero.");
        if (string.IsNullOrWhiteSpace(d.Reason)) return Fail(400, "Say why the arrear arose.");
        var ds = await LoadAsync(ct); var p = ds.Projects.FirstOrDefault(x => x.Id == d.ProjectId); if (p == null || !VisibleProjects(ds).Contains(p.Id)) return NotFound();
        var a = new Arrear { EmployeeId = d.EmployeeId, ProjectId = d.ProjectId, ForPeriodMonth = d.ForPeriodMonth, Amount = d.Amount, Reason = d.Reason.Trim(), Status = ArrearStatus.Raised, RaisedAtUtc = Clock.UtcNow, CreatedBy = Me.Id.ToString() };
        Db.Arrears.Add(a); await Db.SaveChangesAsync(ct); return Ok(new { a.Id });
    }

    /// <summary>Routes by age bucket: 0–3 months → HRBP; 4–6 → Regional Head; &gt;6 → Regional Head then HRSS.</summary>
    [HttpPost("{id:guid}/route")]
    public async Task<IActionResult> Route(Guid id, CancellationToken ct)
    {
        var a = await Db.Arrears.FirstOrDefaultAsync(x => x.Id == id, ct); if (a == null) return NotFound();
        if (!Me.Has(Role.PM | Role.HRBP | Role.HRSS)) return Forbid();
        if (a.Status != ArrearStatus.Raised) return Fail(409, $"This arrear is already {a.Status}.");
        var age = a.AgeMonths(Clock.Today); var me = await Me.LoadAsync(ct); var emp = await Db.Employees.AsNoTracking().FirstAsync(e => e.Id == a.EmployeeId, ct);
        var pm = await Db.Employees.AsNoTracking().FirstAsync(e => e.Id == Db.Projects.First(p => p.Id == a.ProjectId).OwnerPmId, ct);
        var req = await S<ApprovalService>().RaiseAsync(ApprovalType.Arrear, a.Id, $"Arrear for {emp.FirstName} {emp.LastName}, {a.ForPeriodMonth}", a.Reason, pm, a.ProjectId, a.Amount, null, null, a.EmployeeId, ConditionFor(age), ct);
        a.Status = ArrearStatus.PendingApproval; a.ApprovalRequestId = req.Id;
        await Db.SaveChangesAsync(ct);
        return Ok(new { requestId = req.Id, route = RouteText(age) });
    }
}

[Route("api/settlements"), Authorize(Policy = Policies.IsHrss)]
public class SettlementsController : RxController
{
    private static (decimal allowance, decimal arrears, int unresolved) Totals(Dataset ds, Settlement s)
    {
        var runs = ds.Runs.Where(r => r.Status is not RunStatus.Submitted).Select(r => r.Id).ToHashSet();
        var a = ds.Arrears.Where(x => x.EmployeeId == s.EmployeeId && x.Status != ArrearStatus.Paid && x.Status != ArrearStatus.Rejected).ToList();
        return (ds.Lines.Where(l => l.EmployeeId == s.EmployeeId && runs.Contains(l.AllowanceRunId)).Sum(l => l.Amount), a.Sum(x => x.Amount), a.Count(x => x.Status is ArrearStatus.Raised or ArrearStatus.PendingApproval));
    }

    private object Compute(Dataset ds, Settlement s)
    {
        var runs = ds.Runs.Where(r => r.Status is not RunStatus.Submitted).Select(r => r.Id).ToHashSet();
        var lines = ds.Lines.Where(l => l.EmployeeId == s.EmployeeId && runs.Contains(l.AllowanceRunId)).ToList();
        var arrears = ds.Arrears.Where(a => a.EmployeeId == s.EmployeeId && a.Status != ArrearStatus.Paid && a.Status != ArrearStatus.Rejected).ToList();
        return new
        {
            s.Id, employee = EmpRef(ds.Emp(s.EmployeeId)), s.ExitDate, daysToExit = s.ExitDate.DayNumber - ds.Today.DayNumber, status = s.ClearanceStatus.ToString(), pendingAllowance = lines.Sum(l => l.Amount), pendingArrears = arrears.Sum(a => a.Amount), total = lines.Sum(l => l.Amount) + arrears.Sum(a => a.Amount),
            allowanceRuns = lines.GroupBy(l => l.AllowanceRunId).Select(g => new { run = ds.Runs.First(r => r.Id == g.Key).PeriodMonth, project = ds.Proj(ds.Runs.First(r => r.Id == g.Key).ProjectId).Name, status = ds.Runs.First(r => r.Id == g.Key).Status.ToString(), shifts = g.Count(), amount = g.Sum(l => l.Amount) }),
            arrears = arrears.Select(a => new { a.Id, forMonth = a.ForPeriodMonth, a.Amount, status = a.Status.ToString(), a.Reason }),
            unresolved = arrears.Count(a => a.Status is ArrearStatus.Raised or ArrearStatus.PendingApproval), clearedBy = ds.EmpOrNull(s.ClearedBy)?.FullName, s.ClearedAtUtc
        };
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) { var ds = await LoadAsync(ct); return Ok(ds.Settlements.OrderBy(s => s.ExitDate).Select(s => Compute(ds, s))); }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) { var ds = await LoadAsync(ct); var s = ds.Settlements.FirstOrDefault(x => x.Id == id); return s == null ? NotFound() : Ok(Compute(ds, s)); }

    public sealed record ClearDto(bool AcknowledgePending);

    [HttpPost("{id:guid}/clear")]
    public async Task<IActionResult> Clear(Guid id, ClearDto d, CancellationToken ct)
    {
        var s = await Db.Settlements.FirstOrDefaultAsync(x => x.Id == id, ct); if (s == null) return NotFound();
        if (s.ClearanceStatus == ClearanceStatus.Cleared) return Fail(409, "This settlement is already cleared.");
        var ds = await LoadAsync(ct); var (allowance, arrears, unresolved) = Totals(ds, s);
        if (unresolved > 0 && !d.AcknowledgePending) return Fail(409, $"{unresolved} arrear(s) are still unresolved. Acknowledge them to clear the settlement.");
        s.PendingAllowanceTotal = allowance; s.PendingArrearTotal = arrears;
        s.ClearanceStatus = ClearanceStatus.Cleared; s.ClearedBy = Me.Id; s.ClearedAtUtc = Clock.UtcNow;
        await Db.SaveChangesAsync(ct); return Ok(new { status = "Cleared" });
    }
}

[Route("api/me")]
public class SelfServiceController : RxController
{
    [HttpGet("roster")]
    public async Task<IActionResult> MyRoster([FromQuery] string month, CancellationToken ct)
    {
        if (!ValidMonth(month)) return Fail(400, "Use a month like 2026-10.");
        var ds = await LoadAsync(ct); var from = ShiftMath.MonthStart(month); var to = ShiftMath.MonthEnd(month);
        var rosters = ds.Rosters.Where(r => r.PeriodMonth == month && r.Status != RosterStatus.Draft).ToDictionary(r => r.Id);
        var bu = ds.BU(ds.Emp(Me.Id).BusinessUnitId);
        var hol = ds.Holidays.Where(h => h.RegionId == bu.RegionId && h.Date >= from && h.Date <= to).ToDictionary(h => h.Date);
        var entries = ds.RosterEntries.Where(e => e.EmployeeId == Me.Id && rosters.ContainsKey(e.RosterId)).OrderBy(e => e.WorkDate).Select(e => new { date = e.WorkDate, type = e.EntryType.ToString(), shift = e.ShiftTypeId == null ? null : ds.Shift(e.ShiftTypeId.Value).Name, code = e.ShiftTypeId == null ? null : ds.Shift(e.ShiftTypeId.Value).Code, time = e.ShiftTypeId == null ? null : $"{ds.Shift(e.ShiftTypeId.Value).StartTime:HH\\:mm}–{ds.Shift(e.ShiftTypeId.Value).EndTime:HH\\:mm}", project = ds.Proj(rosters[e.RosterId].ProjectId).Name, holiday = hol.TryGetValue(e.WorkDate, out var h) ? h.Name : null }).ToList();
        var changes = await Db.RosterChangeLogs.AsNoTracking().Where(c => c.EmployeeId == Me.Id && c.WorkDate >= from && c.WorkDate <= to).OrderByDescending(c => c.ChangedAtUtc).Take(30).ToListAsync(ct);
        var draftExists = ds.Rosters.Any(r => r.PeriodMonth == month && r.Status == RosterStatus.Draft && ds.RosterEntries.Any(e => e.RosterId == r.Id && e.EmployeeId == Me.Id));
        return Ok(new { month, entries, shifts = entries.Count(e => e.type == "Shift"), offs = entries.Count(e => e.type == "WeeklyOff"), draftPending = draftExists && entries.Count == 0, changes = changes.Select(c => new { c.WorkDate, c.Before, c.After, c.ChangedAtUtc, c.LeadTimeFlag, c.Reason }) });
    }

    [HttpGet("allowance")]
    public async Task<IActionResult> MyAllowance([FromQuery] string month, CancellationToken ct)
        => !ValidMonth(month) ? Fail(400, "Use a month like 2026-09.") : Ok(await S<MyAllowanceExplainer>().SuggestAsync(new MyAllowanceRequest(Me.Id, month), ct));

    [HttpGet("declarations")]
    public async Task<IActionResult> Declarations(CancellationToken ct)
        => Ok((await Db.Declarations.AsNoTracking().Where(d => d.EmployeeId == Me.Id).OrderByDescending(d => d.PeriodMonth).ToListAsync(ct)).Select(d => new { d.Id, d.PeriodMonth, d.Type, status = d.Status.ToString(), d.SignedAtUtc }));

    [HttpPost("declarations/{id:guid}/sign")]
    public async Task<IActionResult> Sign(Guid id, CancellationToken ct)
    {
        var d = await Db.Declarations.FirstOrDefaultAsync(x => x.Id == id && x.EmployeeId == Me.Id, ct); if (d == null) return NotFound();
        if (d.Status == DeclarationStatus.Signed) return Fail(409, "You've already signed this declaration.");
        d.Status = DeclarationStatus.Signed; d.SignedAtUtc = Clock.UtcNow; d.ModifiedAtUtc = Clock.UtcNow; d.ModifiedBy = Me.Id.ToString();
        await Db.SaveChangesAsync(ct); return Ok(new { d.Id, status = "Signed" });
    }

    public sealed record QueryDto(string Month, Guid? AllowanceLineId, string Message);

    [HttpPost("queries")]
    public async Task<IActionResult> RaiseQuery(QueryDto d, CancellationToken ct)
    {
        if (!ValidMonth(d.Month)) return Fail(400, "Use a month like 2026-09.");
        if (string.IsNullOrWhiteSpace(d.Message) || d.Message.Length > 1000) return Fail(400, "Describe your question in up to 1,000 characters.");
        var q = new EmployeeQuery { EmployeeId = Me.Id, PeriodMonth = d.Month, AllowanceLineId = d.AllowanceLineId, Message = d.Message.Trim(), CreatedBy = Me.Id.ToString() };
        Db.Queries.Add(q);
        var hrss = (await Db.Employees.AsNoTracking().ToListAsync(ct)).Where(e => e.Roles.HasFlag(Role.HRSS)).Take(2);
        foreach (var h in hrss) Notifier.Notify(h.Id, "EmployeeQuery", "An employee raised an allowance query", d.Message.Trim()[..Math.Min(120, d.Message.Trim().Length)], NotificationSeverity.Info, "/exceptions");
        await Db.SaveChangesAsync(ct); return Ok(new { q.Id });
    }

    [HttpGet("queries")]
    public async Task<IActionResult> MyQueries(CancellationToken ct)
        => Ok((await Db.Queries.AsNoTracking().Where(q => q.EmployeeId == Me.Id).OrderByDescending(q => q.CreatedAtUtc).ToListAsync(ct)).Select(q => new { q.Id, q.PeriodMonth, q.Message, q.Status, q.CreatedAtUtc }));
}

[Route("api/foundation")]
public class FoundationController : RxController
{
    [HttpGet("shift-types")]
    public async Task<IActionResult> ShiftTypes(CancellationToken ct) => Ok((await Db.ShiftTypes.AsNoTracking().OrderBy(s => s.StartTime).ToListAsync(ct)).Select(s => new { s.Id, s.Code, s.Name, start = s.StartTime.ToString("HH:mm"), end = s.EndTime.ToString("HH:mm"), s.SpansMidnight, s.IsAllowanceEligible, s.IsActive, hours = s.DurationHours }));

    public sealed record ShiftTypeDto(string Code, string Name, string Start, string End, bool IsAllowanceEligible, bool IsActive);

    private IActionResult? ValidateShift(ShiftTypeDto d, out TimeOnly s, out TimeOnly e)
    {
        s = default; e = default;
        if (string.IsNullOrWhiteSpace(d.Code) || d.Code.Length > 20) return Fail(400, "Give the shift a short code, for example NIGHT_A.");
        if (string.IsNullOrWhiteSpace(d.Name)) return Fail(400, "Give the shift a name.");
        if (!TimeOnly.TryParse(d.Start, out s) || !TimeOnly.TryParse(d.End, out e)) return Fail(400, "Use times like 22:00.");
        if (s == e) return Fail(400, "Start and end can't be the same time.");
        return null;
    }

    [HttpPost("shift-types"), Authorize(Policy = Policies.CanManageFoundationData)]
    public async Task<IActionResult> AddShiftType(ShiftTypeDto d, CancellationToken ct)
    {
        if (ValidateShift(d, out var s, out var e) is { } bad) return bad;
        if (await Db.ShiftTypes.AnyAsync(x => x.Code == d.Code, ct)) return Fail(409, $"A shift with code {d.Code} already exists.");
        var st = new ShiftType { Code = d.Code.Trim().ToUpperInvariant(), Name = d.Name.Trim(), StartTime = s, EndTime = e, SpansMidnight = e <= s, IsAllowanceEligible = d.IsAllowanceEligible, IsActive = true, CreatedBy = Me.Id.ToString() };
        Db.ShiftTypes.Add(st); await Db.SaveChangesAsync(ct); return Ok(new { st.Id });
    }

    [HttpPut("shift-types/{id:guid}"), Authorize(Policy = Policies.CanManageFoundationData)]
    public async Task<IActionResult> UpdateShiftType(Guid id, ShiftTypeDto d, CancellationToken ct)
    {
        var st = await Db.ShiftTypes.FirstOrDefaultAsync(x => x.Id == id, ct); if (st == null) return NotFound();
        if (ValidateShift(d, out var s, out var e) is { } bad) return bad;
        st.Name = d.Name.Trim(); st.StartTime = s; st.EndTime = e; st.SpansMidnight = e <= s; st.IsAllowanceEligible = d.IsAllowanceEligible; st.IsActive = d.IsActive; st.ModifiedAtUtc = Clock.UtcNow; st.ModifiedBy = Me.Id.ToString();
        await Db.SaveChangesAsync(ct); return Ok(new { st.Id });
    }

    [HttpGet("shift-rules")]
    public async Task<IActionResult> Rules(CancellationToken ct)
    {
        var today = Clock.Today;
        return Ok((await Db.ShiftRules.AsNoTracking().OrderBy(r => r.Code).ThenByDescending(r => r.EffectiveFrom).ToListAsync(ct)).Select(r => new { r.Id, r.Code, r.Name, type = r.RuleType.ToString(), scope = r.Scope.ToString(), r.Parameters, severity = r.Severity.ToString(), r.EffectiveFrom, r.EffectiveTo, current = r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today) }));
    }

    public sealed record RuleDto(string Code, string Parameters, RuleSeverity Severity, DateOnly EffectiveFrom);

    /// <summary>Rule changes are effective-dated: the current version is closed the day before, and a new version is added.</summary>
    [HttpPost("shift-rules"), Authorize(Policy = Policies.CanManageFoundationData)]
    public async Task<IActionResult> NewRuleVersion(RuleDto d, CancellationToken ct)
    {
        var cur = await Db.ShiftRules.Where(r => r.Code == d.Code && r.EffectiveTo == null).OrderByDescending(r => r.EffectiveFrom).FirstOrDefaultAsync(ct); if (cur == null) return NotFound();
        if (d.EffectiveFrom <= cur.EffectiveFrom) return Fail(400, $"The new version must start after {cur.EffectiveFrom:d MMM yyyy}.");
        try { using var doc = JsonDocument.Parse(d.Parameters); foreach (var p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind != JsonValueKind.Number || p.Value.GetDouble() <= 0) return Fail(400, $"Parameter '{p.Name}' must be a positive number."); }
        catch (JsonException) { return Fail(400, "Parameters must be valid JSON, for example {\"max\":6}."); }
        cur.EffectiveTo = d.EffectiveFrom.AddDays(-1); cur.ModifiedAtUtc = Clock.UtcNow; cur.ModifiedBy = Me.Id.ToString();
        var n = new ShiftRule { Code = cur.Code, Name = cur.Name, RuleType = cur.RuleType, Scope = cur.Scope, ScopeId = cur.ScopeId, Parameters = d.Parameters, Severity = d.Severity, EffectiveFrom = d.EffectiveFrom, CreatedBy = Me.Id.ToString() };
        Db.ShiftRules.Add(n); await Db.SaveChangesAsync(ct); return Ok(new { n.Id });
    }

    [HttpGet("rates")]
    public async Task<IActionResult> Rates(CancellationToken ct)
    {
        var st = await Db.ShiftTypes.AsNoTracking().ToDictionaryAsync(s => s.Id, ct); var bu = await Db.BusinessUnits.AsNoTracking().ToDictionaryAsync(b => b.Id, ct); var today = Clock.Today;
        return Ok((await Db.Rates.AsNoTracking().ToListAsync(ct)).OrderBy(r => st[r.ShiftTypeId].Code).ThenBy(r => r.EffectiveFrom).Select(r => new { r.Id, shiftTypeId = r.ShiftTypeId, shift = st[r.ShiftTypeId].Name, code = st[r.ShiftTypeId].Code, businessUnitId = r.BusinessUnitId, businessUnit = r.BusinessUnitId == null ? "All" : bu[r.BusinessUnitId.Value].Name, grade = r.Grade ?? "All", r.Amount, r.Currency, r.EffectiveFrom, r.EffectiveTo, current = r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today) }));
    }

    public sealed record RateDto(Guid ShiftTypeId, Guid? BusinessUnitId, string? Grade, decimal Amount, DateOnly EffectiveFrom);

    /// <summary>Changing a rate adds a new effective-dated row and closes the previous one. Existing allowance lines keep the rate they were calculated with, so a submitted batch is never rewritten.</summary>
    [HttpPost("rates"), Authorize(Policy = Policies.CanManageFoundationData)]
    public async Task<IActionResult> NewRate(RateDto d, CancellationToken ct)
    {
        if (d.Amount <= 0 || d.Amount > 100000) return Fail(400, "Enter a rate between ₹1 and ₹100,000.");
        if (!await Db.ShiftTypes.AnyAsync(s => s.Id == d.ShiftTypeId, ct)) return Fail(400, "Choose a shift type.");
        var grade = string.IsNullOrWhiteSpace(d.Grade) || d.Grade == "All" ? null : d.Grade;
        var prev = await Db.Rates.Where(r => r.ShiftTypeId == d.ShiftTypeId && r.BusinessUnitId == d.BusinessUnitId && r.Grade == grade && (r.EffectiveTo == null || r.EffectiveTo >= d.EffectiveFrom)).OrderByDescending(r => r.EffectiveFrom).FirstOrDefaultAsync(ct);
        if (prev != null)
        {
            if (d.EffectiveFrom <= prev.EffectiveFrom) return Fail(400, $"The new rate must start after the current one ({prev.EffectiveFrom:d MMM yyyy}).");
            prev.EffectiveTo = d.EffectiveFrom.AddDays(-1); prev.ModifiedAtUtc = Clock.UtcNow; prev.ModifiedBy = Me.Id.ToString();
        }
        var n = new AllowanceRate { ShiftTypeId = d.ShiftTypeId, BusinessUnitId = d.BusinessUnitId, Grade = grade, Amount = d.Amount, EffectiveFrom = d.EffectiveFrom, CreatedBy = Me.Id.ToString() };
        Db.Rates.Add(n); await Db.SaveChangesAsync(ct); return Ok(new { n.Id });
    }

    [HttpGet("holidays")]
    public async Task<IActionResult> Holidays([FromQuery] int? year, CancellationToken ct)
    {
        var rg = await Db.Regions.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
        return Ok((await Db.Holidays.AsNoTracking().Where(h => year == null || h.Date.Year == year).OrderBy(h => h.Date).ToListAsync(ct)).Select(h => new { h.Id, h.Date, h.Name, h.IsRestricted, region = rg[h.RegionId].Name, regionId = h.RegionId }));
    }

    public sealed record HolidayDto(Guid RegionId, DateOnly Date, string Name, bool IsRestricted);

    [HttpPost("holidays"), Authorize(Policy = Policies.CanManageFoundationData)]
    public async Task<IActionResult> AddHoliday(HolidayDto d, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) return Fail(400, "Name the holiday.");
        if (await Db.Holidays.AnyAsync(h => h.RegionId == d.RegionId && h.Date == d.Date, ct)) return Fail(409, "That region already has a holiday on this date.");
        var h = new HolidayCalendar { RegionId = d.RegionId, Date = d.Date, Name = d.Name.Trim(), IsRestricted = d.IsRestricted, CreatedBy = Me.Id.ToString() };
        Db.Holidays.Add(h); await Db.SaveChangesAsync(ct); return Ok(new { h.Id });
    }

    [HttpPut("holidays/{id:guid}"), Authorize(Policy = Policies.CanManageFoundationData)]
    public async Task<IActionResult> UpdateHoliday(Guid id, HolidayDto d, CancellationToken ct)
    {
        var h = await Db.Holidays.FirstOrDefaultAsync(x => x.Id == id, ct); if (h == null) return NotFound();
        h.Name = d.Name.Trim(); h.IsRestricted = d.IsRestricted; h.ModifiedAtUtc = Clock.UtcNow; h.ModifiedBy = Me.Id.ToString();
        await Db.SaveChangesAsync(ct); return Ok(new { h.Id });
    }
}
