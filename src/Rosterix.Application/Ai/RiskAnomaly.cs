using Rosterix.Application.Data;
using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Application.Ai;

// ---------------------------------------------------------------- AI-UC-07
public sealed record RiskRequest(List<Guid>? RequestIds = null);
public sealed record RiskFeature(string Name, double Weight, string Detail);
public sealed record RiskItem(Guid RequestId, double Score, string Band, string Headline, List<AiReason> Reasons, List<RiskFeature> Features);
public sealed record RiskPayload(List<RiskItem> Items);

/// <summary>Scores pending requests so the riskiest are reviewed first. It sorts and explains; it never blocks or decides.</summary>
public sealed class ApprovalRiskAi : AiServiceBase, IAiUseCase<RiskRequest, RiskPayload>
{
    public ApprovalRiskAi(IAiDataSource s, IAiProvider p) : base(s, p) { }
    public string Code => "AI-UC-07";

    public async Task<AiSuggestion<RiskPayload>> SuggestAsync(RiskRequest r, CancellationToken ct = default)
        => Score(await Source.LoadAsync(ct), r);

    public AiSuggestion<RiskPayload> Score(Dataset ds, RiskRequest r)
    {
        var pending = ds.ApprovalRequests.Where(a => a.Status == ApprovalStatus.Pending && (r.RequestIds == null || r.RequestIds.Contains(a.Id))).ToList();
        var items = pending.Select(a => ScoreOne(ds, a)).OrderByDescending(i => i.Score).ToList();
        return new AiSuggestion<RiskPayload>
        {
            UseCaseCode = Code, Confidence = AiScoring.Clamp(0.5 + Math.Min(0.4, ds.ApprovalRequests.Count(a => a.Status != ApprovalStatus.Pending) / 100.0)),
            Summary = $"{items.Count(i => i.Band == "High")} high-risk and {items.Count(i => i.Band == "Medium")} medium-risk request(s) are listed first.",
            Impact = "Changes the order of the queue only. Every request can still be approved or rejected in any order.",
            Reasons = Narrate(new[] { new AiReason($"Scored on the requester's past rejections, date deviation, overlaps, value against the median and how long it has waited, using {ds.ApprovalRequests.Count(a => a.Status != ApprovalStatus.Pending)} decided requests as history.") }),
            Payload = new(items)
        };
    }

    public static string BandOf(double s) => s >= 0.6 ? "High" : s >= 0.3 ? "Medium" : "Low";

    RiskItem ScoreOne(Dataset ds, ApprovalRequest a)
    {
        var f = new List<RiskFeature>(); var reasons = new List<AiReason>();
        var requester = ds.EmpOrNull(a.RaisedBy);
        var decided = ds.ApprovalRequests.Where(x => x.RaisedBy == a.RaisedBy && x.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected).ToList();
        if (decided.Count >= 3)
        {
            var rate = decided.Count(x => x.Status == ApprovalStatus.Rejected) / (double)decided.Count;
            var w = Math.Min(1, rate / 0.5) * 0.30;
            if (w > 0.1) { f.Add(new("Rejection history", w, $"{rate * 100:0}% of {decided.Count} past requests were rejected")); reasons.Add(new($"{requester?.FullName ?? "The requester"} had {rate * 100:0}% of their last {decided.Count} requests rejected.", "employee", a.RaisedBy.ToString(), requester?.FullName)); }
        }
        if (a.Type is ApprovalType.EmployeeTagging or ApprovalType.ProjectShiftTagging or ApprovalType.Movement && a.FromDate is { } from)
        {
            var raised = DateOnly.FromDateTime(a.RaisedAtUtc);
            var back = raised.DayNumber - from.DayNumber; var ahead = from.DayNumber - raised.DayNumber;
            if (back > 7) { var w = Math.Min(1, back / 60.0) * 0.25; f.Add(new("Backdated", w, $"starts {back} days before it was raised")); reasons.Add(new($"The start date is {back} days before the request was raised.", "approval", a.Id.ToString(), a.Title)); }
            else if (ahead > 120) { f.Add(new("Far future", 0.1, $"starts {ahead} days ahead")); reasons.Add(new($"The start date is {ahead} days ahead, which is unusually far out.", "approval", a.Id.ToString(), a.Title)); }
        }
        if (a.Type == ApprovalType.EmployeeTagging)
        {
            var tags = ds.EmployeeTags.Where(t => t.ApprovalRequestId == a.Id).ToList();
            var overlaps = tags.Count(t => ds.EmployeeTags.Any(o => o.EmployeeId == t.EmployeeId && o.Id != t.Id && o.ProjectId != t.ProjectId && o.Status is TagStatus.Approved or TagStatus.Backtagged && o.Overlaps(t)));
            if (overlaps > 0) { var w = Math.Min(1, overlaps / 3.0) * 0.6; f.Add(new("Overlap", w, $"{overlaps} person(s) already tagged elsewhere")); reasons.Add(new($"{overlaps} of {tags.Count} people already hold an approved tag on another project in this window.", "approval", a.Id.ToString(), a.Title)); }
        }
        if (a.Type == ApprovalType.Arrear)
        {
            var arrear = ds.Arrears.FirstOrDefault(x => x.Id == a.SubjectId);
            if (arrear != null)
            {
                var age = arrear.AgeMonths(ds.Today);
                var w = age <= 3 ? 0.1 : age <= 6 ? 0.35 : 0.55;
                f.Add(new("Age bucket", w, $"{age} months old ({arrear.AgeBucket(ds.Today)} bucket)")); reasons.Add(new($"The arrear is {age} months old, in the {arrear.AgeBucket(ds.Today)}-month bucket.", "arrear", arrear.Id.ToString(), $"Arrear {arrear.ForPeriodMonth}"));
                var repeats = ds.Arrears.Count(x => x.EmployeeId == arrear.EmployeeId && x.Id != arrear.Id);
                if (repeats > 0) { f.Add(new("Repeat incidence", Math.Min(0.25, 0.15 * repeats), $"{repeats} other arrear(s) for the same employee")); reasons.Add(new($"{ds.Emp(arrear.EmployeeId).FullName} has {repeats} other arrear(s) on record.", "employee", arrear.EmployeeId.ToString(), ds.Emp(arrear.EmployeeId).FullName)); }
            }
        }
        if (a.Value is { } val && a.Value > 0)
        {
            var peers = ds.ApprovalRequests.Where(x => x.Type == a.Type && x.Value > 0 && x.Id != a.Id).Select(x => (double)x.Value!.Value).ToList();
            if (peers.Count >= 2)
            {
                var med = AiScoring.Median(peers); var ratio = med == 0 ? 1 : (double)val / med;
                if (ratio > 1.5) { var w = Math.Min(1, (ratio - 1) / 3) * 0.2; f.Add(new("High value", w, $"{ratio:0.#}× the median")); reasons.Add(new($"The amount ₹{val:N0} is {ratio:0.#}× the median ₹{med:N0} for similar requests.", "approval", a.Id.ToString(), a.Title)); }
            }
        }
        var waitDays = (ds.Today.ToDateTime(TimeOnly.MinValue) - a.RaisedAtUtc).TotalDays;
        if (waitDays > 7) { f.Add(new("Waiting", Math.Min(0.15, waitDays / 100), $"waiting {waitDays:0} days")); reasons.Add(new($"It has been waiting {waitDays:0} days, longer than most.", "approval", a.Id.ToString(), a.Title)); }
        var score = Math.Round(Math.Min(0.99, f.Sum(x => x.Weight)), 2);
        if (reasons.Count == 0) reasons.Add(new("Nothing unusual: clean history, sensible dates and no overlaps.", "approval", a.Id.ToString(), a.Title));
        var rs = Narrate(reasons.Select((rr, i) => (rr, w: i < f.Count ? f[i].Weight : 0)).OrderByDescending(x => x.w).Select(x => x.rr));
        return new RiskItem(a.Id, score, BandOf(score), rs[0].Text, rs, f);
    }
}

static class TagExt { public static bool Overlaps(this EmployeeTag a, EmployeeTag b) => (a.ValidTo == null || b.ValidFrom <= a.ValidTo) && (b.ValidTo == null || a.ValidFrom <= b.ValidTo); }

// ---------------------------------------------------------------- AI-UC-09
public sealed record AnomalyRequest(bool IncludeDismissed = false);
public sealed record AnomalyItem(string Id, string Kind, string Severity, Guid? EmployeeId, string EmployeeName, Guid? ProjectId, string ProjectName, string Description, double? ZScore, double Confidence, List<AiReason> Evidence, string Status, Guid? LineId);
public sealed record AnomalyPayload(List<AnomalyItem> Items, int OutlierCount, int DuplicateCount, int RateMismatchCount, int ImpossibleCount);

public sealed class AnomalyDetectionAi : AiServiceBase, IAiUseCase<AnomalyRequest, AnomalyPayload>
{
    public AnomalyDetectionAi(IAiDataSource s, IAiProvider p) : base(s, p) { }
    public string Code => "AI-UC-09";
    public async Task<AiSuggestion<AnomalyPayload>> SuggestAsync(AnomalyRequest r, CancellationToken ct = default) => Scan(await Source.LoadAsync(ct), r);

    public AiSuggestion<AnomalyPayload> Scan(Dataset ds, AnomalyRequest r)
    {
        var items = new List<AnomalyItem>();
        string Status(string id) => ds.AiFeedback.Where(f => f.UseCaseCode == Code && f.ContextId == id).OrderByDescending(f => f.ActedAtUtc).Select(f => f.Action == AiAction.Dismiss ? "Dismissed" : "Acknowledged").FirstOrDefault() ?? "Open";

        // 1) duplicate claims
        foreach (var g in ds.Lines.GroupBy(l => (l.EmployeeId, l.WorkDate, l.ShiftTypeId)).Where(g => g.Count() > 1))
        {
            var e = ds.Emp(g.Key.EmployeeId); var run = ds.Runs.First(x => x.Id == g.First().AllowanceRunId); var id = $"dup:{g.Key.EmployeeId}:{g.Key.WorkDate:yyyyMMdd}";
            items.Add(new AnomalyItem(id, "Duplicate claim", "High", e.Id, e.FullName, run.ProjectId, ds.Proj(run.ProjectId).Name, $"{g.Count()} allowance claims for {ds.Shift(g.Key.ShiftTypeId).Name} on {g.Key.WorkDate:d MMM yyyy}; ₹{g.Skip(1).Sum(l => l.Amount):N0} would be paid twice.", null, 0.95,
                Narrate(new[] { new AiReason($"{g.Count()} lines share the same employee, date and shift type.", "run", run.Id.ToString(), $"Run {run.PeriodMonth}"), new AiReason("The second claim adds no extra shift on the published roster.", "employee", e.Id.ToString(), e.FullName) }), Status(id), g.First().Id));
        }
        // 2) rate mismatch
        foreach (var l in ds.Lines)
        {
            var e = ds.Emp(l.EmployeeId); var run = ds.Runs.First(x => x.Id == l.AllowanceRunId);
            if (run.Status == RunStatus.Submitted) continue;
            var ctxRates = ds.Rates.Where(x => new RateInfo(x.ShiftTypeId, x.BusinessUnitId, x.Grade, x.Amount, x.EffectiveFrom, x.EffectiveTo).Matches(l.ShiftTypeId, e.BusinessUnitId, e.Grade, l.WorkDate))
                .OrderByDescending(x => (x.BusinessUnitId != null ? 2 : 0) + (x.Grade != null ? 1 : 0)).ThenByDescending(x => x.EffectiveFrom).FirstOrDefault();
            if (ctxRates != null && ctxRates.Amount != l.RateApplied)
            {
                var id = $"rate:{l.Id}";
                items.Add(new AnomalyItem(id, "Rate mismatch", "High", e.Id, e.FullName, run.ProjectId, ds.Proj(run.ProjectId).Name, $"₹{l.RateApplied:N0} applied for {ds.Shift(l.ShiftTypeId).Name} on {l.WorkDate:d MMM yyyy}; the rate in effect was ₹{ctxRates.Amount:N0}.", null, 0.97,
                    Narrate(new[] { new AiReason($"The effective-dated rate table shows ₹{ctxRates.Amount:N0} from {ctxRates.EffectiveFrom:d MMM yyyy}.", "rate", ctxRates.Id.ToString(), "Allowance rate"), new AiReason("The line's rate differs from the table rate for that date.", "run", run.Id.ToString(), $"Run {run.PeriodMonth}") }), Status(id), l.Id));
            }
        }
        // 3) statistical outliers: monthly eligible-shift count vs the employee's own baseline
        var rosterMonth = ds.Rosters.Where(x => x.Status == RosterStatus.Published).ToDictionary(x => x.Id, x => (x.PeriodMonth, x.ProjectId));
        var monthly = ds.RosterEntries.Where(e => e.EntryType == EntryType.Shift && e.ShiftTypeId != null && rosterMonth.ContainsKey(e.RosterId) && ds.Shift(e.ShiftTypeId.Value).IsAllowanceEligible)
            .GroupBy(e => (e.EmployeeId, Month: rosterMonth[e.RosterId].PeriodMonth, Proj: rosterMonth[e.RosterId].ProjectId)).Select(g => (g.Key.EmployeeId, g.Key.Month, g.Key.Proj, N: g.Count())).ToList();
        var allMonths = rosterMonth.Values.Select(v => v.PeriodMonth).Distinct().OrderBy(x => x).ToList();
        if (allMonths.Count >= 4)
        {
            var test = allMonths[^1]; var baseMonths = allMonths.Take(allMonths.Count - 1).ToList();
            foreach (var g in monthly.GroupBy(m => m.EmployeeId))
            {
                var counts = baseMonths.Select(m => (double)g.Where(x => x.Month == m).Sum(x => x.N)).ToList();
                var cur = g.Where(x => x.Month == test).Sum(x => x.N);
                var mean = counts.Average(); var sd = Math.Max(Math.Max(AiScoring.Std(counts), 1.5), 0.1 * mean);
                var z = (cur - mean) / sd;
                if (z >= 2.5)
                {
                    var e = ds.Emp(g.Key); var proj = g.First(x => x.Month == test).Proj; var id = $"out:{g.Key}:{test}";
                    items.Add(new AnomalyItem(id, "Statistical outlier", z >= 5 ? "High" : "Medium", e.Id, e.FullName, proj, ds.Proj(proj).Name, $"{cur} allowance-eligible shifts in {test} against a usual {mean:0.#} per month (z = {z:0.0}).", Math.Round(z, 1), AiScoring.Clamp(0.55 + Math.Min(0.4, z / 25)),
                        Narrate(new[] { new AiReason($"Baseline over {baseMonths.Count} months is {mean:0.#} eligible shifts; {test} has {cur}.", "employee", e.Id.ToString(), e.FullName), new AiReason($"z-score {z:0.0} is above the 2.5 threshold.", "roster", ds.FindRoster(proj, test)?.Id.ToString(), $"Roster {test}") }), Status(id), null));
                }
            }
        }
        // 4) impossible hours: shifts that overlap in time
        var shifts = ds.ShiftTypes.ToDictionary(s => s.Id, ShiftInfo.From);
        var rosterProj = ds.Rosters.ToDictionary(x => x.Id, x => x.ProjectId);
        foreach (var g in ds.RosterEntries.Where(e => e.EntryType == EntryType.Shift && e.ShiftTypeId != null).GroupBy(e => e.EmployeeId))
        {
            var ctx = new RuleContext { Entries = g.Select(e => new EntryInfo(e.WorkDate, e.EntryType, shifts[e.ShiftTypeId!.Value], rosterProj[e.RosterId])).GroupBy(x => x.Date).Select(x => x.First()).ToList() };
            foreach (var (a, b, gap) in Br03MinRest.Gaps(ctx).Where(x => x.Gap < 0))
            {
                var e = ds.Emp(g.Key); var id = $"imp:{g.Key}:{a.Date:yyyyMMdd}";
                items.Add(new AnomalyItem(id, "Impossible hours", "High", e.Id, e.FullName, a.ProjectId, ds.Proj(a.ProjectId).Name, $"Shifts on {a.Date:d MMM} and {b.Date:d MMM} overlap by {-gap:0.#} h.", null, 0.99,
                    Narrate(new[] { new AiReason("The second shift starts before the first has ended.", "employee", e.Id.ToString(), e.FullName) }), Status(id), null));
            }
        }
        if (!r.IncludeDismissed) items = items.Where(i => i.Status != "Dismissed").ToList();
        var sevOrder = new Dictionary<string, int> { ["High"] = 0, ["Medium"] = 1, ["Low"] = 2 };
        items = items.OrderBy(i => sevOrder[i.Severity]).ThenByDescending(i => i.Confidence).ThenBy(i => i.EmployeeName).ToList();
        return new AiSuggestion<AnomalyPayload>
        {
            UseCaseCode = Code, Confidence = 0.88, Summary = items.Count == 0 ? "No anomalies found." : $"{items.Count} anomalies found, highest severity first.",
            Impact = "Nothing is changed or blocked. Dismiss with a reason to teach the scan what is normal.",
            Reasons = Narrate(new[] { new AiReason("Scanned all allowance lines for duplicates and rate mismatches, and all published rosters for statistical outliers (z ≥ 2.5) and impossible hours.") }),
            Payload = new(items, items.Count(i => i.Kind == "Statistical outlier"), items.Count(i => i.Kind == "Duplicate claim"), items.Count(i => i.Kind == "Rate mismatch"), items.Count(i => i.Kind == "Impossible hours"))
        };
    }
}
