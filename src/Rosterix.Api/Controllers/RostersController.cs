using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rosterix.Api.Auth;
using Rosterix.Application.Ai;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Api.Controllers;

[Route("api/rosters")]
public class RostersController : RxController
{
    private bool CanEdit(Dataset ds, Project p, Roster? r) =>
        (r == null || r.Status != RosterStatus.Locked) && Policies.Satisfies(Policies.CanBuildRoster, Me.Roles) &&
        (Me.Has(Role.HRSS) || p.OwnerPmId == Me.Id || (Me.Has(Role.LM) && VisibleProjects(ds).Contains(p.Id)));

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? projectId, [FromQuery] string? month, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var vis = VisibleProjects(ds);
        var q = ds.Rosters.Where(r => vis.Contains(r.ProjectId) && (projectId == null || r.ProjectId == projectId) && (month == null || r.PeriodMonth == month));
        return Ok(q.OrderByDescending(r => r.PeriodMonth).ThenBy(r => ds.Proj(r.ProjectId).Code).Select(r => new { r.Id, r.ProjectId, project = ds.Proj(r.ProjectId).Name, code = ds.Proj(r.ProjectId).Code, month = r.PeriodMonth, status = r.Status.ToString(), source = r.Source.ToString(), r.PublishedAtUtc, entryCount = ds.RosterEntries.Count(e => e.RosterId == r.Id) }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var r = ds.Rosters.FirstOrDefault(x => x.Id == id);
        if (r == null || !VisibleProjects(ds).Contains(r.ProjectId)) return NotFound();
        var p = ds.Proj(r.ProjectId); var from = ShiftMath.MonthStart(r.PeriodMonth); var to = ShiftMath.MonthEnd(r.PeriodMonth);
        var entries = ds.EntriesFor(id).ToList();
        var tagged = ds.EmployeeTags.Where(t => t.ProjectId == p.Id && t.Status is TagStatus.Approved or TagStatus.Backtagged && t.ValidFrom <= to && (t.ValidTo == null || t.ValidTo >= from)).Select(t => t.EmployeeId);
        var empIds = tagged.Concat(entries.Select(e => e.EmployeeId)).Distinct().ToList();
        var region = ds.BU(p.BusinessUnitId).RegionId;
        var hol = ds.Holidays.Where(h => h.RegionId == region && h.Date >= from && h.Date <= to).ToDictionary(h => h.Date);
        var days = Enumerable.Range(0, to.Day).Select(i => from.AddDays(i)).Select(d => new { date = d, dow = d.DayOfWeek.ToString()[..3], holiday = hol.TryGetValue(d, out var h) ? h.Name : null, restricted = hol.TryGetValue(d, out var hh) && hh.IsRestricted });
        var v = S<ConstraintValidationAi>().Evaluate(ds, new ValidationRequest(p.Id, r.PeriodMonth, new(), empIds));
        var canEdit = CanEdit(ds, p, r);
        return Ok(new
        {
            roster = new { r.Id, r.ProjectId, month = r.PeriodMonth, status = r.Status.ToString(), source = r.Source.ToString(), r.PublishedAtUtc, project = new { p.Code, p.Name, p.StartDate, p.EndDate, shiftTypeIds = p.ShiftTypeIds } },
            employees = empIds.Select(ds.Emp).OrderBy(e => e.FirstName).Select(e => new { e.Id, name = e.FullName, number = e.EmployeeNumber, e.Grade }),
            days, entries = entries.Select(e => new { e = e.EmployeeId, d = e.WorkDate, s = e.ShiftTypeId, t = e.EntryType.ToString(), ai = e.IsAiSuggested }),
            leaves = ds.Leaves.Where(l => empIds.Contains(l.EmployeeId) && l.Date >= from && l.Date <= to).Select(l => new { e = l.EmployeeId, d = l.Date }),
            shiftTypes = ds.ShiftTypes.Where(s => s.IsActive).Select(s => new { s.Id, s.Code, s.Name, start = s.StartTime.ToString("HH:mm"), end = s.EndTime.ToString("HH:mm"), hours = s.DurationHours, eligible = s.IsAllowanceEligible, projectShift = p.ShiftTypeIds.Contains(s.Id) }),
            validation = new { v.Summary, v.Payload.Violations, v.Payload.NearMisses, v.Payload.HasBlock },
            canEdit, canPublish = canEdit && r.Status is RosterStatus.Draft or RosterStatus.PendingPublish
        });
    }

    public sealed record CreateDto(Guid ProjectId, string Month);

    [HttpPost, Authorize(Policy = Policies.CanBuildRoster)]
    public async Task<IActionResult> Create(CreateDto d, CancellationToken ct)
    {
        if (!ValidMonth(d.Month)) return Fail(400, "Use a month like 2026-11.");
        var ds = await LoadAsync(ct); var p = ds.Projects.FirstOrDefault(x => x.Id == d.ProjectId); if (p == null) return NotFound();
        if (!CanEdit(ds, p, null)) return Fail(403, "You can build rosters only for projects you own or manage.");
        if (ds.FindRoster(p.Id, d.Month) != null) return Fail(409, "A roster for this project and month already exists.");
        var r = new Roster { ProjectId = p.Id, PeriodMonth = d.Month, Status = RosterStatus.Draft, Source = RosterSource.Manual, CreatedBy = Me.Id.ToString() };
        Db.Rosters.Add(r); await Db.SaveChangesAsync(ct); return Ok(new { r.Id });
    }

    public sealed record DraftEntryDto(Guid EmployeeId, DateOnly WorkDate, Guid? ShiftTypeId, EntryType EntryType);
    public sealed record AcceptDraftDto(Guid ProjectId, string Month, List<DraftEntryDto> Entries, Guid? AiSuggestionId, double? Confidence);

    /// <summary>The explicit human step that turns an AI-UC-03 suggestion into a Draft roster. It never publishes.</summary>
    [HttpPost("accept-draft"), Authorize(Policy = Policies.CanBuildRoster)]
    public async Task<IActionResult> AcceptDraft(AcceptDraftDto d, CancellationToken ct)
    {
        if (!ValidMonth(d.Month)) return Fail(400, "Use a month like 2026-11.");
        var ds = await LoadAsync(ct); var p = ds.Projects.FirstOrDefault(x => x.Id == d.ProjectId); if (p == null) return NotFound();
        if (!CanEdit(ds, p, null)) return Fail(403, "You can build rosters only for projects you own or manage.");
        var existing = await Db.Rosters.FirstOrDefaultAsync(r => r.ProjectId == p.Id && r.PeriodMonth == d.Month, ct);
        if (existing != null && existing.Status != RosterStatus.Draft) return Fail(409, $"The {d.Month} roster is {existing.Status.ToString().ToLowerInvariant()}; a draft can't replace it.");
        var from = ShiftMath.MonthStart(d.Month); var to = ShiftMath.MonthEnd(d.Month);
        if (d.Entries.Any(e => e.WorkDate < from || e.WorkDate > to)) return Fail(400, "Some draft entries fall outside the month.");
        var sug = new AiSuggestionRecord { UseCaseCode = "AI-UC-03", ContextType = "Roster", ContextId = $"{p.Id}:{d.Month}", Confidence = d.Confidence ?? 0, Status = AiStatus.Accepted, Payload = $"{{\"entries\":{d.Entries.Count}}}", CreatedBy = Me.Id.ToString() };
        Db.AiSuggestions.Add(sug);
        Db.AiFeedback.Add(new AiFeedback { AiSuggestionId = sug.Id, UseCaseCode = "AI-UC-03", ContextId = sug.ContextId, ActorEmployeeId = Me.Id, Action = AiAction.Accept });
        if (existing == null) { existing = new Roster { ProjectId = p.Id, PeriodMonth = d.Month, CreatedBy = Me.Id.ToString() }; Db.Rosters.Add(existing); }
        else Db.RosterEntries.RemoveRange(await Db.RosterEntries.Where(e => e.RosterId == existing.Id).ToListAsync(ct));
        existing.Status = RosterStatus.Draft; existing.Source = RosterSource.AiDraft;
        foreach (var e in d.Entries) Db.RosterEntries.Add(new RosterEntry { RosterId = existing.Id, EmployeeId = e.EmployeeId, WorkDate = e.WorkDate, ShiftTypeId = e.EntryType == EntryType.Shift ? e.ShiftTypeId : null, EntryType = e.EntryType, IsAiSuggested = true, AiSuggestionId = sug.Id, CreatedBy = Me.Id.ToString() });
        await Db.SaveChangesAsync(ct);
        return Ok(new { existing.Id, entries = d.Entries.Count });
    }

    public sealed record EditDto(List<DraftEntryDto> Edits, List<string>? Acknowledged, string? Reason);

    private static string Describe(Dataset ds, EntryType t, Guid? shift) => t == EntryType.Shift && shift != null ? ds.Shift(shift.Value).Name : t switch { EntryType.WeeklyOff => "Weekly off", EntryType.Leave => "Leave", EntryType.Holiday => "Holiday", _ => "Shift" };

    private async Task<(IActionResult? error, int applied, List<ValidationItem> warns)> ApplyAsync(Dataset ds, Roster roster, Project p, List<DraftEntryDto> edits, IEnumerable<string>? acked, string? reason, CancellationToken ct)
    {
        var from = ShiftMath.MonthStart(roster.PeriodMonth); var to = ShiftMath.MonthEnd(roster.PeriodMonth);
        if (edits.Any(e => e.WorkDate < from || e.WorkDate > to)) return (Fail(400, "Edits must fall within the roster's month."), 0, new());
        if (edits.Any(e => e.EntryType == EntryType.Shift && e.ShiftTypeId == null)) return (Fail(400, "Choose a shift type for each shift entry."), 0, new());
        var ai = S<ConstraintValidationAi>(); var ids = edits.Select(e => e.EmployeeId).Distinct().ToList();
        var before = ai.Evaluate(ds, new ValidationRequest(p.Id, roster.PeriodMonth, new(), ids)).Payload.Violations.Select(v => $"{v.EmployeeId}|{v.RuleCode}|{v.Message}").ToHashSet();
        var after = ai.Evaluate(ds, new ValidationRequest(p.Id, roster.PeriodMonth, edits.Select(e => new EntryEdit(e.EmployeeId, e.WorkDate, e.EntryType == EntryType.Shift ? e.ShiftTypeId : null, e.EntryType)).ToList(), ids)).Payload.Violations;
        var introduced = after.Where(v => !before.Contains($"{v.EmployeeId}|{v.RuleCode}|{v.Message}")).ToList();
        var blocks = introduced.Where(v => v.Severity == RuleSeverity.Block).ToList();
        if (blocks.Count > 0) return (Fail(400, "This change breaks a blocking rule, so it wasn't saved.", new { violations = blocks }), 0, new());
        var warns = introduced.Where(v => v.Severity == RuleSeverity.Warn).ToList();
        var unacked = warns.Where(v => acked == null || !acked.Contains(v.RuleCode)).ToList();
        if (unacked.Count > 0) return (Fail(409, "Acknowledge the warnings to save this change.", new { needsAcknowledgement = unacked }), 0, unacked);

        var entries = await Db.RosterEntries.Where(e => e.RosterId == roster.Id && ids.Contains(e.EmployeeId)).ToListAsync(ct);
        var now = Clock.UtcNow; var applied = 0;
        foreach (var e in edits)
        {
            var row = entries.FirstOrDefault(x => x.EmployeeId == e.EmployeeId && x.WorkDate == e.WorkDate);
            var newShift = e.EntryType == EntryType.Shift ? e.ShiftTypeId : null;
            if (row != null && row.EntryType == e.EntryType && row.ShiftTypeId == newShift) continue;
            var beforeText = row == null ? "Empty" : Describe(ds, row.EntryType, row.ShiftTypeId);
            if (row == null) { row = new RosterEntry { RosterId = roster.Id, EmployeeId = e.EmployeeId, WorkDate = e.WorkDate, CreatedBy = Me.Id.ToString() }; Db.RosterEntries.Add(row); entries.Add(row); }
            row.EntryType = e.EntryType; row.ShiftTypeId = newShift; row.IsAiSuggested = false; row.AiSuggestionId = null; row.ModifiedAtUtc = now; row.ModifiedBy = Me.Id.ToString();
            var lead = false;
            if (roster.Status == RosterStatus.Published)
            {
                var anchor = newShift ?? (beforeText != "Empty" ? ds.ShiftTypes.FirstOrDefault(s => s.Name == beforeText)?.Id : null);
                var start = anchor != null ? e.WorkDate.ToDateTime(ds.Shift(anchor.Value).StartTime) : e.WorkDate.ToDateTime(TimeOnly.MinValue);
                lead = start - now < TimeSpan.FromHours(24);
                Notifier.Notify(e.EmployeeId, "RosterChanged", $"Your {e.WorkDate:ddd d MMM} entry changed", $"{beforeText} → {Describe(ds, e.EntryType, newShift)}.{(lead ? " This is less than 24 hours before the shift." : "")}", lead ? NotificationSeverity.Warning : NotificationSeverity.Info, "/me");
            }
            Db.RosterChangeLogs.Add(new RosterChangeLog { RosterEntryId = row.Id, RosterId = roster.Id, EmployeeId = e.EmployeeId, WorkDate = e.WorkDate, Before = beforeText, After = Describe(ds, e.EntryType, newShift), ChangedBy = Me.Id.ToString(), ChangedAtUtc = now, LeadTimeFlag = lead, Reason = string.Join("; ", new[] { reason, acked != null && warns.Count > 0 ? "Acknowledged " + string.Join(",", warns.Select(w => w.RuleCode).Distinct()) : null }.Where(x => !string.IsNullOrWhiteSpace(x))) });
            applied++;
        }
        roster.ModifiedAtUtc = now; roster.ModifiedBy = Me.Id.ToString();
        await Db.SaveChangesAsync(ct);
        return (null, applied, warns);
    }

    [HttpPut("{id:guid}/entries"), Authorize(Policy = Policies.CanBuildRoster)]
    public async Task<IActionResult> Edit(Guid id, EditDto dto, CancellationToken ct)
    {
        var roster = await Db.Rosters.FirstOrDefaultAsync(r => r.Id == id, ct); if (roster == null) return NotFound();
        var ds = await LoadAsync(ct); var p = ds.Proj(roster.ProjectId);
        if (!CanEdit(ds, p, roster)) return Fail(roster.Status == RosterStatus.Locked ? 409 : 403, roster.Status == RosterStatus.Locked ? "This roster is locked." : "You can edit only rosters for projects you own or manage.");
        if (dto.Edits == null || dto.Edits.Count == 0) return Fail(400, "Nothing to change.");
        var (err, applied, warns) = await ApplyAsync(ds, roster, p, dto.Edits, dto.Acknowledged, dto.Reason, ct);
        return err ?? Ok(new { applied, acknowledgedWarnings = warns.Count });
    }

    [HttpGet("{id:guid}/publish-preview")]
    public async Task<IActionResult> PublishPreview(Guid id, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var r = ds.Rosters.FirstOrDefault(x => x.Id == id); if (r == null || !VisibleProjects(ds).Contains(r.ProjectId)) return NotFound();
        var p = ds.Proj(r.ProjectId); var entries = ds.EntriesFor(id).ToList();
        var affected = entries.GroupBy(e => e.EmployeeId).OrderBy(g => ds.Emp(g.Key).FirstName).Select(g => new { employee = EmpRef(ds.Emp(g.Key)), shifts = g.Count(e => e.EntryType == EntryType.Shift), offs = g.Count(e => e.EntryType == EntryType.WeeklyOff) }).ToList();
        var v = S<ConstraintValidationAi>().Evaluate(ds, new ValidationRequest(p.Id, r.PeriodMonth, new()));
        var firstDay = ShiftMath.MonthStart(r.PeriodMonth); var lead = firstDay.ToDateTime(TimeOnly.MinValue) - Clock.UtcNow < TimeSpan.FromHours(24);
        return Ok(new { roster = new { r.Id, month = r.PeriodMonth, status = r.Status.ToString(), project = p.Name }, affected, affectedCount = affected.Count, leadTimeFlag = lead, blocking = v.Payload.Violations.Where(x => x.Severity == RuleSeverity.Block), warnings = v.Payload.Violations.Where(x => x.Severity == RuleSeverity.Warn), canPublish = !v.Payload.HasBlock && r.Status is RosterStatus.Draft or RosterStatus.PendingPublish });
    }

    public sealed record PublishDto(bool Confirm, List<string>? Acknowledged);

    [HttpPost("{id:guid}/publish"), Authorize(Policy = Policies.CanBuildRoster)]
    public async Task<IActionResult> Publish(Guid id, PublishDto dto, CancellationToken ct)
    {
        if (!dto.Confirm) return Fail(400, "Confirm the publish after reviewing who is affected.");
        var roster = await Db.Rosters.FirstOrDefaultAsync(r => r.Id == id, ct); if (roster == null) return NotFound();
        var ds = await LoadAsync(ct); var p = ds.Proj(roster.ProjectId);
        if (!CanEdit(ds, p, roster)) return Fail(403, "You can publish only rosters for projects you own or manage.");
        if (roster.Status is RosterStatus.Published or RosterStatus.Locked) return Fail(409, $"This roster is already {roster.Status.ToString().ToLowerInvariant()}.");
        var v = S<ConstraintValidationAi>().Evaluate(ds, new ValidationRequest(p.Id, roster.PeriodMonth, new()));
        if (v.Payload.HasBlock) return Fail(400, "Fix the blocking conflicts before publishing.", new { violations = v.Payload.Violations.Where(x => x.Severity == RuleSeverity.Block) });
        var unacked = v.Payload.Violations.Where(x => x.Severity == RuleSeverity.Warn && (dto.Acknowledged == null || !dto.Acknowledged.Contains(x.RuleCode))).ToList();
        if (unacked.Count > 0) return Fail(409, "Acknowledge the warnings to publish.", new { needsAcknowledgement = unacked });
        roster.Status = RosterStatus.Published; roster.PublishedAtUtc = Clock.UtcNow; roster.PublishedBy = Me.Id.ToString();
        var lead = ShiftMath.MonthStart(roster.PeriodMonth).ToDateTime(TimeOnly.MinValue) - Clock.UtcNow < TimeSpan.FromHours(24);
        var staff = ds.EntriesFor(roster.Id).Select(e => e.EmployeeId).Distinct().ToList();
        foreach (var e in staff) Notifier.Notify(e, "RosterPublished", $"{ShiftMath.MonthStart(roster.PeriodMonth):MMMM yyyy} roster is ready", $"Your {p.Name} roster is published.{(lead ? " It starts in less than 24 hours." : "")}", lead ? NotificationSeverity.Warning : NotificationSeverity.Info, "/me");
        await Db.SaveChangesAsync(ct);
        return Ok(new { roster.Id, status = roster.Status.ToString(), notified = staff.Count, leadTimeFlag = lead });
    }

    private static readonly Dictionary<string, EntryType> Words = new(StringComparer.OrdinalIgnoreCase) { ["OFF"] = EntryType.WeeklyOff, ["WO"] = EntryType.WeeklyOff, ["LEAVE"] = EntryType.Leave, ["HOL"] = EntryType.Holiday };

    public sealed record UploadDto(string Csv);

    /// <summary>CSV upload: employeeNumber,date,shift (a shift code, or OFF / LEAVE / HOL). commit=false only validates.</summary>
    [HttpPost("{id:guid}/upload"), Authorize(Policy = Policies.CanBuildRoster)]
    public async Task<IActionResult> Upload(Guid id, [FromBody] UploadDto dto, [FromQuery] bool commit = false, CancellationToken ct = default)
    {
        var roster = await Db.Rosters.FirstOrDefaultAsync(r => r.Id == id, ct); if (roster == null) return NotFound();
        var ds = await LoadAsync(ct); var p = ds.Proj(roster.ProjectId);
        if (!CanEdit(ds, p, roster)) return Fail(403, "You can upload only to rosters for projects you own or manage.");
        var byNo = ds.Employees.ToDictionary(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase); var byCode = ds.ShiftTypes.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>(); var edits = new List<DraftEntryDto>(); var line = 0; var bad = 0;
        var from = ShiftMath.MonthStart(roster.PeriodMonth); var to = ShiftMath.MonthEnd(roster.PeriodMonth);
        foreach (var raw in (dto.Csv ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            line++; var l = raw.Trim(); if (line == 1 && l.StartsWith("employee", StringComparison.OrdinalIgnoreCase)) continue;
            var c = l.Split(',').Select(x => x.Trim()).ToArray();
            if (c.Length < 3) { rows.Add(new { row = line, ok = false, message = "Expected 3 columns: employeeNumber,date,shift." }); bad++; continue; }
            if (!byNo.TryGetValue(c[0], out var emp)) { rows.Add(new { row = line, ok = false, message = $"Unknown employee number {c[0]}." }); bad++; continue; }
            if (!DateOnly.TryParse(c[1], out var date) || date < from || date > to) { rows.Add(new { row = line, ok = false, message = $"'{c[1]}' isn't a date in {roster.PeriodMonth}." }); bad++; continue; }
            if (Words.TryGetValue(c[2], out var et)) edits.Add(new DraftEntryDto(emp.Id, date, null, et));
            else if (byCode.TryGetValue(c[2], out var st)) edits.Add(new DraftEntryDto(emp.Id, date, st.Id, EntryType.Shift));
            else { rows.Add(new { row = line, ok = false, message = $"Unknown shift '{c[2]}'. Use a shift code, OFF, LEAVE or HOL." }); bad++; continue; }
            rows.Add(new { row = line, ok = true, message = "OK" });
        }
        var val = S<ConstraintValidationAi>().Evaluate(ds, new ValidationRequest(p.Id, roster.PeriodMonth, edits.Select(e => new EntryEdit(e.EmployeeId, e.WorkDate, e.ShiftTypeId, e.EntryType)).ToList()));
        if (!commit) return Ok(new { rows, validRows = edits.Count, errorRows = bad, violations = val.Payload.Violations, committed = false });
        if (bad > 0) return Fail(400, $"Fix {bad} row(s) with errors before uploading.", new { rows });
        var (err, applied, _) = await ApplyAsync(ds, roster, p, edits, val.Payload.Violations.Where(v => v.Severity == RuleSeverity.Warn).Select(v => v.RuleCode).Distinct().ToList(), "CSV upload", ct);
        if (err != null) return err;
        if (roster.Status == RosterStatus.Draft && roster.Source == RosterSource.Manual) { roster.Source = RosterSource.Upload; await Db.SaveChangesAsync(ct); }
        return Ok(new { rows, validRows = edits.Count, errorRows = 0, committed = true, applied });
    }

    /// <summary>SEC-10: streamed to the caller, never written to disk.</summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var ds = await LoadAsync(ct); var r = ds.Rosters.FirstOrDefault(x => x.Id == id); if (r == null || !VisibleProjects(ds).Contains(r.ProjectId)) return NotFound();
        var sb = new StringBuilder("employeeNumber,employeeName,date,shift\n");
        foreach (var e in ds.EntriesFor(id).OrderBy(e => ds.Emp(e.EmployeeId).EmployeeNumber).ThenBy(e => e.WorkDate))
            sb.Append($"{ds.Emp(e.EmployeeId).EmployeeNumber},\"{ds.Emp(e.EmployeeId).FullName}\",{e.WorkDate:yyyy-MM-dd},{(e.EntryType == EntryType.Shift && e.ShiftTypeId != null ? ds.Shift(e.ShiftTypeId.Value).Code : e.EntryType == EntryType.WeeklyOff ? "OFF" : e.EntryType == EntryType.Leave ? "LEAVE" : "HOL")}\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"roster-{ds.Proj(r.ProjectId).Code}-{r.PeriodMonth}.csv");
    }
}
