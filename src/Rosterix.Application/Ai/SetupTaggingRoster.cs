using Rosterix.Application.Data;
using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Application.Ai;

// ---------------------------------------------------------------- AI-UC-01
public sealed record ProjectSetupRequest(Guid BusinessUnitId, Guid PmId);
public sealed record ProjectSetupPayload(List<Guid> ShiftTypeIds, List<string> ShiftTypeNames, int DurationMonths, string CostCentre, bool IsShiftProject, string SuggestedNamePrefix);

public sealed class ProjectSetupAi : AiServiceBase, IAiUseCase<ProjectSetupRequest, ProjectSetupPayload>
{
    public ProjectSetupAi(IAiDataSource s, IAiProvider p) : base(s, p) { }
    public string Code => "AI-UC-01";
    public async Task<AiSuggestion<ProjectSetupPayload>> SuggestAsync(ProjectSetupRequest r, CancellationToken ct = default)
    {
        var ds = await Source.LoadAsync(ct);
        var mine = ds.Projects.Where(p => p.BusinessUnitId == r.BusinessUnitId && p.OwnerPmId == r.PmId && p.Status != ProjectStatus.Draft).OrderByDescending(p => p.StartDate).Take(3).ToList();
        var basis = mine.Count >= 2 ? mine : ds.Projects.Where(p => p.BusinessUnitId == r.BusinessUnitId && p.Status != ProjectStatus.Draft).OrderByDescending(p => p.StartDate).Take(3).ToList();
        var reasons = new List<AiReason>();
        if (basis.Count == 0)
            return new AiSuggestion<ProjectSetupPayload> { UseCaseCode = Code, Confidence = 0.2, Summary = "No earlier projects in this business unit, so there is nothing to pre-fill from.", Impact = "Nothing is filled in.",
                Reasons = Narrate(new[] { new AiReason("No project history exists for this business unit yet.") }), Payload = new(new(), new(), 12, "", true, "") };
        var freq = basis.SelectMany(p => p.ShiftTypeIds).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var chosen = freq.Where(kv => kv.Value * 2 >= basis.Count).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        if (chosen.Count == 0) chosen = freq.OrderByDescending(kv => kv.Value).Take(2).Select(kv => kv.Key).ToList();
        var names = chosen.Select(c => ds.Shift(c).Name).ToList();
        var durations = basis.Where(p => p.EndDate != null).Select(p => (double)((p.EndDate!.Value.Year - p.StartDate.Year) * 12 + p.EndDate.Value.Month - p.StartDate.Month)).Where(x => x > 0).ToList();
        var months = durations.Count > 0 ? (int)Math.Round(AiScoring.Median(durations)) : 12;
        var cc = basis.GroupBy(p => p.CostCentre[..Math.Min(4, p.CostCentre.Length)]).OrderByDescending(g => g.Count()).First();
        var nextCc = cc.Key + (basis.Max(p => int.TryParse(p.CostCentre[^4..], out var n) ? n : 0) + 1);
        var consistency = chosen.Count == 0 ? 0 : chosen.Average(c => freq[c] / (double)basis.Count);
        reasons.Add(new AiReason($"{freq.Where(kv => chosen.Contains(kv.Key)).Count()} shift type(s) ({string.Join(", ", names)}) appear in at least half of the last {basis.Count} project(s) in this business unit.", "project", basis[0].Id.ToString(), basis[0].Name));
        reasons.Add(new AiReason(durations.Count > 0 ? $"Comparable projects ran for a median of {months} months." : "No comparable project had an end date, so a 12-month default was used.", "project", basis[^1].Id.ToString(), basis[^1].Name));
        reasons.Add(new AiReason($"Cost centre follows the {cc.Key}xxxx series used by your last project.", "project", basis[0].Id.ToString(), basis[0].CostCentre));
        return new AiSuggestion<ProjectSetupPayload>
        {
            UseCaseCode = Code, Confidence = AiScoring.Clamp(0.35 + 0.5 * consistency * Math.Min(1, basis.Count / 3.0)),
            Summary = $"Pre-filled from your last {basis.Count} project(s): {string.Join(" + ", names)} shifts, {months} months.",
            Impact = "Fills shift types, duration and cost centre in the form. You can change any field before saving.",
            Reasons = Narrate(reasons), Payload = new(chosen, names, months, nextCc, chosen.Count > 0, "")
        };
    }
}

// ---------------------------------------------------------------- AI-UC-02
public sealed record TaggingRequest(Guid ProjectId, DateOnly From, DateOnly? To, int Limit = 10);
public sealed record TagCandidate(Guid EmployeeId, string Name, string Grade, string EmployeeNumber, double Score, ConfidenceBand Band, List<AiReason> Reasons, List<string> Cautions);
public sealed record TaggingPayload(List<TagCandidate> Candidates, int Considered, int Excluded);

public sealed class EmployeeTaggingAi : AiServiceBase, IAiUseCase<TaggingRequest, TaggingPayload>
{
    private readonly IRuleEngine _rules;
    public EmployeeTaggingAi(IAiDataSource s, IAiProvider p, IRuleEngine rules) : base(s, p) { _rules = rules; }
    public string Code => "AI-UC-02";
    public async Task<AiSuggestion<TaggingPayload>> SuggestAsync(TaggingRequest r, CancellationToken ct = default)
    {
        var ds = await Source.LoadAsync(ct);
        var proj = ds.Proj(r.ProjectId);
        var current = ds.EmployeeTags.Where(t => t.ProjectId == proj.Id && t.Status == TagStatus.Approved && t.Covers(r.From)).Select(t => t.EmployeeId).ToHashSet();
        var team = ds.Employees.Where(e => current.Contains(e.Id)).ToList();
        var teamGrade = team.GroupBy(e => e.Grade).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "G3";
        var projShifts = proj.ShiftTypeIds.ToHashSet();
        var candidates = new List<TagCandidate>(); int excluded = 0, considered = 0;
        var rosterProject = ds.Rosters.ToDictionary(x => x.Id, x => x.ProjectId);
        var shiftHistory = ds.RosterEntries.Where(e => e.EntryType == EntryType.Shift && e.ShiftTypeId != null).GroupBy(e => e.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Where(e => projShifts.Contains(e.ShiftTypeId!.Value)).Count() / (double)Math.Max(1, g.Count()));
        foreach (var e in ds.Employees.Where(e => e.IsActive && e.IsShiftEligible && e.ExitedOn == null && e.BusinessUnitId == proj.BusinessUnitId && !current.Contains(e.Id)))
        {
            considered++;
            var overlap = ds.EmployeeTags.Any(t => t.EmployeeId == e.Id && t.ProjectId != proj.Id && t.Status is TagStatus.Approved or TagStatus.PendingApproval or TagStatus.Backtagged
                && (t.ValidTo == null || t.ValidTo >= r.From) && (r.To == null || t.ValidFrom <= r.To));
            if (overlap) { excluded++; continue; }
            var reasons = new List<AiReason>(); var cautions = new List<string>(); double score = 0.3;
            var prior = ds.EmployeeTags.Where(t => t.EmployeeId == e.Id && t.ProjectId == proj.Id && t.Status == TagStatus.Approved).OrderByDescending(t => t.ValidFrom).FirstOrDefault();
            if (prior != null) { score += 0.2; reasons.Add(new AiReason($"Worked on {proj.Name} before ({prior.ValidFrom:MMM yyyy}).", "project", proj.Id.ToString(), proj.Name)); }
            var past = ds.EmployeeTags.Where(t => t.EmployeeId == e.Id && t.Status == TagStatus.Approved && t.ProjectId != proj.Id && ds.Proj(t.ProjectId).OwnerPmId == proj.OwnerPmId).FirstOrDefault();
            if (past != null && prior == null) { score += 0.12; reasons.Add(new AiReason($"Has worked for the same project manager on {ds.Proj(past.ProjectId).Name}.", "project", past.ProjectId.ToString(), ds.Proj(past.ProjectId).Name)); }
            var leaves = ds.Leaves.Count(l => l.EmployeeId == e.Id && l.Date >= r.From && l.Date <= (r.To ?? r.From.AddDays(30)));
            if (leaves == 0) { score += 0.12; reasons.Add(new AiReason("No approved leave in the first 30 days of the window.", "employee", e.Id.ToString(), e.FullName)); } else { score -= 0.03 * leaves; cautions.Add($"{leaves} approved leave day(s) in the window."); }
            if (e.Grade == teamGrade) { score += 0.1; reasons.Add(new AiReason($"Grade {e.Grade} matches most of the current team.", "employee", e.Id.ToString(), e.FullName)); } else cautions.Add($"Grade {e.Grade} differs from the team's usual {teamGrade}.");
            if (shiftHistory.TryGetValue(e.Id, out var sh) && sh > 0.25) { score += 0.2 * Math.Min(1, sh); reasons.Add(new AiReason($"{sh:P0} of their past shifts match this project's shift types.", "employee", e.Id.ToString(), e.FullName)); }
            var pref = ds.Preferences.FirstOrDefault(x => x.EmployeeId == e.Id);
            if (pref?.PreferredShiftTypeId is { } ps && projShifts.Contains(ps)) { score += 0.1; reasons.Add(new AiReason($"Prefers {ds.Shift(ps).Name} shifts, which this project uses.", "employee", e.Id.ToString(), e.FullName)); }
            reasons.Add(new AiReason("Shift-eligible with no overlapping tag in the window.", "employee", e.Id.ToString(), e.FullName));
            candidates.Add(new TagCandidate(e.Id, e.FullName, e.Grade, e.EmployeeNumber, AiScoring.Clamp(score), AiScoring.Band(AiScoring.Clamp(score)), Narrate(reasons), cautions));
        }
        var top = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Name).Take(r.Limit).ToList();
        var conf = top.Count == 0 ? 0.2 : AiScoring.Clamp(top.Average(c => c.Score));
        return new AiSuggestion<TaggingPayload>
        {
            UseCaseCode = Code, Confidence = conf,
            Summary = top.Count == 0 ? "No eligible, available employees found for this window." : $"{top.Count} people look like a good fit for {proj.Name}.",
            Impact = "Selecting a person only adds them to the draft tag list. Nothing is saved until you submit for approval.",
            Reasons = Narrate(new[] { new AiReason($"Looked at {considered} shift-eligible people in the business unit; {excluded} were excluded because they already hold an overlapping tag."), new AiReason($"Ranked by prior tag, availability, grade fit and shift history; top {top.Count} shown.") }),
            Payload = new(top, considered, excluded)
        };
    }
}

// ---------------------------------------------------------------- AI-UC-08
public sealed record PreferenceRequest(Guid EmployeeId);
public sealed record ShiftAffinity(Guid ShiftTypeId, string Code, string Name, int Count, double Share);
public sealed record PreferencePayload(Guid EmployeeId, List<ShiftAffinity> Affinity, DayOfWeek? LearnedOffDay, Guid? OverrideShiftTypeId, DayOfWeek? OverrideOffDay, Guid? EffectiveShiftTypeId, DayOfWeek? EffectiveOffDay, string? OverrideNote, int SampleDays);

public sealed class PreferenceLearningAi : AiServiceBase, IAiUseCase<PreferenceRequest, PreferencePayload>
{
    public PreferenceLearningAi(IAiDataSource s, IAiProvider p) : base(s, p) { }
    public string Code => "AI-UC-08";
    public static (List<ShiftAffinity> affinity, DayOfWeek? off, int days) Learn(Dataset ds, Guid employeeId, int months = 3)
    {
        var published = ds.Rosters.Where(r => r.Status == RosterStatus.Published).OrderByDescending(r => r.PeriodMonth).Select(r => r.PeriodMonth).Distinct().Take(months).ToHashSet();
        var rosterIds = ds.Rosters.Where(r => r.Status == RosterStatus.Published && published.Contains(r.PeriodMonth)).Select(r => r.Id).ToHashSet();
        var mine = ds.RosterEntries.Where(e => e.EmployeeId == employeeId && rosterIds.Contains(e.RosterId)).ToList();
        var shifts = mine.Where(e => e.EntryType == EntryType.Shift && e.ShiftTypeId != null).GroupBy(e => e.ShiftTypeId!.Value).Select(g => (Id: g.Key, N: g.Count())).OrderByDescending(x => x.N).ToList();
        var total = Math.Max(1, shifts.Sum(x => x.N));
        var aff = shifts.Select(x => new ShiftAffinity(x.Id, ds.Shift(x.Id).Code, ds.Shift(x.Id).Name, x.N, Math.Round(x.N / (double)total, 2))).ToList();
        var off = mine.Where(e => e.EntryType == EntryType.WeeklyOff).GroupBy(e => e.WorkDate.DayOfWeek).OrderByDescending(g => g.Count()).Select(g => (DayOfWeek?)g.Key).FirstOrDefault();
        return (aff, off, mine.Count);
    }
    public async Task<AiSuggestion<PreferencePayload>> SuggestAsync(PreferenceRequest r, CancellationToken ct = default)
    {
        var ds = await Source.LoadAsync(ct);
        var (aff, off, days) = Learn(ds, r.EmployeeId);
        var pref = ds.Preferences.FirstOrDefault(p => p.EmployeeId == r.EmployeeId);
        var eff = pref?.PreferredShiftTypeId ?? aff.FirstOrDefault()?.ShiftTypeId; var effOff = pref?.PreferredOffDay ?? off;
        var reasons = new List<AiReason>();
        if (aff.Count > 0) reasons.Add(new AiReason($"{aff[0].Share:P0} of published shifts in the last 3 months were {aff[0].Name}.", "employee", r.EmployeeId.ToString(), Name(ds, r.EmployeeId)));
        if (off != null) reasons.Add(new AiReason($"Weekly off has most often been {off}.", "employee", r.EmployeeId.ToString(), Name(ds, r.EmployeeId)));
        if (pref != null) reasons.Add(new AiReason("An editable override on the employee record takes priority over what was learned.", "preference", pref.Id.ToString(), "Employee preference"));
        if (reasons.Count == 0) reasons.Add(new AiReason("No published roster history yet, so nothing has been learned."));
        return new AiSuggestion<PreferencePayload>
        {
            UseCaseCode = Code, Confidence = AiScoring.Clamp(0.3 + 0.6 * Math.Min(1, days / 70.0) * (aff.Count > 0 ? aff[0].Share : 0.3)),
            Summary = aff.Count == 0 ? "No pattern learned yet." : $"Usually works {aff[0].Name}{(effOff != null ? $", off on {effOff}" : "")}.",
            Impact = "Used as a starting point when drafting rosters. It never changes a roster by itself.",
            Reasons = Narrate(reasons), Payload = new(r.EmployeeId, aff, off, pref?.PreferredShiftTypeId, pref?.PreferredOffDay, eff, effOff, pref?.Note, days)
        };
    }
}

// ---------------------------------------------------------------- AI-UC-03
public sealed record RosterDraftRequest(Guid ProjectId, string Month);
public sealed record DraftEntry(Guid EmployeeId, DateOnly WorkDate, Guid? ShiftTypeId, EntryType EntryType, bool IsAiSuggested);
public sealed record DraftConflict(Guid EmployeeId, string EmployeeName, string RuleCode, RuleSeverity Severity, string Message, string? SuggestedFix);
public sealed record RosterDraftPayload(Guid ProjectId, string Month, List<DraftEntry> Entries, List<DraftConflict> Conflicts, int EmployeeCount, int HistoryMonths);

public sealed class RosterGenerationAi : AiServiceBase, IAiUseCase<RosterDraftRequest, RosterDraftPayload>
{
    private readonly IRuleEngine _rules;
    public RosterGenerationAi(IAiDataSource s, IAiProvider p, IRuleEngine rules) : base(s, p) { _rules = rules; }
    public string Code => "AI-UC-03";
    public async Task<AiSuggestion<RosterDraftPayload>> SuggestAsync(RosterDraftRequest r, CancellationToken ct = default)
    {
        var ds = await Source.LoadAsync(ct);
        var proj = ds.Proj(r.ProjectId);
        var from = ShiftMath.MonthStart(r.Month); var to = ShiftMath.MonthEnd(r.Month);
        var hist = ds.Rosters.Where(x => x.ProjectId == r.ProjectId && x.Status == RosterStatus.Published && string.CompareOrdinal(x.PeriodMonth, r.Month) < 0).OrderByDescending(x => x.PeriodMonth).Take(3).ToList();
        var histIds = hist.Select(h => h.Id).ToHashSet();
        var team = ds.EmployeeTags.Where(t => t.ProjectId == r.ProjectId && t.Status is TagStatus.Approved or TagStatus.Backtagged && t.ValidFrom <= to && (t.ValidTo == null || t.ValidTo >= from)).Select(t => t.EmployeeId).Distinct().Select(ds.Emp).ToList();
        var restricted = ds.Holidays.Where(h => h.IsRestricted).Select(h => (h.RegionId, h.Date)).ToHashSet();
        var entries = new List<DraftEntry>(); var conflicts = new List<DraftConflict>(); var reasons = new List<AiReason>();
        var projShift = proj.ShiftTypeIds.ToList(); int usedPrefs = 0;
        foreach (var e in team)
        {
            var mine = ds.RosterEntries.Where(x => x.EmployeeId == e.Id && histIds.Contains(x.RosterId)).ToList();
            var modal = mine.Where(x => x.EntryType == EntryType.Shift && x.ShiftTypeId != null).GroupBy(x => x.ShiftTypeId!.Value).OrderByDescending(g => g.Count()).Select(g => (Guid?)g.Key).FirstOrDefault();
            var off = mine.Where(x => x.EntryType == EntryType.WeeklyOff).GroupBy(x => x.WorkDate.DayOfWeek).OrderByDescending(g => g.Count()).Select(g => (DayOfWeek?)g.Key).FirstOrDefault();
            var pref = ds.Preferences.FirstOrDefault(p => p.EmployeeId == e.Id);
            if (pref?.PreferredShiftTypeId != null && projShift.Contains(pref.PreferredShiftTypeId.Value)) { modal = pref.PreferredShiftTypeId; usedPrefs++; }
            if (pref?.PreferredOffDay != null) off = pref.PreferredOffDay;
            modal ??= projShift.FirstOrDefault(); off ??= DayOfWeek.Sunday;
            var regionId = ds.BU(e.BusinessUnitId).RegionId;
            var leave = ds.Leaves.Where(l => l.EmployeeId == e.Id && l.Approved).Select(l => l.Date).ToHashSet();
            var empEntries = new List<RosterEntry>();
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var tagged = ds.EmployeeTags.Any(t => t.EmployeeId == e.Id && t.ProjectId == r.ProjectId && t.Status is TagStatus.Approved or TagStatus.Backtagged && t.Covers(d));
                if (!tagged) continue;
                EntryType et = restricted.Contains((regionId, d)) ? EntryType.Holiday : leave.Contains(d) ? EntryType.Leave : d.DayOfWeek == off ? EntryType.WeeklyOff : EntryType.Shift;
                var sid = et == EntryType.Shift ? modal : null;
                entries.Add(new DraftEntry(e.Id, d, sid, et, true));
                empEntries.Add(new RosterEntry { EmployeeId = e.Id, WorkDate = d, ShiftTypeId = sid, EntryType = et });
            }
            var ctx = RuleContextFactory.For(ds, e.Id, r.ProjectId, r.Month, empEntries);
            foreach (var v in _rules.Evaluate(ctx).Where(v => v.RuleCode is not ("BR-10" or "BR-11" or "BR-12")))
                conflicts.Add(new DraftConflict(e.Id, e.FullName, v.RuleCode, v.Severity, v.Message, v.SuggestedFix));
        }
        if (hist.Count > 0) reasons.Add(new AiReason($"Each person's most common shift and weekly off were taken from the last {hist.Count} published roster(s): {string.Join(", ", hist.Select(h => h.PeriodMonth))}.", "roster", hist[0].Id.ToString(), $"{proj.Name} {hist[0].PeriodMonth}"));
        else reasons.Add(new AiReason("There is no published history for this project, so every person got the project's first shift type and a Sunday off."));
        reasons.Add(new AiReason($"The rule engine checked {team.Count} people; {conflicts.Count} conflict(s) remain for you to resolve before publishing."));
        if (usedPrefs > 0) reasons.Add(new AiReason($"{usedPrefs} employee preference(s) set on the employee record were applied first."));
        var blocks = conflicts.Count(c => c.Severity == RuleSeverity.Block);
        var conf = AiScoring.Clamp(0.45 + 0.15 * hist.Count - Math.Min(0.35, blocks * 0.05));
        return new AiSuggestion<RosterDraftPayload>
        {
            UseCaseCode = Code, Confidence = hist.Count == 0 ? Math.Min(conf, 0.4) : conf,
            Summary = $"Draft roster for {r.Month}: {team.Count} people, {entries.Count(e => e.EntryType == EntryType.Shift)} shifts, {conflicts.Count} conflict(s) to review.",
            Impact = "Creates a Draft roster only when you accept it. It is never published automatically; you publish it separately.",
            Reasons = Narrate(reasons), Payload = new(r.ProjectId, r.Month, entries, conflicts, team.Count, hist.Count)
        };
    }
}
