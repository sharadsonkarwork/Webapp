using System.Globalization;
using Rosterix.Application.Allowance;
using Rosterix.Application.Data;
using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Application.Ai;

// ---------------------------------------------------------------- AI-UC-04
public sealed record EntryEdit(Guid EmployeeId, DateOnly WorkDate, Guid? ShiftTypeId, EntryType EntryType);
public sealed record ValidationRequest(Guid ProjectId, string Month, List<EntryEdit> Edits, List<Guid>? EmployeeIds = null);
public sealed record ValidationItem(Guid EmployeeId, string EmployeeName, string RuleCode, RuleSeverity Severity, string Message, List<string> Affected, string? SuggestedFix);
public sealed record NearMiss(Guid EmployeeId, string EmployeeName, string RuleCode, string Message);
public sealed record ValidationPayload(List<ValidationItem> Violations, List<NearMiss> NearMisses, bool HasBlock, int EmployeesChecked);

/// <summary>Runs the shared rule engine on every edit, then adds a near-miss band (e.g. day 5 of 6).</summary>
public sealed class ConstraintValidationAi : AiServiceBase, IAiUseCase<ValidationRequest, ValidationPayload>
{
    private readonly IRuleEngine _rules;
    public ConstraintValidationAi(IAiDataSource s, IAiProvider p, IRuleEngine rules) : base(s, p) { _rules = rules; }
    public string Code => "AI-UC-04";
    public async Task<AiSuggestion<ValidationPayload>> SuggestAsync(ValidationRequest r, CancellationToken ct = default)
        => Evaluate(await Source.LoadAsync(ct), r);

    public AiSuggestion<ValidationPayload> Evaluate(Dataset ds, ValidationRequest r)
    {
        var roster = ds.FindRoster(r.ProjectId, r.Month);
        var ids = (r.EmployeeIds ?? new List<Guid>()).Concat(r.Edits.Select(e => e.EmployeeId)).Distinct().ToList();
        if (ids.Count == 0 && roster != null) ids = ds.EntriesFor(roster.Id).Select(e => e.EmployeeId).Distinct().ToList();
        var violations = new List<ValidationItem>(); var near = new List<NearMiss>();
        foreach (var id in ids)
        {
            var existing = roster == null ? new List<RosterEntry>() : ds.EntriesFor(roster.Id).Where(e => e.EmployeeId == id).ToList();
            var merged = existing.Where(e => !r.Edits.Any(x => x.EmployeeId == id && x.WorkDate == e.WorkDate)).ToList();
            merged.AddRange(r.Edits.Where(x => x.EmployeeId == id).Select(x => new RosterEntry { EmployeeId = id, WorkDate = x.WorkDate, ShiftTypeId = x.ShiftTypeId, EntryType = x.EntryType }));
            var ctx = RuleContextFactory.For(ds, id, r.ProjectId, r.Month, merged);
            foreach (var v in _rules.Evaluate(ctx).Where(v => v.RuleCode is not ("BR-11" or "BR-12")))
                violations.Add(new ValidationItem(id, ctx.EmployeeName, v.RuleCode, v.Severity, v.Message, v.AffectedEntities.ToList(), v.SuggestedFix));
            // near-miss band. A whole-roster scan only reports "day 5 of 6" (a regular 6-on/1-off pattern sits exactly at the limit, which is legal and not news);
            // a scan of edits also reports anything at the limit that the edit touched.
            var edited = r.Edits.Where(x => x.EmployeeId == id).Select(x => x.WorkDate).ToHashSet(); var scan = edited.Count == 0;
            foreach (var run in Br02MaxConsecutiveDays.Runs(ctx).Where(x => x.Count == ctx.Parameters.MaxConsecutiveDays - 1 || (!scan && x.Count == ctx.Parameters.MaxConsecutiveDays && x.Any(edited.Contains))))
                near.Add(new NearMiss(id, ctx.EmployeeName, "BR-02", $"{ctx.EmployeeName} is on day {run.Count} of {ctx.Parameters.MaxConsecutiveDays} consecutive working days ({run[0]:d MMM}–{run[^1]:d MMM}). {(run.Count < ctx.Parameters.MaxConsecutiveDays ? "One more shift would reach" : "This is exactly at")} the limit."));
            foreach (var (a, b, gap) in Br03MinRest.Gaps(ctx).Where(g => g.Gap >= ctx.Parameters.MinRestHours && g.Gap < ctx.Parameters.MinRestHours + 1 && (!scan && (edited.Contains(g.A.Date) || edited.Contains(g.B.Date)))))
                near.Add(new NearMiss(id, ctx.EmployeeName, "BR-03", $"Rest between {a.Date:d MMM} and {b.Date:d MMM} is {gap:0.#} h, just {gap - ctx.Parameters.MinRestHours:0.#} h above the minimum."));
            foreach (var (ws, h) in Br07MaxWeeklyHours.Weekly(ctx).Where(w => w.Hours <= ctx.Parameters.MaxWeeklyHours && w.Hours > ctx.Parameters.MaxWeeklyHours - 4 && (w.Hours < ctx.Parameters.MaxWeeklyHours || (!scan && Enumerable.Range(0, 7).Any(i => edited.Contains(w.WeekStart.AddDays(i)))))))
                near.Add(new NearMiss(id, ctx.EmployeeName, "BR-07", $"{h:0.#} h scheduled in the week of {ws:d MMM}. The guideline is {ctx.Parameters.MaxWeeklyHours:0.#} h."));
        }
        var hasBlock = violations.Any(v => v.Severity == RuleSeverity.Block);
        return new AiSuggestion<ValidationPayload>
        {
            UseCaseCode = Code, Confidence = 0.97,
            Summary = violations.Count == 0 ? (near.Count == 0 ? "No rule conflicts." : $"No conflicts, but {near.Count} near miss(es).") : $"{violations.Count} conflict(s): {violations.Count(v => v.Severity == RuleSeverity.Block)} blocking, {violations.Count(v => v.Severity == RuleSeverity.Warn)} warning.",
            Impact = hasBlock ? "Blocking conflicts must be fixed before this can be saved or published." : "Warnings can be saved once you acknowledge them.",
            Reasons = Narrate(new[] { new AiReason($"The same 12 rules used by the allowance calculator were applied to {ids.Count} employee(s)."), new AiReason("Rule limits come from Foundation data, not from the code.", "foundation", "rules", "Shift rules") }),
            Payload = new(violations, near, hasBlock, ids.Count)
        };
    }
}

// ---------------------------------------------------------------- AI-UC-05
public sealed record ForecastRequest(Guid ProjectId, string Month);
public sealed record ForecastMonth(string Month, decimal Amount, int ShiftCount);
public sealed record ForecastDriver(string Label, decimal Impact, string Detail);
public sealed record ForecastPayload(decimal Forecast, decimal HistoryAverage, double VariancePct, List<ForecastMonth> History, List<ForecastDriver> Drivers, int ShiftCount);

public sealed class CostPredictionAi : AiServiceBase, IAiUseCase<ForecastRequest, ForecastPayload>
{
    private readonly AllowanceCalculator _calc;
    public CostPredictionAi(IAiDataSource s, IAiProvider p, AllowanceCalculator calc) : base(s, p) { _calc = calc; }
    public string Code => "AI-UC-05";
    public async Task<AiSuggestion<ForecastPayload>> SuggestAsync(ForecastRequest r, CancellationToken ct = default)
    {
        var ds = await Source.LoadAsync(ct);
        var cur = _calc.Calculate(ds, r.ProjectId, r.Month);
        var past = ds.Rosters.Where(x => x.ProjectId == r.ProjectId && x.Status == RosterStatus.Published && string.CompareOrdinal(x.PeriodMonth, r.Month) < 0).OrderByDescending(x => x.PeriodMonth).Take(3).OrderBy(x => x.PeriodMonth).ToList();
        var hist = past.Select(p => { var c = _calc.Calculate(ds, r.ProjectId, p.PeriodMonth); return new ForecastMonth(p.PeriodMonth, c.Total, c.Lines.Count); }).ToList();
        var avg = hist.Count == 0 ? 0 : hist.Average(h => h.Amount);
        var variance = avg == 0 ? 0 : (double)((cur.Total - avg) / avg * 100m);
        var drivers = new List<ForecastDriver>();
        var byShift = cur.Lines.GroupBy(l => l.ShiftTypeId).Select(g => (Name: ds.Shift(g.Key).Name, Amt: g.Sum(l => l.Amount), N: g.Count())).OrderByDescending(x => x.Amt).ToList();
        foreach (var s in byShift.Take(3)) drivers.Add(new ForecastDriver($"{s.Name} shifts", s.Amt, $"{s.N} shift(s) at {(s.N == 0 ? 0 : s.Amt / s.N):C0}".Replace("¤", "₹")));
        var lastCount = hist.LastOrDefault()?.ShiftCount ?? cur.Lines.Count;
        if (hist.Count > 0 && cur.Lines.Count != lastCount) drivers.Add(new ForecastDriver("Volume change", (cur.Lines.Count - lastCount) * (cur.Lines.Count == 0 ? 0 : cur.Total / cur.Lines.Count), $"{cur.Lines.Count} eligible shifts vs {lastCount} last month."));
        var changed = ds.Rates.Where(x => x.EffectiveFrom > ShiftMath.MonthStart(past.FirstOrDefault()?.PeriodMonth ?? r.Month) && x.EffectiveFrom <= ShiftMath.MonthEnd(r.Month) && x.EffectiveFrom.Day == 1 && x.Currency == "INR" && x.EffectiveFrom.Month != 1).ToList();
        foreach (var ch in changed.Take(1)) drivers.Add(new ForecastDriver("Rate revision", 0, $"{ds.Shift(ch.ShiftTypeId).Name} rate became ₹{ch.Amount:0} on {ch.EffectiveFrom:d MMM yyyy}."));
        var reasons = new List<AiReason>
        {
            new($"Costed {cur.Lines.Count} allowance-eligible shift(s) in the {r.Month} roster at the rates effective on each date.", "roster", ds.FindRoster(r.ProjectId, r.Month)?.Id.ToString(), $"Roster {r.Month}"),
            hist.Count > 0 ? new($"Compared with the average of the last {hist.Count} month(s): ₹{avg:N0}.", "roster", past[^1].Id.ToString(), $"Roster {past[^1].PeriodMonth}") : new("There is no published history to compare against."),
        };
        return new AiSuggestion<ForecastPayload>
        {
            UseCaseCode = Code, Confidence = AiScoring.Clamp(0.45 + 0.15 * hist.Count),
            Summary = $"Forecast ₹{cur.Total:N0} for {ShiftMath.MonthStart(r.Month):MMMM yyyy}{(hist.Count > 0 ? $" ({(variance >= 0 ? "+" : "")}{variance:0.#}% vs the last {hist.Count}-month average)" : "")}.",
            Impact = "For planning only. Final amounts come from the allowance run after the month closes.",
            Reasons = Narrate(reasons), Payload = new(cur.Total, avg, Math.Round(variance, 1), hist, drivers, cur.Lines.Count)
        };
    }
}

public sealed record MyAllowanceRequest(Guid EmployeeId, string Month);
public sealed record MyAllowanceLine(DateOnly Date, string Shift, decimal Rate, decimal Amount, string Trace, string Source);
public sealed record MyAllowancePayload(string Month, decimal Total, List<MyAllowanceLine> Lines, string Source);

public sealed class MyAllowanceExplainer : AiServiceBase, IAiUseCase<MyAllowanceRequest, MyAllowancePayload>
{
    private readonly AllowanceCalculator _calc;
    public MyAllowanceExplainer(IAiDataSource s, IAiProvider p, AllowanceCalculator calc) : base(s, p) { _calc = calc; }
    public string Code => "AI-UC-05";
    public async Task<AiSuggestion<MyAllowancePayload>> SuggestAsync(MyAllowanceRequest r, CancellationToken ct = default)
    {
        var ds = await Source.LoadAsync(ct);
        var lines = new List<MyAllowanceLine>();
        // prefer calculated lines; otherwise explain what the published roster would earn
        var runIds = ds.Runs.Where(x => x.PeriodMonth == r.Month).Select(x => x.Id).ToHashSet();
        var stored = ds.Lines.Where(l => l.EmployeeId == r.EmployeeId && runIds.Contains(l.AllowanceRunId)).OrderBy(l => l.WorkDate).ToList();
        string source;
        if (stored.Count > 0) { source = "Calculated allowance run"; lines = stored.Select(l => new MyAllowanceLine(l.WorkDate, ds.Shift(l.ShiftTypeId).Name, l.RateApplied, l.Amount, l.CalculationTrace, "run")).ToList(); }
        else
        {
            source = "Estimated from your published roster";
            foreach (var proj in ds.Rosters.Where(x => x.PeriodMonth == r.Month && x.Status != RosterStatus.Draft && ds.RosterEntries.Any(e => e.RosterId == x.Id && e.EmployeeId == r.EmployeeId)).Select(x => x.ProjectId).Distinct())
                lines.AddRange(_calc.Calculate(ds, proj, r.Month, ds.EntriesFor(ds.FindRoster(proj, r.Month)!.Id).Where(e => e.EmployeeId == r.EmployeeId)).Lines
                    .Select(l => new MyAllowanceLine(l.WorkDate, ds.Shift(l.ShiftTypeId).Name, l.Rate, l.Amount, l.Trace, "estimate")));
        }
        var total = lines.Sum(l => l.Amount);
        return new AiSuggestion<MyAllowancePayload>
        {
            UseCaseCode = Code, Confidence = stored.Count > 0 ? 0.95 : 0.7,
            Summary = lines.Count == 0 ? $"No allowance-eligible shifts found for {r.Month}." : $"₹{total:N0} for {lines.Count} eligible shift(s) in {r.Month}.",
            Impact = "Explains how each amount was worked out. If something looks wrong, raise a query.",
            Reasons = Narrate(new[] { new AiReason($"Source: {source}."), new AiReason("Each line shows the shift, the rate in effect on that date and any rate change.") }),
            Payload = new(r.Month, total, lines, source)
        };
    }
}

// ---------------------------------------------------------------- AI-UC-06
public sealed record RippleRequest(Guid EmployeeId, Guid? FromProjectId, Guid ToProjectId, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
public sealed record RippleRow(string EntityType, string Label, string Before, string After, string Change, string? EntityId);
public sealed record RipplePayload(List<RippleRow> Rows, List<ValidationItem> Violations, int RosterEntriesAffected, int AllowanceLinesAffected, decimal ArrearExposure, bool HasBlock);

public sealed class ConflictRippleAi : AiServiceBase, IAiUseCase<RippleRequest, RipplePayload>
{
    private readonly IRuleEngine _rules;
    public ConflictRippleAi(IAiDataSource s, IAiProvider p, IRuleEngine rules) : base(s, p) { _rules = rules; }
    public string Code => "AI-UC-06";
    public async Task<AiSuggestion<RipplePayload>> SuggestAsync(RippleRequest r, CancellationToken ct = default)
    {
        var ds = await Source.LoadAsync(ct);
        var emp = ds.Emp(r.EmployeeId); var to = ds.Proj(r.ToProjectId);
        var rows = new List<RippleRow>(); var viol = new List<ValidationItem>();
        var backdated = r.EffectiveFrom < ds.Today;
        // tags
        var oldTags = ds.EmployeeTags.Where(t => t.EmployeeId == r.EmployeeId && t.Status is TagStatus.Approved or TagStatus.Backtagged && (r.FromProjectId == null || t.ProjectId == r.FromProjectId) && (t.ValidTo == null || t.ValidTo >= r.EffectiveFrom)).ToList();
        foreach (var t in oldTags)
            rows.Add(new RippleRow("Tag", $"{ds.Proj(t.ProjectId).Name} tag", $"{t.ValidFrom:d MMM yyyy} – {(t.ValidTo?.ToString("d MMM yyyy") ?? "open")}", $"{t.ValidFrom:d MMM yyyy} – {r.EffectiveFrom.AddDays(-1):d MMM yyyy}", "Changed", t.Id.ToString()));
        rows.Add(new RippleRow("Tag", $"{to.Name} tag", "None", $"{r.EffectiveFrom:d MMM yyyy} – {(r.EffectiveTo?.ToString("d MMM yyyy") ?? "open")}", "Created", null));
        // roster entries on the old project after the effective date
        var fromId = r.FromProjectId ?? oldTags.FirstOrDefault()?.ProjectId;
        var oldRosterIds = ds.Rosters.Where(x => x.ProjectId == fromId).Select(x => x.Id).ToHashSet();
        var entries = ds.RosterEntries.Where(e => e.EmployeeId == r.EmployeeId && oldRosterIds.Contains(e.RosterId) && e.WorkDate >= r.EffectiveFrom && (r.EffectiveTo == null || e.WorkDate <= r.EffectiveTo)).ToList();
        foreach (var g in entries.GroupBy(e => ds.Rosters.First(x => x.Id == e.RosterId).PeriodMonth))
        {
            var shifts = g.Count(e => e.EntryType == EntryType.Shift);
            var rs = ds.Rosters.First(x => x.Id == g.First().RosterId);
            rows.Add(new RippleRow("Roster entries", $"{ds.Proj(rs.ProjectId).Name}, {g.Key} ({rs.Status})", $"{g.Count()} entries ({shifts} shifts)", "Removed from this project", "Removed", rs.Id.ToString()));
        }
        // allowance lines
        var runs = ds.Runs.Where(x => x.ProjectId == fromId).ToDictionary(x => x.Id);
        var lines = ds.Lines.Where(l => l.EmployeeId == r.EmployeeId && runs.ContainsKey(l.AllowanceRunId) && l.WorkDate >= r.EffectiveFrom).ToList();
        var exposure = 0m;
        foreach (var g in lines.GroupBy(l => l.AllowanceRunId))
        {
            var run = runs[g.Key]; var submitted = run.Status is RunStatus.Submitted or RunStatus.CompensationApproved;
            var amt = g.Sum(l => l.Amount); if (submitted) exposure += amt;
            rows.Add(new RippleRow("Allowance lines", $"{ds.Proj(run.ProjectId).Name} run, {run.PeriodMonth} ({run.Status})", $"{g.Count()} line(s), ₹{amt:N0}", submitted ? $"Recovery or arrear of ₹{amt:N0} needed" : "Recalculated on next run", submitted ? "Arrear" : "Changed", run.Id.ToString()));
        }
        if (exposure > 0) rows.Add(new RippleRow("Arrear", $"{emp.FullName}", "None", $"₹{exposure:N0} exposure", "Arrear", null));
        // rules on the post-state: BR-01 (overlap) and BR-12 (backdate)
        var extra = new EmployeeTag { EmployeeId = r.EmployeeId, ProjectId = r.ToProjectId, ValidFrom = r.EffectiveFrom, ValidTo = r.EffectiveTo, Status = TagStatus.Approved };
        var ctx = RuleContextFactory.For(ds, r.EmployeeId, r.ToProjectId, ShiftMath.Month(r.EffectiveFrom), null, new[] { extra }, backdated ? new BackdateInfo(r.ToProjectId, r.EffectiveFrom, r.EffectiveTo) : null);
        // the old tag is shortened by the movement, so drop it from the overlap test
        var pruned = new RuleContext
        {
            EmployeeId = ctx.EmployeeId, EmployeeName = ctx.EmployeeName, Grade = ctx.Grade, BusinessUnitId = ctx.BusinessUnitId, ProjectId = ctx.ProjectId, Project = ctx.Project, Today = ctx.Today,
            PeriodStart = ctx.PeriodStart, PeriodEnd = ctx.PeriodEnd, Entries = ctx.Entries, LeaveDays = ctx.LeaveDays, RestrictedHolidays = ctx.RestrictedHolidays, Rates = ctx.Rates, Parameters = ctx.Parameters, Backdate = ctx.Backdate,
            Tags = ctx.Tags.Select(t => fromId != null && t.ProjectId == fromId && t.Overlaps(r.EffectiveFrom, null) ? t with { To = r.EffectiveFrom.AddDays(-1) } : t).Where(t => t.To == null || t.To >= t.From).ToList()
        };
        foreach (var v in new IRule[] { new Br01NoOverlappingTags(), new Br12BackdateSafe() }.SelectMany(x => x.Evaluate(pruned)))
            viol.Add(new ValidationItem(r.EmployeeId, emp.FullName, v.RuleCode, v.Severity, v.Message, v.AffectedEntities.ToList(), v.SuggestedFix));
        var reasons = new List<AiReason>
        {
            new($"Looked at tags, roster entries, allowance lines and arrears linked to {emp.FullName} from {r.EffectiveFrom:d MMM yyyy}.", "employee", emp.Id.ToString(), emp.FullName),
            new(backdated ? $"This change is backdated by {(ds.Today.DayNumber - r.EffectiveFrom.DayNumber)} day(s), so earlier records are touched." : "This change starts today or later, so no past records are touched.")
        };
        if (viol.Count > 0) reasons.Add(new AiReason($"{viol.Count} rule conflict(s) would result: {string.Join(", ", viol.Select(v => v.RuleCode).Distinct())}."));
        return new AiSuggestion<RipplePayload>
        {
            UseCaseCode = Code, Confidence = 0.92,
            Summary = $"{rows.Count} downstream record group(s) change{(exposure > 0 ? $"; ₹{exposure:N0} may need to be recovered or paid as an arrear" : "")}.",
            Impact = "This is a preview. Nothing changes until you commit the movement as a second step.",
            Reasons = Narrate(reasons), Payload = new(rows, viol, entries.Count, lines.Count, exposure, viol.Any(v => v.Severity == RuleSeverity.Block))
        };
    }
}
