using FluentAssertions;
using Rosterix.Domain;
using Rosterix.Domain.Rules;
using Xunit;

namespace Rosterix.Domain.Tests;

internal static class B
{
    public static readonly Guid Emp = Guid.NewGuid(), ProjA = Guid.NewGuid(), ProjB = Guid.NewGuid(), Bu = Guid.NewGuid();
    public static readonly ShiftInfo Morning = new(Guid.NewGuid(), "MORN", "Morning", new(6, 0), new(14, 0), false, false);
    public static readonly ShiftInfo General = new(Guid.NewGuid(), "GEN", "General", new(9, 0), new(17, 0), false, false);
    public static readonly ShiftInfo Evening = new(Guid.NewGuid(), "EVE", "Evening", new(14, 0), new(22, 0), false, true);
    public static readonly ShiftInfo Night = new(Guid.NewGuid(), "NIGHT", "Night", new(22, 0), new(6, 0), true, true);
    public static readonly ShiftInfo Long = new(Guid.NewGuid(), "LONG", "Long", new(6, 0), new(18, 0), false, true);
    public static DateOnly D(int day) => new(2026, 9, day);

    public static RuleContext Ctx(IEnumerable<EntryInfo>? entries = null, IEnumerable<TagInfo>? tags = null, Action<RuleParameters>? p = null,
        ProjectInfo? project = null, IEnumerable<DateOnly>? leave = null, IEnumerable<DateOnly>? restricted = null, IEnumerable<RateInfo>? rates = null,
        BackdateInfo? backdate = null, DelegationInfo? del = null, IEnumerable<DateOnly>? claims = null)
    {
        var par = new RuleParameters(); p?.Invoke(par);
        return new RuleContext
        {
            EmployeeId = Emp, EmployeeName = "Ravi Kumar", Grade = "G3", BusinessUnitId = Bu, ProjectId = ProjA, Project = project ?? new ProjectInfo(ProjA, D(1), null),
            Today = new DateOnly(2026, 10, 10), PeriodStart = D(1), PeriodEnd = D(30), Entries = (entries ?? Array.Empty<EntryInfo>()).ToList(), Tags = (tags ?? new[] { new TagInfo(ProjA, D(1), null) }).ToList(),
            LeaveDays = (leave ?? Array.Empty<DateOnly>()).ToHashSet(), RestrictedHolidays = (restricted ?? Array.Empty<DateOnly>()).ToHashSet(), Rates = (rates ?? Array.Empty<RateInfo>()).ToList(),
            Parameters = par, Backdate = backdate, Delegation = del, AllowanceClaims = (claims ?? Array.Empty<DateOnly>()).ToHashSet()
        };
    }
    public static EntryInfo Shift(int day, ShiftInfo s, Guid? proj = null) => new(D(day), EntryType.Shift, s, proj ?? ProjA);
    public static IEnumerable<EntryInfo> Run(int from, int count, ShiftInfo s) => Enumerable.Range(from, count).Select(d => Shift(d, s));
}

public class RuleTests
{
    // BR-01
    [Fact] public void BR01_pass_single_project_tag() => new Br01NoOverlappingTags().Evaluate(B.Ctx(tags: new[] { new TagInfo(B.ProjA, B.D(1), null) })).Should().BeEmpty();
    [Fact] public void BR01_fail_two_projects_overlap()
    {
        var r = new Br01NoOverlappingTags().Evaluate(B.Ctx(tags: new[] { new TagInfo(B.ProjA, B.D(1), null), new TagInfo(B.ProjB, B.D(15), null) })).Single();
        r.Severity.Should().Be(RuleSeverity.Block); r.Message.Should().Contain("two projects"); r.SuggestedFix.Should().NotBeNullOrEmpty();
    }
    [Fact] public void BR01_pass_back_to_back_tags_do_not_overlap() => new Br01NoOverlappingTags().Evaluate(B.Ctx(tags: new[] { new TagInfo(B.ProjA, B.D(1), B.D(14)), new TagInfo(B.ProjB, B.D(15), null) })).Should().BeEmpty();

    // BR-02
    [Fact] public void BR02_pass_six_in_a_row() => new Br02MaxConsecutiveDays().Evaluate(B.Ctx(B.Run(1, 6, B.Morning))).Should().BeEmpty();
    [Fact] public void BR02_fail_seven_in_a_row()
    {
        var r = new Br02MaxConsecutiveDays().Evaluate(B.Ctx(B.Run(1, 7, B.Morning))).Single();
        r.Severity.Should().Be(RuleSeverity.Block); r.Message.Should().Contain("7 days in a row");
    }
    [Fact] public void BR02_parameter_is_editable() => new Br02MaxConsecutiveDays().Evaluate(B.Ctx(B.Run(1, 7, B.Morning), p: x => x.MaxConsecutiveDays = 7)).Should().BeEmpty();

    // BR-03
    [Fact] public void BR03_pass_exactly_eleven_hours() => new Br03MinRest().Evaluate(B.Ctx(new[] { B.Shift(1, B.Evening), B.Shift(2, B.General) })).Should().BeEmpty();
    [Fact] public void BR03_fail_night_then_morning()
    {
        var r = new Br03MinRest().Evaluate(B.Ctx(new[] { B.Shift(1, B.Night), B.Shift(2, B.Morning) })).Single();
        r.Severity.Should().Be(RuleSeverity.Block); r.Message.Should().Contain("0 h of rest");
    }

    // BR-04
    [Fact] public void BR04_pass_covered_by_approved_tag() => new Br04RequiresApprovedTag().Evaluate(B.Ctx(B.Run(1, 3, B.Morning))).Should().BeEmpty();
    [Fact] public void BR04_fail_no_approved_tag_on_date() => new Br04RequiresApprovedTag().Evaluate(B.Ctx(B.Run(1, 3, B.Morning), tags: new[] { new TagInfo(B.ProjA, B.D(2), null) })).Single().Severity.Should().Be(RuleSeverity.Block);
    [Fact] public void BR04_fail_pending_tag_does_not_count() => new Br04RequiresApprovedTag().Evaluate(B.Ctx(B.Run(1, 2, B.Morning), tags: new[] { new TagInfo(B.ProjA, B.D(1), null, Approved: false) })).Should().HaveCount(1);

    // BR-05
    [Fact] public void BR05_pass_inside_range() => new Br05WithinProjectRange().Evaluate(B.Ctx(B.Run(5, 3, B.Morning), project: new ProjectInfo(B.ProjA, B.D(1), B.D(30)))).Should().BeEmpty();
    [Fact] public void BR05_fail_after_project_end() => new Br05WithinProjectRange().Evaluate(B.Ctx(B.Run(5, 3, B.Morning), project: new ProjectInfo(B.ProjA, B.D(1), B.D(6)))).Single().Message.Should().Contain("outside the project");

    // BR-06
    [Fact] public void BR06_pass_normal_day() => new Br06LeaveOrHoliday().Evaluate(B.Ctx(new[] { B.Shift(3, B.Morning) }, leave: new[] { B.D(4) })).Should().BeEmpty();
    [Fact] public void BR06_warn_on_leave_day() => new Br06LeaveOrHoliday().Evaluate(B.Ctx(new[] { B.Shift(3, B.Morning) }, leave: new[] { B.D(3) })).Single().Severity.Should().Be(RuleSeverity.Warn);
    [Fact] public void BR06_warn_on_restricted_holiday() => new Br06LeaveOrHoliday().Evaluate(B.Ctx(new[] { B.Shift(3, B.Morning) }, restricted: new[] { B.D(3) })).Single().Message.Should().Contain("restricted holiday");

    // BR-07
    [Fact] public void BR07_pass_48_hours() => new Br07MaxWeeklyHours().Evaluate(B.Ctx(B.Run(7, 6, B.General))).Should().BeEmpty(); // Mon 7 Sep – Sat 12 Sep, 6×8 = 48
    [Fact] public void BR07_warn_over_48_hours()
    {
        var r = new Br07MaxWeeklyHours().Evaluate(B.Ctx(B.Run(7, 5, B.General).Concat(new[] { B.Shift(12, B.Long) }))).Single(); // 40 + 12 = 52
        r.Severity.Should().Be(RuleSeverity.Warn); r.Message.Should().Contain("52 h");
    }

    // BR-08
    [Fact] public void BR08_pass_eligible_shift_claimed() => new Br08EligibleShiftOnly().Evaluate(B.Ctx(new[] { B.Shift(3, B.Evening) }, claims: new[] { B.D(3) })).Should().BeEmpty();
    [Fact] public void BR08_fail_ineligible_shift_claimed() => new Br08EligibleShiftOnly().Evaluate(B.Ctx(new[] { B.Shift(3, B.Morning) }, claims: new[] { B.D(3) })).Single().Severity.Should().Be(RuleSeverity.Block);
    [Fact] public void BR08_ignores_unclaimed_ineligible_shifts() => new Br08EligibleShiftOnly().Evaluate(B.Ctx(new[] { B.Shift(3, B.Morning) })).Should().BeEmpty();

    // BR-09
    [Fact] public void BR09_pass_one_off_in_week() => new Br09MinWeeklyOffs().Evaluate(B.Ctx(B.Run(7, 6, B.Morning))).Should().BeEmpty();
    [Fact] public void BR09_fail_seven_days_worked() => new Br09MinWeeklyOffs().Evaluate(B.Ctx(B.Run(7, 7, B.Morning))).Single().Message.Should().Contain("0 weekly off");

    // BR-10
    private static RateInfo Rate(ShiftInfo s, DateOnly from, DateOnly? to = null, decimal amt = 150) => new(s.Id, null, null, amt, from, to);
    [Fact] public void BR10_pass_rate_effective_on_date() => new Br10RateMustExist().Evaluate(B.Ctx(new[] { B.Shift(3, B.Evening) }, rates: new[] { Rate(B.Evening, B.D(1)) })).Should().BeEmpty();
    [Fact] public void BR10_fail_rate_expired_before_date() => new Br10RateMustExist().Evaluate(B.Ctx(new[] { B.Shift(3, B.Evening) }, rates: new[] { Rate(B.Evening, B.D(1), B.D(2)) })).Single().Severity.Should().Be(RuleSeverity.Block);
    [Fact] public void BR10_resolves_most_specific_then_latest_rate()
    {
        var ctx = B.Ctx(rates: new[] { Rate(B.Evening, B.D(1), amt: 100), new RateInfo(B.Evening.Id, B.Bu, null, 175, B.D(1), null), new RateInfo(B.Evening.Id, null, null, 120, B.D(10), null) });
        Br10RateMustExist.Resolve(ctx, B.Evening.Id, B.D(12))!.Amount.Should().Be(175);
    }

    // BR-11
    private static DelegationInfo Del(Role to = Role.LM, string grade = "G5", bool sub = false) => new(Role.LM | Role.EMP, to, "G5", grade, sub);
    [Fact] public void BR11_pass_same_role_same_grade_peer() => new Br11DelegationPeer().Evaluate(B.Ctx(del: Del())).Should().BeEmpty();
    [Fact] public void BR11_fail_subordinate() => new Br11DelegationPeer().Evaluate(B.Ctx(del: Del(sub: true))).Single().Message.Should().Contain("reports to you");
    [Fact] public void BR11_fail_different_role() => new Br11DelegationPeer().Evaluate(B.Ctx(del: Del(Role.PM))).Single().Message.Should().Contain("same role");
    [Fact] public void BR11_fail_different_grade() => new Br11DelegationPeer().Evaluate(B.Ctx(del: Del(grade: "G4"))).Single().Message.Should().Contain("grade");

    // BR-12
    [Fact] public void BR12_pass_future_change_never_checked() => new Br12BackdateSafe().Evaluate(B.Ctx(tags: new[] { new TagInfo(B.ProjB, B.D(1), null) }, backdate: new BackdateInfo(B.ProjA, new DateOnly(2026, 10, 20), null))).Should().BeEmpty();
    [Fact] public void BR12_pass_backdate_with_no_clash() => new Br12BackdateSafe().Evaluate(B.Ctx(tags: new[] { new TagInfo(B.ProjB, B.D(1), B.D(10)) }, backdate: new BackdateInfo(B.ProjA, B.D(15), null))).Should().BeEmpty();
    [Fact] public void BR12_fail_backdate_overlaps_approved_tag_elsewhere()
    {
        var r = new Br12BackdateSafe().Evaluate(B.Ctx(tags: new[] { new TagInfo(B.ProjB, B.D(1), null) }, backdate: new BackdateInfo(B.ProjA, B.D(5), null))).Single();
        r.Severity.Should().Be(RuleSeverity.Block); r.Message.Should().Contain("double-count");
    }

    [Fact] public void Engine_runs_all_twelve_rules_with_unique_codes()
    {
        var e = new RuleEngine();
        e.Rules.Should().HaveCount(12);
        e.Rules.Select(r => r.Code).Distinct().Should().HaveCount(12);
        e.Rules.Select(r => r.Code).Should().BeEquivalentTo(Enumerable.Range(1, 12).Select(i => $"BR-{i:00}"));
    }
    [Fact] public void Engine_aggregates_results_from_several_rules() => new RuleEngine().Evaluate(B.Ctx(B.Run(7, 7, B.Morning))).Select(r => r.RuleCode).Should().Contain(new[] { "BR-02", "BR-09" });
    [Fact] public void Severity_override_from_foundation_data_is_respected()
    {
        var r = new Br02MaxConsecutiveDays().Evaluate(B.Ctx(B.Run(1, 7, B.Morning), p: x => x.Severities["BR-02"] = RuleSeverity.Warn)).Single();
        r.Severity.Should().Be(RuleSeverity.Warn);
    }
    [Fact] public void Parameters_are_read_from_rule_json_not_hard_coded()
    {
        var rules = new[] { new ShiftRule { Code = "BR-02", Parameters = "{\"max\":5}", Severity = RuleSeverity.Block, EffectiveFrom = new DateOnly(2026, 1, 1) }, new ShiftRule { Code = "BR-03", Parameters = "{\"hours\":12}", Severity = RuleSeverity.Block, EffectiveFrom = new DateOnly(2026, 1, 1) } };
        var p = RuleParameters.From(rules, new DateOnly(2026, 9, 1));
        p.MaxConsecutiveDays.Should().Be(5); p.MinRestHours.Should().Be(12);
        RuleParameters.From(rules, new DateOnly(2025, 1, 1)).MaxConsecutiveDays.Should().Be(6); // rule not yet effective → default
    }
}
