using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rosterix.Application.Ai;
using Rosterix.Application.Allowance;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;
using Rosterix.Infrastructure.Services;

namespace Rosterix.Api.Controllers;

[Route("api/allowance-runs")]
public class AllowanceController : RxController
{
    private static int Prio(RunStatus s) => s switch { RunStatus.PendingCompensation => 0, RunStatus.Calculated => 1, RunStatus.CompensationApproved => 2, _ => 3 };

    private static object Item(Dataset ds, AllowanceRun r) => new
    {
        r.Id, r.ProjectId, project = ds.Proj(r.ProjectId).Name, code = ds.Proj(r.ProjectId).Code, month = r.PeriodMonth, status = r.Status.ToString(), r.TotalAmount, r.LineCount, r.ExceptionCount,
        calculatedBy = ds.Emp(r.CalculatedBy).FullName, r.CreatedAtUtc, r.ApprovalRequestId, approvedBy = ds.EmpOrNull(r.ApprovedBy)?.FullName, r.ApprovedAtUtc,
        openExceptions = ds.Exceptions.Count(e => e.ProjectId == r.ProjectId && e.Status == ExceptionStatus.Open && e.AllowanceLineId != null && ds.Lines.Any(l => l.Id == e.AllowanceLineId && l.AllowanceRunId == r.Id)),
        flaggedLines = ds.Lines.Count(l => l.AllowanceRunId == r.Id && l.Status == LineStatus.Flagged)
    };

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var vis = VisibleProjects(ds);
        return Ok(ds.Runs.Where(r => vis.Contains(r.ProjectId)).OrderBy(r => Prio(r.Status)).ThenByDescending(r => r.PeriodMonth).ThenBy(r => ds.Proj(r.ProjectId).Code).Select(r => Item(ds, r)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var r = ds.Runs.FirstOrDefault(x => x.Id == id); if (r == null || !VisibleProjects(ds).Contains(r.ProjectId)) return NotFound();
        var lines = ds.Lines.Where(l => l.AllowanceRunId == id).ToList();
        var byShift = lines.GroupBy(l => ds.Shift(l.ShiftTypeId).Name).Select(g => new { shift = g.Key, count = g.Count(), amount = g.Sum(l => l.Amount) }).OrderByDescending(x => x.amount);
        var byEmp = lines.GroupBy(l => l.EmployeeId).Select(g => new { employee = EmpRef(ds.Emp(g.Key)), count = g.Count(), amount = g.Sum(l => l.Amount), flagged = g.Count(l => l.Status == LineStatus.Flagged) }).OrderByDescending(x => x.amount).ToList();
        var exc = ds.Exceptions.Where(e => e.ProjectId == r.ProjectId && e.Status != ExceptionStatus.Resolved).Take(20).Select(e => new { e.Id, category = e.Category.ToString(), severity = e.Severity.ToString(), e.Description, status = e.Status.ToString() });
        var p = ds.Proj(r.ProjectId);
        return Ok(new { run = Item(ds, r), byShift, employees = byEmp, exceptions = exc, canProcess = r.Status == RunStatus.Calculated && (Me.Has(Role.HRSS) || p.OwnerPmId == Me.Id), canApprove = Me.Has(Role.HRSS) && r.Status is RunStatus.Calculated or RunStatus.PendingCompensation, canRecalculate = r.Status == RunStatus.Calculated && (Me.Has(Role.HRSS) || p.OwnerPmId == Me.Id) });
    }

    [HttpGet("{id:guid}/lines")]
    public async Task<IActionResult> Lines(Guid id, [FromQuery] Guid? employeeId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var ds = await LoadAsync(ct); var r = ds.Runs.FirstOrDefault(x => x.Id == id); if (r == null || !VisibleProjects(ds).Contains(r.ProjectId)) return NotFound();
        var q = ds.Lines.Where(l => l.AllowanceRunId == id && (employeeId == null || l.EmployeeId == employeeId)).OrderBy(l => ds.Emp(l.EmployeeId).FirstName).ThenBy(l => l.WorkDate).ToList();
        return Ok(new { total = q.Count, rows = q.Skip((Math.Max(1, page) - 1) * pageSize).Take(Math.Clamp(pageSize, 1, 200)).Select(l => new { l.Id, employee = EmpRef(ds.Emp(l.EmployeeId)), l.WorkDate, shift = ds.Shift(l.ShiftTypeId).Name, l.RateApplied, l.Amount, status = l.Status.ToString(), trace = l.CalculationTrace }) });
    }

    public sealed record CalcDto(Guid ProjectId, string Month);

    /// <summary>Calculates a project-month. Allowance lines are system-computed and read-only afterwards (SEC-02): the only way to change them is to recalculate an unprocessed run.</summary>
    [HttpPost("calculate"), Authorize(Policy = Policies.CanReviewExceptions)]
    public async Task<IActionResult> Calculate(CalcDto d, CancellationToken ct)
    {
        if (!ValidMonth(d.Month)) return Fail(400, "Use a month like 2026-09.");
        var ds = await LoadAsync(ct); var p = ds.Projects.FirstOrDefault(x => x.Id == d.ProjectId); if (p == null) return NotFound();
        if (!(Me.Has(Role.HRSS) || p.OwnerPmId == Me.Id)) return Fail(403, "Only the project's PM or HRSS can calculate its allowance.");
        var roster = ds.FindRoster(p.Id, d.Month);
        if (roster == null || roster.Status is not (RosterStatus.Published or RosterStatus.Locked)) return Fail(400, $"Publish the {d.Month} roster before calculating its allowance.");
        var existing = await Db.Runs.FirstOrDefaultAsync(r => r.ProjectId == p.Id && r.PeriodMonth == d.Month, ct);
        if (existing != null && existing.Status != RunStatus.Calculated) return Fail(409, $"The {d.Month} run is already {existing.Status}; it can't be recalculated.");
        var res = S<AllowanceCalculator>().Calculate(ds, p.Id, d.Month);
        if (existing != null)
        {
            var oldLineIds = await Db.Lines.Where(l => l.AllowanceRunId == existing.Id).Select(l => l.Id).ToListAsync(ct);
            Db.Exceptions.RemoveRange(await Db.Exceptions.Where(e => e.AllowanceLineId != null && oldLineIds.Contains(e.AllowanceLineId.Value)).ToListAsync(ct));
            Db.Lines.RemoveRange(await Db.Lines.Where(l => l.AllowanceRunId == existing.Id).ToListAsync(ct));
            Db.Runs.Remove(existing); await Db.SaveChangesAsync(ct);
        }
        var run = new AllowanceRun { ProjectId = p.Id, PeriodMonth = d.Month, Status = RunStatus.Calculated, CalculatedBy = Me.Id, TotalAmount = res.Total, LineCount = res.Lines.Count, ExceptionCount = res.Exceptions.Count, CreatedBy = Me.Id.ToString() };
        Db.Runs.Add(run);
        foreach (var l in res.Lines) Db.Lines.Add(new AllowanceLine { AllowanceRunId = run.Id, EmployeeId = l.EmployeeId, WorkDate = l.WorkDate, ShiftTypeId = l.ShiftTypeId, RateApplied = l.Rate, Amount = l.Amount, CalculationTrace = l.Trace, Status = LineStatus.Calculated, CreatedBy = Me.Id.ToString() });
        foreach (var e in res.Exceptions) Db.Exceptions.Add(new ExceptionItem { ProjectId = p.Id, EmployeeId = e.EmployeeId, Category = e.Category, RuleCode = e.RuleCode, Severity = e.Severity, Description = e.Description, Status = ExceptionStatus.Open, AssignedTo = p.OwnerPmId, CreatedBy = Me.Id.ToString() });
        await Db.SaveChangesAsync(ct);
        return Ok(new { run.Id, run.LineCount, run.TotalAmount, run.ExceptionCount });
    }

    /// <summary>Process = send the calculated run for compensation approval. Only HRSS can approve it (SEC-01).</summary>
    [HttpPost("{id:guid}/process"), Authorize(Policy = Policies.CanReviewExceptions)]
    public async Task<IActionResult> Process(Guid id, CancellationToken ct)
    {
        var run = await Db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct); if (run == null) return NotFound();
        var p = await Db.Projects.FirstAsync(x => x.Id == run.ProjectId, ct);
        if (!(Me.Has(Role.HRSS) || p.OwnerPmId == Me.Id)) return Fail(403, "Only the project's PM or HRSS can process this run.");
        if (run.Status != RunStatus.Calculated) return Fail(409, $"This run is {run.Status}; only a calculated run can be processed.");
        var me = await Me.LoadAsync(ct);
        var month = ShiftMath.MonthStart(run.PeriodMonth);
        var req = await S<ApprovalService>().RaiseAsync(ApprovalType.AllowanceCompensation, run.Id, $"Compensation for {p.Name}, {month:MMMM yyyy}", $"Allowance run of ₹{run.TotalAmount:N0} across {run.LineCount} lines needs HRSS approval.", me, p.Id, run.TotalAmount, month, ShiftMath.MonthEnd(run.PeriodMonth), null, null, ct);
        run.Status = RunStatus.PendingCompensation; run.ApprovalRequestId = req.Id;
        await Db.SaveChangesAsync(ct);
        return Ok(new { requestId = req.Id });
    }

    /// <summary>SEC-01: only HRSS (by their own role, not by delegation) may approve compensation. PM, LM, RH and every other role get 403 from the policy.</summary>
    [HttpPost("{id:guid}/approve-compensation"), Authorize(Policy = Policies.CanApproveCompensation)]
    public async Task<IActionResult> ApproveCompensation(Guid id, CancellationToken ct)
    {
        var run = await Db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct); if (run == null) return NotFound();
        if (run.Status == RunStatus.CompensationApproved) return Ok(new { status = run.Status.ToString(), alreadyApproved = true });
        if (run.Status == RunStatus.Submitted) return Fail(409, "This run has already gone to payroll.");
        var me = await Me.LoadAsync(ct);
        if (run.ApprovalRequestId != null && await Db.ApprovalRequests.AnyAsync(r => r.Id == run.ApprovalRequestId && r.Status == ApprovalStatus.Pending, ct))
        {
            var step = await Db.ApprovalSteps.FirstAsync(s => s.ApprovalRequestId == run.ApprovalRequestId && s.Decision == StepDecision.Pending, ct);
            if (step.AssignedToEmployeeId != me.Id) { step.AssignedToEmployeeId = me.Id; await Db.SaveChangesAsync(ct); } // any HRSS specialist may take the HRSS step
            await S<ApprovalService>().DecideAsync(run.ApprovalRequestId.Value, me, true, "Approved from the allowance run", ct);
        }
        else
        {
            run.Status = RunStatus.CompensationApproved; run.ApprovedBy = me.Id; run.ApprovedAtUtc = Clock.UtcNow;
            foreach (var l in await Db.Lines.Where(l => l.AllowanceRunId == id && l.Status == LineStatus.Calculated).ToListAsync(ct)) l.Status = LineStatus.Approved;
            await Db.SaveChangesAsync(ct);
        }
        return Ok(new { status = RunStatus.CompensationApproved.ToString() });
    }
}

[Route("api/exceptions"), Authorize(Policy = Policies.CanReviewExceptions)]
public class ExceptionsController : RxController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ExceptionCategory? category, [FromQuery] Guid? projectId, [FromQuery] ExceptionSeverity? severity, [FromQuery] ExceptionStatus? status, [FromQuery] bool assignedToMe = false, CancellationToken ct = default)
    {
        var ds = await LoadAsync(ct); var vis = VisibleProjects(ds);
        var q = ds.Exceptions.Where(e => (e.ProjectId != null ? vis.Contains(e.ProjectId.Value) : e.AssignedTo == Me.Id) && (category == null || e.Category == category) && (projectId == null || e.ProjectId == projectId)
            && (severity == null || e.Severity == severity) && (status == null || e.Status == status) && (!assignedToMe || e.AssignedTo == Me.Id));
        return Ok(q.OrderBy(e => e.Status == ExceptionStatus.Resolved).ThenBy(e => e.Severity).ThenBy(e => e.Category).Select(e => new
        {
            e.Id, category = e.Category.ToString(), e.RuleCode, severity = e.Severity.ToString(), e.Description, e.AiConfidence, status = e.Status.ToString(),
            project = e.ProjectId == null ? null : ds.Proj(e.ProjectId.Value).Name, projectId = e.ProjectId, employee = ds.EmpOrNull(e.EmployeeId)?.FullName, assignedTo = ds.EmpOrNull(e.AssignedTo)?.FullName,
            resolvedBy = ds.EmpOrNull(e.ResolvedBy)?.FullName, e.Resolution, e.AllowanceLineId
        }));
    }

    public sealed record ResolveDto(string Resolution);
    public sealed record BulkDto(List<Guid> Ids);

    [HttpPost("{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        var e = await Db.Exceptions.FirstOrDefaultAsync(x => x.Id == id, ct); if (e == null) return NotFound();
        if (e.Status == ExceptionStatus.Resolved) return Fail(409, "This exception is already resolved.");
        e.Status = ExceptionStatus.Acknowledged; e.ModifiedAtUtc = Clock.UtcNow; e.ModifiedBy = Me.Id.ToString(); await Db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, ResolveDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Resolution)) return Fail(400, "Say how it was resolved so the next reviewer can trust it.");
        var e = await Db.Exceptions.FirstOrDefaultAsync(x => x.Id == id, ct); if (e == null) return NotFound();
        e.Status = ExceptionStatus.Resolved; e.Resolution = dto.Resolution.Trim(); e.ResolvedBy = Me.Id; e.ModifiedAtUtc = Clock.UtcNow; e.ModifiedBy = Me.Id.ToString(); await Db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpPost("bulk-acknowledge")]
    public async Task<IActionResult> BulkAcknowledge(BulkDto dto, CancellationToken ct)
    {
        if (dto.Ids == null || dto.Ids.Count == 0) return Fail(400, "Select at least one exception.");
        var items = await Db.Exceptions.Where(e => dto.Ids.Contains(e.Id) && e.Status == ExceptionStatus.Open).ToListAsync(ct);
        foreach (var e in items) { e.Status = ExceptionStatus.Acknowledged; e.ModifiedAtUtc = Clock.UtcNow; e.ModifiedBy = Me.Id.ToString(); }
        await Db.SaveChangesAsync(ct); return Ok(new { acknowledged = items.Count });
    }
}
