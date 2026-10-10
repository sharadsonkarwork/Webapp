using Microsoft.EntityFrameworkCore;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;
using Rosterix.Infrastructure.Persistence;

namespace Rosterix.Infrastructure.Services;

public sealed class ApprovalException : Exception
{
    public int Status { get; }
    public ApprovalException(int status, string message) : base(message) { Status = status; }
}

public sealed record DecisionResult(ApprovalRequest Request, bool Completed, bool ViaDelegation);

/// <summary>Owns the approval chain: raising requests, assigning approvers, deciding steps (incl. delegation) and applying side effects.</summary>
public sealed class ApprovalService
{
    private readonly RosterixDbContext _db; private readonly INotifier _notify; private readonly IClock _clock;
    public ApprovalService(RosterixDbContext db, INotifier notify, IClock clock) { _db = db; _notify = notify; _clock = clock; }

    public static string PolicyFor(ApprovalType t) => t switch
    {
        ApprovalType.ProjectShiftTagging => Policies.CanApproveProjectTagging,
        ApprovalType.EmployeeTagging => Policies.CanApproveEmployeeTagging,
        ApprovalType.AllowanceCompensation => Policies.CanApproveCompensation,
        _ => Policies.CanDecideApprovals
    };

    public static DelegationScope? ScopeFor(ApprovalType t) => t switch
    {
        ApprovalType.ProjectShiftTagging => DelegationScope.ProjectApproval,
        ApprovalType.EmployeeTagging => DelegationScope.EmployeeTaggingApproval,
        ApprovalType.Exception => DelegationScope.ExceptionReview,
        _ => null // compensation (SEC-01), arrears and movements can never be delegated
    };

    public async Task<Employee> AssigneeAsync(Role role, Employee raiser, CancellationToken ct)
    {
        var bus = await _db.BusinessUnits.AsNoTracking().ToListAsync(ct);
        var emps = await _db.Employees.AsNoTracking().Where(e => e.IsActive).ToListAsync(ct);
        var region = bus.First(b => b.Id == raiser.BusinessUnitId).RegionId;
        switch (role)
        {
            case Role.LM:
                var mgr = emps.FirstOrDefault(e => e.Id == raiser.ManagerId && e.Roles.HasFlag(Role.LM));
                return mgr ?? emps.OrderBy(e => e.EmployeeNumber).First(e => e.BusinessUnitId == raiser.BusinessUnitId && e.Roles.HasFlag(Role.LM));
            case Role.HRBP or Role.RH:
                var inRegion = bus.Where(b => b.RegionId == region).Select(b => b.Id).ToHashSet();
                return emps.OrderBy(e => e.EmployeeNumber).First(e => inRegion.Contains(e.BusinessUnitId) && e.Roles.HasFlag(role));
            default:
                return emps.OrderBy(e => e.EmployeeNumber).First(e => e.Roles.HasFlag(Role.HRSS));
        }
    }

    public async Task<ApprovalRequest> RaiseAsync(ApprovalType type, Guid subjectId, string title, string summary, Employee raiser, Guid? projectId,
        decimal? value, DateOnly? from, DateOnly? to, Guid? subjectEmp, string? condition, CancellationToken ct)
    {
        var chain = await _db.Chains.AsNoTracking().Where(c => c.RequestType == type && c.Condition == condition).OrderBy(c => c.StepIndex).ToListAsync(ct);
        if (chain.Count == 0) throw new ApprovalException(500, $"No approval chain is defined for {type}.");
        var req = new ApprovalRequest { Type = type, SubjectId = subjectId, Title = title, Summary = summary, RaisedBy = raiser.Id, RaisedAtUtc = _clock.UtcNow, ProjectId = projectId, Value = value, FromDate = from, ToDate = to, SubjectEmployeeId = subjectEmp, CreatedBy = raiser.Id.ToString() };
        _db.ApprovalRequests.Add(req);
        foreach (var c in chain)
        {
            var a = await AssigneeAsync(c.RequiredRole, raiser, ct);
            _db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = req.Id, StepIndex = c.StepIndex, RequiredRole = c.RequiredRole, AssignedToEmployeeId = a.Id, CreatedBy = raiser.Id.ToString() });
        }
        var first = await AssigneeAsync(chain[0].RequiredRole, raiser, ct);
        _notify.Notify(first.Id, "ApprovalAssigned", $"New request: {title}", summary, NotificationSeverity.Info, "/approvals/" + req.Id);
        return req;
    }

    public async Task<DecisionResult> DecideAsync(Guid requestId, Employee actor, bool approve, string? comment, CancellationToken ct)
    {
        var req = await _db.ApprovalRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw new ApprovalException(404, "Request not found.");
        if (req.Status != ApprovalStatus.Pending) throw new ApprovalException(409, $"This request is already {req.Status.ToString().ToLowerInvariant()}.");
        var steps = await _db.ApprovalSteps.Where(s => s.ApprovalRequestId == requestId).OrderBy(s => s.StepIndex).ToListAsync(ct);
        var step = steps.First(s => s.StepIndex == req.CurrentStepIndex);

        // SEC-01: compensation is HRSS only, by the person's own role, never via delegation.
        if (req.Type == ApprovalType.AllowanceCompensation && (actor.Roles & Role.HRSS) == 0)
            throw new ApprovalException(403, "Only HRSS can approve allowance compensation.");
        if (!Policies.Satisfies(PolicyFor(req.Type), actor.Roles))
            throw new ApprovalException(403, "Your role can't decide this type of request.");

        Guid? viaDelegation = null;
        if (step.AssignedToEmployeeId != actor.Id)
        {
            var scope = ScopeFor(req.Type);
            var today = _clock.Today;
            var del = scope == null ? null : await _db.Delegations.FirstOrDefaultAsync(d => d.FromEmployeeId == step.AssignedToEmployeeId && d.ToEmployeeId == actor.Id && d.Scope == scope && !d.IsRevoked && d.ValidFrom <= today && d.ValidTo >= today, ct);
            if (del == null) throw new ApprovalException(403, "This step is assigned to someone else, and you don't hold a delegation for it.");
            viaDelegation = del.Id;
        }
        if ((actor.Roles & step.RequiredRole) == 0) throw new ApprovalException(403, $"This step needs the {step.RequiredRole} role.");
        if (!approve && string.IsNullOrWhiteSpace(comment)) throw new ApprovalException(400, "Give a reason when rejecting, so the requester knows what to fix.");

        step.ActedByEmployeeId = actor.Id; step.ActedViaDelegationId = viaDelegation; step.ActedAtUtc = _clock.UtcNow; step.Comment = comment;
        step.Decision = approve ? StepDecision.Approved : StepDecision.Rejected; step.ModifiedAtUtc = _clock.UtcNow; step.ModifiedBy = actor.Id.ToString();
        var raiser = req.RaisedBy;
        var completed = false;
        if (!approve)
        {
            req.Status = ApprovalStatus.Rejected; completed = true;
            await ApplyRejectionAsync(req, ct);
            _notify.Notify(raiser, "ApprovalDecided", $"Rejected: {req.Title}", $"{actor.FirstName} {actor.LastName}: {comment}", NotificationSeverity.Critical, "/approvals/" + req.Id);
        }
        else if (step.StepIndex >= steps.Max(s => s.StepIndex))
        {
            req.Status = ApprovalStatus.Approved; completed = true;
            await ApplyApprovalAsync(req, actor, ct);
            _notify.Notify(raiser, "ApprovalDecided", $"Approved: {req.Title}", "All steps are complete.", NotificationSeverity.Success, "/approvals/" + req.Id);
        }
        else
        {
            req.CurrentStepIndex = step.StepIndex + 1;
            var next = steps.First(s => s.StepIndex == req.CurrentStepIndex);
            _notify.Notify(next.AssignedToEmployeeId, "ApprovalAssigned", $"Your review is needed: {req.Title}", req.Summary, NotificationSeverity.Info, "/approvals/" + req.Id);
            _notify.Notify(raiser, "ApprovalDecided", $"Step approved: {req.Title}", $"{actor.FirstName} {actor.LastName} approved; it moves to {next.RequiredRole}.", NotificationSeverity.Info, "/approvals/" + req.Id);
        }
        req.ModifiedAtUtc = _clock.UtcNow; req.ModifiedBy = actor.Id.ToString();
        await _db.SaveChangesAsync(ct);
        return new DecisionResult(req, completed, viaDelegation != null);
    }

    private async Task ApplyApprovalAsync(ApprovalRequest req, Employee actor, CancellationToken ct)
    {
        switch (req.Type)
        {
            case ApprovalType.ProjectShiftTagging:
                var p = await _db.Projects.FirstAsync(x => x.Id == req.SubjectId, ct); p.Status = ProjectStatus.Active; p.IsShiftProject = true;
                foreach (var t in await _db.ProjectShiftTags.Where(t => t.ProjectId == p.Id && t.Status == ShiftTagStatus.PendingApproval).ToListAsync(ct)) t.Status = ShiftTagStatus.Approved;
                break;
            case ApprovalType.EmployeeTagging:
                foreach (var t in await _db.EmployeeTags.Where(t => t.ApprovalRequestId == req.Id).ToListAsync(ct)) t.Status = t.ValidFrom < _clock.Today ? TagStatus.Backtagged : TagStatus.Approved;
                break;
            case ApprovalType.AllowanceCompensation:
                var run = await _db.Runs.FirstAsync(r => r.Id == req.SubjectId, ct);
                run.Status = RunStatus.CompensationApproved; run.ApprovedBy = actor.Id; run.ApprovedAtUtc = _clock.UtcNow;
                foreach (var l in await _db.Lines.Where(l => l.AllowanceRunId == run.Id && l.Status == LineStatus.Calculated).ToListAsync(ct)) l.Status = LineStatus.Approved;
                break;
            case ApprovalType.Arrear:
                var a = await _db.Arrears.FirstAsync(x => x.Id == req.SubjectId, ct); a.Status = ArrearStatus.Approved; break;
            case ApprovalType.Exception:
                foreach (var x in await _db.Exceptions.Where(x => x.Id == req.SubjectId).ToListAsync(ct)) { x.Status = ExceptionStatus.Resolved; x.ResolvedBy = actor.Id; x.Resolution = "Accepted through approval."; }
                break;
        }
    }

    private async Task ApplyRejectionAsync(ApprovalRequest req, CancellationToken ct)
    {
        switch (req.Type)
        {
            case ApprovalType.ProjectShiftTagging:
                var p = await _db.Projects.FirstAsync(x => x.Id == req.SubjectId, ct); p.Status = ProjectStatus.Draft;
                foreach (var t in await _db.ProjectShiftTags.Where(t => t.ProjectId == p.Id && t.Status == ShiftTagStatus.PendingApproval).ToListAsync(ct)) t.Status = ShiftTagStatus.Rejected;
                break;
            case ApprovalType.EmployeeTagging:
                foreach (var t in await _db.EmployeeTags.Where(t => t.ApprovalRequestId == req.Id).ToListAsync(ct)) t.Status = TagStatus.Rejected; break;
            case ApprovalType.AllowanceCompensation:
                var run = await _db.Runs.FirstAsync(r => r.Id == req.SubjectId, ct); run.Status = RunStatus.Calculated; break;
            case ApprovalType.Arrear:
                var a = await _db.Arrears.FirstAsync(x => x.Id == req.SubjectId, ct); a.Status = ArrearStatus.Rejected; break;
        }
    }
}
