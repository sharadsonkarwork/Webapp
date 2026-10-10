using Microsoft.AspNetCore.Mvc;
using Rosterix.Application.Ai;
using Rosterix.Application.Data;
using Rosterix.Domain;
using Rosterix.Infrastructure.Services;

namespace Rosterix.Api.Controllers;

[Route("api/approvals")]
public class ApprovalsController : RxController
{
    private bool InMyQueue(Dataset ds, ApprovalRequest r, out Delegation? via)
    {
        via = null;
        var step = ds.ApprovalSteps.FirstOrDefault(s => s.ApprovalRequestId == r.Id && s.StepIndex == r.CurrentStepIndex);
        if (step == null) return false;
        if (step.AssignedToEmployeeId == Me.Id) return true;
        var scope = ApprovalService.ScopeFor(r.Type);
        if (scope == null) return false;
        via = ds.Delegations.FirstOrDefault(d => d.FromEmployeeId == step.AssignedToEmployeeId && d.ToEmployeeId == Me.Id && d.Scope == scope && d.IsActiveOn(ds.Today));
        return via != null && Me.Has(step.RequiredRole);
    }

    private object Row(Dataset ds, ApprovalRequest r, RiskItem? risk, Delegation? via)
    {
        var steps = ds.ApprovalSteps.Where(s => s.ApprovalRequestId == r.Id).OrderBy(s => s.StepIndex).ToList();
        var cur = steps.FirstOrDefault(s => s.StepIndex == r.CurrentStepIndex);
        return new
        {
            r.Id, type = r.Type.ToString(), r.Title, r.Summary, status = r.Status.ToString(), raisedBy = EmpRef(ds.Emp(r.RaisedBy)), r.RaisedAtUtc,
            ageDays = Math.Round((ds.Today.ToDateTime(TimeOnly.MinValue) - r.RaisedAtUtc).TotalDays, 0), r.Value, project = r.ProjectId == null ? null : ds.Proj(r.ProjectId.Value).Name,
            step = cur == null ? null : new { index = cur.StepIndex + 1, total = steps.Count, role = cur.RequiredRole.ToString(), assignedTo = ds.Emp(cur.AssignedToEmployeeId).FullName },
            viaDelegationFrom = via == null ? null : ds.Emp(via.FromEmployeeId).FullName,
            risk = risk == null ? null : new { score = risk.Score, band = risk.Band, headline = risk.Headline }
        };
    }

    /// <summary>My queue, risk-sorted by AI-UC-07 by default. Pass sort=oldest|type|value to override; the risk score never blocks anything.</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> Pending([FromQuery] string sort = "risk", [FromQuery] ApprovalType? type = null, CancellationToken ct = default)
    {
        var ds = await LoadAsync(ct);
        var risk = S<ApprovalRiskAi>().Score(ds, new RiskRequest()).Payload.Items.ToDictionary(i => i.RequestId);
        var mine = new List<(ApprovalRequest r, Delegation? via)>();
        foreach (var r in ds.ApprovalRequests.Where(r => r.Status == ApprovalStatus.Pending && (type == null || r.Type == type))) if (InMyQueue(ds, r, out var via)) mine.Add((r, via));
        IEnumerable<(ApprovalRequest r, Delegation? via)> ordered = sort switch
        {
            "oldest" => mine.OrderBy(x => x.r.RaisedAtUtc), "type" => mine.OrderBy(x => x.r.Type).ThenBy(x => x.r.RaisedAtUtc),
            "value" => mine.OrderByDescending(x => x.r.Value ?? 0), _ => mine.OrderByDescending(x => risk.TryGetValue(x.r.Id, out var ri) ? ri.Score : 0).ThenBy(x => x.r.RaisedAtUtc)
        };
        return Ok(new { sort, total = mine.Count, items = ordered.Select(x => Row(ds, x.r, risk.GetValueOrDefault(x.r.Id), x.via)) });
    }

    /// <summary>Requests I raised, any status.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        return Ok(ds.ApprovalRequests.Where(r => r.RaisedBy == Me.Id).OrderByDescending(r => r.RaisedAtUtc).Take(100).Select(r => Row(ds, r, null, null)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        var r = ds.ApprovalRequests.FirstOrDefault(x => x.Id == id); if (r == null) return NotFound();
        var steps = ds.ApprovalSteps.Where(s => s.ApprovalRequestId == id).OrderBy(s => s.StepIndex).ToList();
        InMyQueue(ds, r, out var via);
        var involved = r.RaisedBy == Me.Id || steps.Any(s => s.AssignedToEmployeeId == Me.Id) || via != null || Me.Has(Role.HRSS) ||
            ds.Delegations.Any(d => d.ToEmployeeId == Me.Id && d.IsActiveOn(ds.Today) && steps.Any(s => s.AssignedToEmployeeId == d.FromEmployeeId));
        if (!involved) return NotFound();
        var risk = S<ApprovalRiskAi>().Score(ds, new RiskRequest(new List<Guid> { id }));
        var ri = risk.Payload.Items.FirstOrDefault();
        var hist = ds.ApprovalRequests.Where(x => x.RaisedBy == r.RaisedBy && x.Id != id && x.Status != ApprovalStatus.Pending).ToList();
        object? context = null; var diff = new List<object>();
        switch (r.Type)
        {
            case ApprovalType.EmployeeTagging:
                var tags = ds.EmployeeTags.Where(t => t.ApprovalRequestId == id).ToList();
                var rows = tags.Select(t => new
                {
                    employee = EmpRef(ds.Emp(t.EmployeeId)), name = ds.Emp(t.EmployeeId).FullName, t.ValidFrom, t.ValidTo, status = t.Status.ToString(),
                    overlaps = ds.EmployeeTags.Where(o => o.EmployeeId == t.EmployeeId && o.Id != t.Id && o.ProjectId != t.ProjectId && o.Status is TagStatus.Approved or TagStatus.Backtagged && (o.ValidTo == null || o.ValidTo >= t.ValidFrom) && (t.ValidTo == null || o.ValidFrom <= t.ValidTo)).Select(o => $"{ds.Proj(o.ProjectId).Name} from {o.ValidFrom:d MMM yyyy}").ToList()
                }).ToList();
                context = new { kind = "tagging", project = r.ProjectId == null ? null : ds.Proj(r.ProjectId.Value).Name, rows, overlapCount = rows.Count(x => x.overlaps.Count > 0) };
                diff.AddRange(rows.Select(x => new { field = x.name, before = x.overlaps.Count > 0 ? "Already tagged: " + string.Join("; ", x.overlaps) : "Not tagged", after = $"Tagged from {x.ValidFrom:d MMM yyyy}" }));
                break;
            case ApprovalType.ProjectShiftTagging:
                var p = ds.Proj(r.SubjectId);
                context = new { kind = "project", p.Code, p.Name, businessUnit = ds.BU(p.BusinessUnitId).Name, owner = ds.Emp(p.OwnerPmId).FullName, p.StartDate, p.EndDate, p.CostCentre, shiftTypes = p.ShiftTypeIds.Select(s => ds.Shift(s).Name) };
                diff.Add(new { field = "Shift project", before = "No", after = "Yes" });
                diff.Add(new { field = "Shift types", before = "None", after = string.Join(", ", p.ShiftTypeIds.Select(s => ds.Shift(s).Name)) });
                break;
            case ApprovalType.AllowanceCompensation:
                var run = ds.Runs.First(x => x.Id == r.SubjectId); var lines = ds.Lines.Where(l => l.AllowanceRunId == run.Id).ToList();
                var anomalies = S<AnomalyDetectionAi>().Scan(ds, new AnomalyRequest()).Payload.Items.Where(a => a.ProjectId == run.ProjectId).ToList();
                context = new
                {
                    kind = "compensation", runId = run.Id, project = ds.Proj(run.ProjectId).Name, run.PeriodMonth, run.TotalAmount, run.LineCount, openExceptions = ds.Exceptions.Count(e => e.ProjectId == run.ProjectId && e.Status == ExceptionStatus.Open),
                    anomalies = anomalies.Select(a => new { a.Kind, a.EmployeeName, a.Description }), byShift = lines.GroupBy(l => ds.Shift(l.ShiftTypeId).Name).Select(g => new { shift = g.Key, count = g.Count(), amount = g.Sum(l => l.Amount) })
                };
                break;
            case ApprovalType.Arrear:
                var a1 = ds.Arrears.First(x => x.Id == r.SubjectId); var emp = ds.Emp(a1.EmployeeId);
                context = new { kind = "arrear", employee = EmpRef(emp), project = ds.Proj(a1.ProjectId).Name, a1.ForPeriodMonth, a1.Amount, a1.Reason, ageMonths = a1.AgeMonths(ds.Today), bucket = a1.AgeBucket(ds.Today), others = ds.Arrears.Count(x => x.EmployeeId == a1.EmployeeId && x.Id != a1.Id) };
                diff.Add(new { field = "Arrear", before = "Unpaid", after = $"₹{a1.Amount:N0} for {a1.ForPeriodMonth}" });
                break;
            case ApprovalType.Movement:
                var mv = ds.Movements.First(x => x.Id == r.SubjectId);
                var rip = await S<ConflictRippleAi>().SuggestAsync(new RippleRequest(mv.EmployeeId, mv.FromProjectId, mv.ToProjectId, mv.EffectiveFrom, mv.EffectiveTo), ct);
                context = new { kind = "movement", employee = EmpRef(ds.Emp(mv.EmployeeId)), from = mv.FromProjectId == null ? null : ds.Proj(mv.FromProjectId.Value).Name, to = ds.Proj(mv.ToProjectId).Name, mv.EffectiveFrom, mv.IsBackdated, mv.Reason, ripple = rip.Payload };
                diff.AddRange(rip.Payload.Rows.Select(x => new { field = $"{x.EntityType}: {x.Label}", before = x.Before, after = x.After }));
                break;
            case ApprovalType.Exception:
                context = new { kind = "exception", items = ds.Exceptions.Where(e => e.Id == r.SubjectId).Select(e => new { e.Description, severity = e.Severity.ToString(), category = e.Category.ToString() }) };
                break;
        }
        var canDecide = r.Status == ApprovalStatus.Pending && InMyQueue(ds, r, out _) && Rosterix.Application.Security.Policies.Satisfies(ApprovalService.PolicyFor(r.Type), Me.Roles) && (r.Type != ApprovalType.AllowanceCompensation || Me.Has(Role.HRSS));
        return Ok(new
        {
            request = Row(ds, r, ri, via), steps = steps.Select(s => new { s.StepIndex, role = s.RequiredRole.ToString(), assignedTo = ds.Emp(s.AssignedToEmployeeId).FullName, decision = s.Decision.ToString(), actedBy = ds.EmpOrNull(s.ActedByEmployeeId)?.FullName, viaDelegation = s.ActedViaDelegationId != null, s.Comment, s.ActedAtUtc, current = s.StepIndex == r.CurrentStepIndex && r.Status == ApprovalStatus.Pending }),
            risk = ri == null ? null : new { ri.Score, ri.Band, ri.Headline, ri.Reasons, ri.Features, confidence = risk.Confidence, confidenceBand = risk.ConfidenceBand.ToString(), suggestionId = risk.SuggestionId },
            requesterHistory = new { total = hist.Count, approved = hist.Count(x => x.Status == ApprovalStatus.Approved), rejected = hist.Count(x => x.Status == ApprovalStatus.Rejected), recent = hist.OrderByDescending(x => x.RaisedAtUtc).Take(5).Select(x => new { x.Title, status = x.Status.ToString(), x.RaisedAtUtc }) },
            context, diff, canDecide
        });
    }

    public sealed record DecideDto(bool Approve, string? Comment);

    [HttpPost("{id:guid}/decide")]
    public async Task<IActionResult> Decide(Guid id, DecideDto dto, CancellationToken ct)
    {
        var me = await Me.LoadAsync(ct);
        var res = await S<ApprovalService>().DecideAsync(id, me, dto.Approve, dto.Comment, ct);
        return Ok(new { status = res.Request.Status.ToString(), completed = res.Completed, viaDelegation = res.ViaDelegation });
    }

    public sealed record BulkDto(List<Guid> Ids, List<Guid> ReviewedIds, string? Comment);

    /// <summary>Bulk approve requires every request to have been opened and reviewed (mandatory review).</summary>
    [HttpPost("bulk-approve")]
    public async Task<IActionResult> BulkApprove(BulkDto dto, CancellationToken ct)
    {
        if (dto.Ids == null || dto.Ids.Count == 0) return Fail(400, "Select at least one request.");
        var unreviewed = dto.Ids.Except(dto.ReviewedIds ?? new()).ToList();
        if (unreviewed.Count > 0) return Fail(400, $"Review {unreviewed.Count} more request(s) before approving in bulk.", unreviewed);
        var me = await Me.LoadAsync(ct); var svc = S<ApprovalService>(); var results = new List<object>();
        foreach (var id in dto.Ids.Distinct())
        {
            try { var r = await svc.DecideAsync(id, me, true, dto.Comment, ct); results.Add(new { id, ok = true, status = r.Request.Status.ToString() }); }
            catch (ApprovalException ex) { results.Add(new { id, ok = false, error = ex.Message, status = ex.Status }); Db.ChangeTracker.Clear(); }
        }
        return Ok(new { results });
    }
}
