using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rosterix.Api.Auth;
using Rosterix.Application.Ai;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;

namespace Rosterix.Api.Controllers;

/// <summary>
/// The nine advisory AI use cases. Every GET here is read-only; the two POSTs (validate, ripple) are previews and carry [ReadOnlyOperation].
/// Persistence only happens through POST /api/ai/feedback and the ordinary human-driven endpoints (e.g. accept-draft).
/// </summary>
[Route("api/ai")]
public class AiController : RxController
{
    private async Task<bool> CanSee(Guid projectId, CancellationToken ct) { var ds = await LoadAsync(ct); return VisibleProjects(ds).Contains(projectId); }

    /// <summary>AI-UC-01 Smart Project Setup.</summary>
    [HttpGet("project-setup"), Authorize(Policy = Policies.CanManageProjects)]
    public async Task<IActionResult> ProjectSetup([FromQuery] Guid businessUnitId, CancellationToken ct) => Ok(await S<ProjectSetupAi>().SuggestAsync(new ProjectSetupRequest(businessUnitId, Me.Id), ct));

    /// <summary>AI-UC-02 Employee Tagging: top candidates with reasons.</summary>
    [HttpGet("employee-tagging"), Authorize(Policy = Policies.CanManageProjects)]
    public async Task<IActionResult> Tagging([FromQuery] Guid projectId, [FromQuery] DateOnly from, [FromQuery] DateOnly? to, [FromQuery] int limit = 10, CancellationToken ct = default)
        => !await CanSee(projectId, ct) ? NotFound() : Ok(await S<EmployeeTaggingAi>().SuggestAsync(new TaggingRequest(projectId, from, to, Math.Clamp(limit, 1, 25)), ct));

    /// <summary>AI-UC-03 Roster Generation. Returns a suggestion only; accept it with POST /api/rosters/accept-draft.</summary>
    [HttpGet("roster-draft"), Authorize(Policy = Policies.CanBuildRoster)]
    public async Task<IActionResult> RosterDraft([FromQuery] Guid projectId, [FromQuery] string month, CancellationToken ct)
        => !ValidMonth(month) ? Fail(400, "Use a month like 2026-11.") : !await CanSee(projectId, ct) ? NotFound() : Ok(await S<RosterGenerationAi>().SuggestAsync(new RosterDraftRequest(projectId, month), ct));

    /// <summary>AI-UC-04 Constraint Validation: the shared rule engine plus near-misses. A preview, so no audit row.</summary>
    [HttpPost("validate"), ReadOnlyOperation, Authorize(Policy = Policies.CanBuildRoster)]
    public async Task<IActionResult> Validate(ValidationRequest r, CancellationToken ct)
        => !await CanSee(r.ProjectId, ct) ? NotFound() : Ok(await S<ConstraintValidationAi>().SuggestAsync(r, ct));

    /// <summary>AI-UC-05 Cost &amp; Allowance Prediction for a project-month.</summary>
    [HttpGet("cost-forecast"), Authorize(Policy = Policies.CanManageProjects)]
    public async Task<IActionResult> Forecast([FromQuery] Guid projectId, [FromQuery] string month, CancellationToken ct)
        => !ValidMonth(month) ? Fail(400, "Use a month like 2026-10.") : !await CanSee(projectId, ct) ? NotFound() : Ok(await S<CostPredictionAi>().SuggestAsync(new ForecastRequest(projectId, month), ct));

    /// <summary>AI-UC-06 Conflict / Ripple Preview before a movement or backtag commits.</summary>
    [HttpPost("ripple"), ReadOnlyOperation, Authorize(Policy = Policies.IsHrss)]
    public async Task<IActionResult> Ripple(RippleRequest r, CancellationToken ct) => Ok(await S<ConflictRippleAi>().SuggestAsync(r, ct));

    /// <summary>AI-UC-07 Approval Risk for everything pending in my queue.</summary>
    [HttpGet("approval-risk"), Authorize(Policy = Policies.CanDecideApprovals)]
    public async Task<IActionResult> Risk(CancellationToken ct) => Ok(await S<ApprovalRiskAi>().SuggestAsync(new RiskRequest(), ct));

    /// <summary>AI-UC-08 Preference Learning for one employee (editable on the employee record).</summary>
    [HttpGet("preferences/{employeeId:guid}")]
    public async Task<IActionResult> Preferences(Guid employeeId, CancellationToken ct)
        => employeeId != Me.Id && !Me.Has(Role.PM | Role.LM | Role.HRSS) ? Forbid() : Ok(await S<PreferenceLearningAi>().SuggestAsync(new PreferenceRequest(employeeId), ct));

    /// <summary>AI-UC-09 Anomaly Detection: org-wide for HRSS/HRBP, scoped to my projects for PM/LM.</summary>
    [HttpGet("anomalies"), Authorize(Policy = Policies.CanReviewExceptions)]
    public async Task<IActionResult> Anomalies([FromQuery] bool includeDismissed = false, CancellationToken ct = default)
    {
        var ds = await LoadAsync(ct); var res = S<AnomalyDetectionAi>().Scan(ds, new AnomalyRequest(includeDismissed));
        if (Me.Has(Role.HRSS)) return Ok(res);
        var vis = VisibleProjects(ds);
        var items = res.Payload.Items.Where(i => i.ProjectId != null && vis.Contains(i.ProjectId.Value)).ToList();
        return Ok(new AiSuggestion<AnomalyPayload> { UseCaseCode = res.UseCaseCode, SuggestionId = res.SuggestionId, Confidence = res.Confidence, Summary = $"{items.Count} anomalies in your projects.", Impact = res.Impact, Reasons = res.Reasons, GeneratedAtUtc = res.GeneratedAtUtc, Payload = new(items, items.Count(i => i.Kind == "Statistical outlier"), items.Count(i => i.Kind == "Duplicate claim"), items.Count(i => i.Kind == "Rate mismatch"), items.Count(i => i.Kind == "Impossible hours")) });
    }

    public sealed record FeedbackDto(string UseCaseCode, string ContextType, string ContextId, AiAction Action, string? Reason, double? Confidence, string? Summary);

    /// <summary>Records that a person accepted, modified or dismissed a suggestion. This is the only AI-related write besides accepting a draft.</summary>
    [HttpPost("feedback")]
    public async Task<IActionResult> Feedback(FeedbackDto d, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(d.UseCaseCode) || !d.UseCaseCode.StartsWith("AI-UC-")) return Fail(400, "Unknown AI use case.");
        if (d.Action == AiAction.Dismiss && d.UseCaseCode == "AI-UC-09" && string.IsNullOrWhiteSpace(d.Reason)) return Fail(400, "Say why you're dismissing this, so future scans learn what is normal.");
        var rec = new AiSuggestionRecord { UseCaseCode = d.UseCaseCode, ContextType = d.ContextType, ContextId = d.ContextId, Confidence = d.Confidence ?? 0, Payload = System.Text.Json.JsonSerializer.Serialize(new { d.Summary }), Status = d.Action switch { AiAction.Accept => AiStatus.Accepted, AiAction.Modify => AiStatus.Modified, _ => AiStatus.Dismissed }, CreatedBy = Me.Id.ToString() };
        Db.AiSuggestions.Add(rec);
        Db.AiFeedback.Add(new AiFeedback { AiSuggestionId = rec.Id, UseCaseCode = d.UseCaseCode, ContextId = d.ContextId, ActorEmployeeId = Me.Id, Action = d.Action, Reason = d.Reason?[..Math.Min(500, d.Reason.Length)], CreatedBy = Me.Id.ToString() });
        await Db.SaveChangesAsync(ct);
        return Ok(new { id = rec.Id });
    }
}
