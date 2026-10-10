using FluentAssertions;
using Rosterix.Application.Ai;
using Rosterix.Application.Allowance;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;
using Rosterix.Domain.Rules;
using Xunit;

namespace Rosterix.Domain.Tests;

public sealed class SnapshotSource : IAiDataSource
{
    public Dataset Data { get; }
    public SnapshotSource(Dataset d) => Data = d;
    public Task<Dataset> LoadAsync(CancellationToken ct = default) => Task.FromResult(Data);
}

public class ShiftMathTests
{
    [Theory]
    [InlineData("06:00", "14:00", false, 8)]
    [InlineData("22:00", "06:00", true, 8)]
    [InlineData("16:00", "00:00", true, 8)]
    [InlineData("21:00", "05:00", true, 8)]
    [InlineData("08:00", "20:00", false, 12)]
    public void Duration_handles_midnight_span(string s, string e, bool spans, double hours) =>
        ShiftMath.DurationHours(TimeOnly.Parse(s), TimeOnly.Parse(e), spans).Should().Be(hours);
    [Fact] public void Midnight_shift_ends_next_day() => ShiftMath.EndAt(new DateOnly(2026, 9, 1), new(22, 0), new(6, 0), true).Should().Be(new DateTime(2026, 9, 2, 6, 0, 0));
    [Fact] public void Month_helpers() { ShiftMath.MonthEnd("2026-02").Should().Be(new DateOnly(2026, 2, 28)); ShiftMath.WeekStart(new DateOnly(2026, 9, 6)).Should().Be(new DateOnly(2026, 8, 31)); }
    [Fact] public void Arrear_age_buckets()
    {
        var today = new DateOnly(2026, 10, 10);
        new Arrear { ForPeriodMonth = "2026-09" }.AgeBucket(today).Should().Be("0-3");
        new Arrear { ForPeriodMonth = "2026-05" }.AgeBucket(today).Should().Be("4-6");
        new Arrear { ForPeriodMonth = "2025-12" }.AgeBucket(today).Should().Be(">6");
    }
}

public class SeedTests
{
    static readonly Dataset Ds = CanonicalSeed.Build();
    [Fact] public void Seed_is_deterministic() { var a = CanonicalSeed.Build(); a.Employees.Select(e => e.Id).Should().Equal(Ds.Employees.Select(e => e.Id)); a.Lines.Sum(l => l.Amount).Should().Be(Ds.Lines.Sum(l => l.Amount)); }
    [Fact] public void Seed_matches_the_brief()
    {
        Ds.Regions.Should().HaveCount(2); Ds.BusinessUnits.Should().HaveCount(4); Ds.ShiftTypes.Should().HaveCount(12); Ds.ShiftRules.Should().HaveCount(12);
        Ds.Employees.Count.Should().BeInRange(110, 140); Ds.Projects.Should().HaveCount(10);
        Ds.Projects.Count(p => p.IsShiftProject && p.Status == ProjectStatus.Active).Should().Be(7);
        Ds.Projects.Count(p => p.Status == ProjectStatus.PendingApproval).Should().Be(1); Ds.Projects.Count(p => p.Status == ProjectStatus.Closed).Should().Be(1); Ds.Projects.Count(p => p.Status == ProjectStatus.Draft).Should().Be(1);
        Ds.Runs.Should().HaveCount(3); Ds.Exceptions.Count.Should().BeInRange(24, 30); Ds.Arrears.Should().HaveCount(8);
        Ds.ApprovalRequests.Count(a => a.Status == ApprovalStatus.Pending).Should().Be(15);
        Ds.Delegations.Should().HaveCount(2); Ds.Settlements.Should().HaveCount(1); Ds.Rates.Count.Should().BeInRange(18, 24);
        Ds.Arrears.Select(a => a.AgeBucket(Ds.Today)).Distinct().Should().BeEquivalentTo("0-3", "4-6", ">6");
        Ds.ApprovalRequests.Where(a => a.Status == ApprovalStatus.Pending).Select(a => a.Type).Distinct().Should().HaveCountGreaterThan(4);
        Ds.Rosters.Count(r => r.Status == RosterStatus.Published).Should().Be(12); Ds.Rosters.Count(r => r.Status == RosterStatus.Draft).Should().Be(3);
    }
    [Fact] public void Six_personas_have_fixed_ids_and_roles()
    {
        Ds.Emp(Personas.Priya).Roles.Should().HaveFlag(Role.PM); Ds.Emp(Personas.Arjun).Roles.Should().HaveFlag(Role.LM); Ds.Emp(Personas.Meera).Roles.Should().HaveFlag(Role.HRBP);
        Ds.Emp(Personas.Karthik).Roles.Should().HaveFlag(Role.RH); Ds.Emp(Personas.Fathima).Roles.Should().HaveFlag(Role.HRSS); Ds.Emp(Personas.Ravi).Roles.Should().Be(Role.EMP);
    }
    [Fact] public void Night_A_rate_change_proves_effective_dating()
    {
        var calc = new AllowanceCalculator(new RuleEngine());
        var ret = Ds.Projects.First(p => p.Code == "P-RET-01");
        var jul = calc.Calculate(Ds, ret.Id, "2026-07").Lines.Where(l => Ds.Shift(l.ShiftTypeId).Code == "NIGHT_A" && Ds.Emp(l.EmployeeId).Grade != "G2").Select(l => l.Rate).Distinct();
        var aug = calc.Calculate(Ds, ret.Id, "2026-08").Lines.Where(l => Ds.Shift(l.ShiftTypeId).Code == "NIGHT_A" && Ds.Emp(l.EmployeeId).Grade != "G2").Select(l => l.Rate).Distinct();
        jul.Should().Equal(450m); aug.Should().Equal(525m);
    }
    [Fact] public void Published_history_is_rule_compliant() { var ai = new ConstraintValidationAi(new SnapshotSource(Ds), new HeuristicAiProvider(), new RuleEngine()); var ret = Ds.Projects.First(p => p.Code == "P-RET-01"); foreach (var m in new[] { "2026-06", "2026-07", "2026-08" }) ai.Evaluate(Ds, new ValidationRequest(ret.Id, m, new())).Payload.Violations.Should().BeEmpty(m); }
}

public class AllowanceCalculatorTests
{
    [Fact] public void Lines_carry_a_plain_language_trace()
    {
        var ds = CanonicalSeed.Build(); var ret = ds.Projects.First(p => p.Code == "P-RET-01");
        var res = new AllowanceCalculator(new RuleEngine()).Calculate(ds, ret.Id, "2026-08");
        res.Lines.Should().NotBeEmpty(); res.Lines.Should().OnlyContain(l => l.Trace.Contains("allowance-eligible") && l.Amount == l.Rate);
        res.Lines.Select(l => ds.Shift(l.ShiftTypeId).IsAllowanceEligible).Should().OnlyContain(x => x);
    }
    [Fact] public void Missing_rate_becomes_a_data_gap_exception_not_a_silent_zero()
    {
        var ds = CanonicalSeed.Build(); var ret = ds.Projects.First(p => p.Code == "P-RET-01");
        ds.Rates.RemoveAll(r => ds.Shift(r.ShiftTypeId).Code == "EVE");
        var res = new AllowanceCalculator(new RuleEngine()).Calculate(ds, ret.Id, "2026-08");
        res.Exceptions.Should().Contain(e => e.Category == ExceptionCategory.DataGap && e.RuleCode == "BR-10");
    }
    [Fact] public void Same_rule_engine_flags_roster_problems_in_the_batch_calculator()
    {
        var ds = CanonicalSeed.Build(); var ret = ds.Projects.First(p => p.Code == "P-RET-01");
        var res = new AllowanceCalculator(new RuleEngine()).Calculate(ds, ret.Id, "2026-10");
        res.Exceptions.Select(e => e.RuleCode).Should().Contain(new[] { "BR-02", "BR-03" });
    }
}

public class DelegationValidatorTests
{
    static readonly Dataset Ds = CanonicalSeed.Build();
    [Fact] public void Peer_with_same_role_and_grade_is_allowed() => DelegationValidator.Validate(Ds, Personas.Arjun, Personas.Gid("emp:nikhil")).Allowed.Should().BeTrue();
    [Fact] public void Subordinate_is_refused() => DelegationValidator.Validate(Ds, Personas.Priya, Ds.Employees.First(e => e.FirstName == "Deepa").Id).Violations.Single().Message.Should().Contain("reports to you");
    [Fact] public void Different_grade_is_refused() => DelegationValidator.Validate(Ds, Personas.Arjun, Personas.Gid("emp:sunita")).Violations.Single().Message.Should().Contain("grade");
    [Fact] public void Different_role_is_refused() => DelegationValidator.Validate(Ds, Personas.Arjun, Personas.Priya).Allowed.Should().BeFalse();
    [Fact] public void Self_delegation_is_refused() => DelegationValidator.Validate(Ds, Personas.Arjun, Personas.Arjun).Allowed.Should().BeFalse();
}

public class PolicyTests
{
    [Fact] public void Compensation_is_hrss_only() { Policies.Satisfies(Policies.CanApproveCompensation, Role.HRSS).Should().BeTrue(); foreach (var r in new[] { Role.PM, Role.LM, Role.HRBP, Role.RH, Role.EMP }) Policies.Satisfies(Policies.CanApproveCompensation, r).Should().BeFalse(r.ToString()); }
    [Fact] public void Policy_table_matches_the_brief()
    {
        Policies.Map[Policies.CanApproveProjectTagging].Should().Be(Role.LM | Role.HRBP | Role.RH);
        Policies.Map[Policies.CanApproveEmployeeTagging].Should().Be(Role.LM | Role.HRBP);
        Policies.Map[Policies.CanManageFoundationData].Should().Be(Role.HRSS); Policies.Map[Policies.CanSubmitPayroll].Should().Be(Role.HRSS);
        Policies.Map[Policies.CanDelegate].Should().Be(Role.PM | Role.LM); Policies.Map[Policies.CanBuildRoster].Should().Be(Role.PM | Role.LM | Role.HRSS);
    }
}

public class AiServiceTests
{
    static readonly Dataset Ds = CanonicalSeed.Build();
    static readonly SnapshotSource Src = new(Ds);
    static readonly IAiProvider P = new HeuristicAiProvider();
    static readonly IRuleEngine E = new RuleEngine();
    static Project Ret1 => Ds.Projects.First(p => p.Code == "P-RET-01");

    static void Contract<T>(AiSuggestion<T> s)
    {
        s.Reversible.Should().BeTrue(); s.Confidence.Should().BeInRange(0, 1); s.Summary.Should().NotBeNullOrWhiteSpace(); s.Impact.Should().NotBeNullOrWhiteSpace();
        s.Reasons.Count.Should().BeGreaterThanOrEqualTo(1);
        s.ConfidenceBand.Should().Be(s.Confidence >= 0.8 ? ConfidenceBand.High : s.Confidence >= 0.5 ? ConfidenceBand.Medium : ConfidenceBand.Low);
    }

    [Fact] public async Task UC01_prefills_from_last_projects()
    {
        var s = await new ProjectSetupAi(Src, P).SuggestAsync(new(Ret1.BusinessUnitId, Personas.Priya)); Contract(s);
        s.Reasons.Count.Should().BeGreaterThanOrEqualTo(2); s.Payload.ShiftTypeIds.Should().NotBeEmpty(); s.Payload.CostCentre.Should().StartWith("CC-");
    }
    [Fact] public async Task UC01_admits_when_there_is_no_history()
    {
        var empty = CanonicalSeed.Build(); empty.Projects.Clear();
        var s = await new ProjectSetupAi(new SnapshotSource(empty), P).SuggestAsync(new(Ret1.BusinessUnitId, Personas.Priya)); s.ConfidenceBand.Should().Be(ConfidenceBand.Low);
    }
    [Fact] public async Task UC02_ranks_candidates_with_reasons_and_excludes_overlaps()
    {
        var pilot = Ds.Projects.First(p => p.Code == "P-RET-03");
        var s = await new EmployeeTaggingAi(Src, P, E).SuggestAsync(new(pilot.Id, new DateOnly(2026, 11, 1), null)); Contract(s);
        s.Payload.Candidates.Should().NotBeEmpty().And.HaveCountLessOrEqualTo(10);
        s.Payload.Candidates.Should().BeInDescendingOrder(c => c.Score); s.Payload.Candidates.Should().OnlyContain(c => c.Reasons.Count >= 2);
        s.Payload.Excluded.Should().BeGreaterThan(0);
        var taken = Ds.EmployeeTags.Where(t => t.Status == TagStatus.Approved && t.ValidTo == null).Select(t => t.EmployeeId).ToHashSet();
        s.Payload.Candidates.Should().OnlyContain(c => !taken.Contains(c.EmployeeId));
    }
    [Fact] public async Task UC03_drafts_a_roster_that_self_reports_conflicts()
    {
        var s = await new RosterGenerationAi(Src, P, E).SuggestAsync(new(Ret1.Id, "2026-11")); Contract(s);
        s.Payload.Entries.Should().NotBeEmpty().And.OnlyContain(e => e.IsAiSuggested); s.Payload.HistoryMonths.Should().Be(3); s.Reasons.Count.Should().BeGreaterThanOrEqualTo(2);
        s.Payload.Entries.Where(e => e.EntryType == EntryType.WeeklyOff).Should().NotBeEmpty();
        s.Impact.Should().Contain("never published");
    }
    [Fact] public async Task UC03_without_history_is_honestly_low_confidence()
    {
        var ds = CanonicalSeed.Build(); var pilot = ds.Projects.First(p => p.Code == "P-RET-03");
        var s = await new RosterGenerationAi(new SnapshotSource(ds), P, E).SuggestAsync(new(ret1Id(ds), "2026-06")); s.Payload.HistoryMonths.Should().Be(0); s.Confidence.Should().BeLessOrEqualTo(0.4);
        static Guid ret1Id(Dataset d) => d.Projects.First(p => p.Code == "P-RET-01").Id;
    }
    [Fact] public async Task UC04_finds_planted_october_conflicts_and_near_misses()
    {
        var s = await new ConstraintValidationAi(Src, P, E).SuggestAsync(new(Ret1.Id, "2026-10", new())); Contract(s);
        s.Payload.Violations.Select(v => v.RuleCode).Should().Contain(new[] { "BR-02", "BR-03", "BR-06", "BR-09" }); s.Payload.HasBlock.Should().BeTrue(); s.Payload.NearMisses.Should().NotBeEmpty();
    }
    [Fact] public async Task UC04_edit_that_creates_a_conflict_is_detected_before_saving()
    {
        var emp = Ds.EmployeeTags.First(t => t.ProjectId == Ret1.Id && t.Status == TagStatus.Approved).EmployeeId;
        var morn = Ds.ShiftTypes.First(x => x.Code == "MORN").Id;
        var edits = Enumerable.Range(1, 30).Select(d => new EntryEdit(emp, new DateOnly(2026, 11, d), morn, EntryType.Shift)).ToList();
        var r = await new ConstraintValidationAi(Src, P, E).SuggestAsync(new(Ret1.Id, "2026-11", edits)); r.Payload.Violations.Should().Contain(v => v.RuleCode == "BR-02" && v.Severity == RuleSeverity.Block);
    }
    [Fact] public async Task UC05_forecasts_cost_with_drivers_and_history()
    {
        var s = await new CostPredictionAi(Src, P, new AllowanceCalculator(E)).SuggestAsync(new(Ret1.Id, "2026-10")); Contract(s);
        s.Payload.Forecast.Should().BeGreaterThan(0); s.Payload.History.Should().HaveCount(3); s.Payload.Drivers.Should().NotBeEmpty();
    }
    [Fact] public async Task UC05_explains_my_allowance_line_by_line()
    {
        var s = await new MyAllowanceExplainer(Src, P, new AllowanceCalculator(E)).SuggestAsync(new(Personas.Ravi, "2026-09")); Contract(s);
        s.Payload.Lines.Should().NotBeEmpty(); s.Payload.Lines.Should().OnlyContain(l => l.Trace.Length > 40); s.Payload.Total.Should().Be(s.Payload.Lines.Sum(l => l.Amount));
    }
    [Fact] public async Task UC06_shows_a_ripple_before_a_backdated_movement()
    {
        var mv = Ds.Movements.First(m => m.Status == MovementStatus.Staged);
        var s = await new ConflictRippleAi(Src, P, E).SuggestAsync(new(mv.EmployeeId, mv.FromProjectId, mv.ToProjectId, mv.EffectiveFrom, null)); Contract(s);
        s.Payload.Rows.Select(r => r.EntityType).Should().Contain(new[] { "Tag", "Roster entries" }); s.Payload.Rows.Should().Contain(r => r.Change == "Created"); s.Impact.Should().Contain("preview");
    }
    [Fact] public async Task UC06_flags_blocking_overlap_with_an_approved_tag_elsewhere()
    {
        var other = Ds.EmployeeTags.First(t => t.Status == TagStatus.Approved && t.ProjectId != Ret1.Id && t.ValidTo == null && Ds.Proj(t.ProjectId).Status == ProjectStatus.Active);
        var s = await new ConflictRippleAi(Src, P, E).SuggestAsync(new(other.EmployeeId, Ds.Projects.First(p => p.Code == "P-LOG-01").Id, Ret1.Id, new DateOnly(2026, 9, 1), null));
        s.Payload.HasBlock.Should().BeTrue(); s.Payload.Violations.Select(v => v.RuleCode).Should().Contain("BR-12");
    }
    [Fact] public async Task UC07_sorts_the_queue_by_risk_with_a_real_spread()
    {
        var s = await new ApprovalRiskAi(Src, P).SuggestAsync(new()); Contract(s);
        s.Payload.Items.Should().HaveCount(15).And.BeInDescendingOrder(i => i.Score);
        s.Payload.Items.Select(i => i.Band).Distinct().Should().Contain(new[] { "High", "Low" }); s.Payload.Items.Should().OnlyContain(i => i.Headline.Length > 10);
    }
    [Fact] public async Task UC07_arrear_variant_uses_age_bucket_and_repeat_incidence()
    {
        var s = await new ApprovalRiskAi(Src, P).SuggestAsync(new());
        var arrearReqs = Ds.ApprovalRequests.Where(a => a.Type == ApprovalType.Arrear && a.Status == ApprovalStatus.Pending).Select(a => a.Id).ToHashSet();
        var feats = s.Payload.Items.Where(i => arrearReqs.Contains(i.RequestId)).SelectMany(i => i.Features.Select(f => f.Name)).ToList();
        feats.Should().Contain("Age bucket").And.Contain("Repeat incidence");
    }
    [Fact] public async Task UC08_learns_modal_shift_and_off_day_and_respects_override()
    {
        var s = await new PreferenceLearningAi(Src, P).SuggestAsync(new(Personas.Ravi)); Contract(s);
        s.Payload.Affinity.Should().NotBeEmpty(); s.Payload.LearnedOffDay.Should().NotBeNull(); s.Payload.EffectiveShiftTypeId.Should().Be(s.Payload.OverrideShiftTypeId);
        s.Payload.Affinity.Sum(a => a.Share).Should().BeApproximately(1, 0.05);
    }
    [Fact] public async Task UC09_finds_exactly_the_planted_anomalies()
    {
        var s = await new AnomalyDetectionAi(Src, P).SuggestAsync(new()); Contract(s);
        s.Payload.DuplicateCount.Should().Be(1); s.Payload.RateMismatchCount.Should().Be(1); s.Payload.OutlierCount.Should().Be(2);
        s.Payload.Items.Select(i => i.Severity == "High" ? 3 : i.Severity == "Medium" ? 2 : 1).Should().BeInDescendingOrder();
        s.Payload.Items.Where(i => i.Kind == "Statistical outlier").Should().OnlyContain(i => i.ZScore >= 2.5); s.Payload.Items.Should().OnlyContain(i => i.Evidence.Count >= 1);
    }
    [Fact] public async Task UC09_dismissed_items_are_hidden_but_recoverable()
    {
        var ds = CanonicalSeed.Build(); var svc = new AnomalyDetectionAi(new SnapshotSource(ds), P);
        var first = (await svc.SuggestAsync(new())).Payload.Items[0];
        ds.AiFeedback.Add(new AiFeedback { UseCaseCode = "AI-UC-09", ContextId = first.Id, Action = AiAction.Dismiss, Reason = "Known one-off", ActorEmployeeId = Personas.Fathima });
        (await svc.SuggestAsync(new())).Payload.Items.Should().NotContain(i => i.Id == first.Id);
        (await svc.SuggestAsync(new(true))).Payload.Items.Single(i => i.Id == first.Id).Status.Should().Be("Dismissed");
    }
    [Fact] public async Task Services_never_mutate_the_snapshot_they_read()
    {
        var ds = CanonicalSeed.Build(); var src = new SnapshotSource(ds);
        string Fingerprint() => $"{ds.RosterEntries.Count}|{ds.Lines.Count}|{ds.EmployeeTags.Count}|{ds.Rosters.Count}|{ds.ApprovalRequests.Count}|{ds.Exceptions.Count}|{ds.Arrears.Count}|{ds.Lines.Sum(l => l.Amount)}|{ds.AiFeedback.Count}";
        var before = Fingerprint(); var ret = ds.Projects.First(p => p.Code == "P-RET-01"); var calc = new AllowanceCalculator(E);
        await new ProjectSetupAi(src, P).SuggestAsync(new(ret.BusinessUnitId, Personas.Priya)); await new EmployeeTaggingAi(src, P, E).SuggestAsync(new(ret.Id, new DateOnly(2026, 11, 1), null));
        await new RosterGenerationAi(src, P, E).SuggestAsync(new(ret.Id, "2026-11")); await new ConstraintValidationAi(src, P, E).SuggestAsync(new(ret.Id, "2026-10", new()));
        await new CostPredictionAi(src, P, calc).SuggestAsync(new(ret.Id, "2026-10")); await new MyAllowanceExplainer(src, P, calc).SuggestAsync(new(Personas.Ravi, "2026-09"));
        var mv = ds.Movements.First(m => m.Status == MovementStatus.Staged); await new ConflictRippleAi(src, P, E).SuggestAsync(new(mv.EmployeeId, mv.FromProjectId, mv.ToProjectId, mv.EffectiveFrom, null));
        await new ApprovalRiskAi(src, P).SuggestAsync(new()); await new PreferenceLearningAi(src, P).SuggestAsync(new(Personas.Ravi)); await new AnomalyDetectionAi(src, P).SuggestAsync(new());
        Fingerprint().Should().Be(before);
    }
    [Fact] public void Llm_narration_provider_is_off_by_default_and_never_changes_data_points()
    {
        var p = new LlmNarrationProvider(); p.Name.Should().Contain("off");
        var r = new AiReason("x", "employee", "1", "Ravi"); p.Narrate(r).Should().Be(r);
    }
    [Theory] [InlineData(0.49, ConfidenceBand.Low)] [InlineData(0.5, ConfidenceBand.Medium)] [InlineData(0.79, ConfidenceBand.Medium)] [InlineData(0.8, ConfidenceBand.High)]
    public void Confidence_bands(double c, ConfidenceBand b) => AiScoring.Band(c).Should().Be(b);
}
