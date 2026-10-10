using System.Text.Json;

namespace Rosterix.Domain.Rules;

public sealed record ShiftInfo(Guid Id, string Code, string Name, TimeOnly Start, TimeOnly End, bool SpansMidnight, bool AllowanceEligible)
{
    public double Hours => ShiftMath.DurationHours(Start, End, SpansMidnight);
    public static ShiftInfo From(ShiftType s) => new(s.Id, s.Code, s.Name, s.StartTime, s.EndTime, s.SpansMidnight, s.IsAllowanceEligible);
}

/// <summary>One scheduled day for the employee under evaluation (across any project).</summary>
public sealed record EntryInfo(DateOnly Date, EntryType Type, ShiftInfo? Shift, Guid ProjectId);

public sealed record TagInfo(Guid ProjectId, DateOnly From, DateOnly? To, bool Approved = true)
{
    public bool Covers(DateOnly d) => d >= From && (To == null || d <= To);
    public bool Overlaps(DateOnly from, DateOnly? to) => (To == null || from <= To) && (to == null || From <= to);
}

public sealed record ProjectInfo(Guid Id, DateOnly Start, DateOnly? End);
public sealed record RateInfo(Guid ShiftTypeId, Guid? BusinessUnitId, string? Grade, decimal Amount, DateOnly From, DateOnly? To)
{
    public bool Matches(Guid shiftTypeId, Guid buId, string grade, DateOnly d) =>
        ShiftTypeId == shiftTypeId && (BusinessUnitId == null || BusinessUnitId == buId) && (Grade == null || Grade == grade)
        && d >= From && (To == null || d <= To);
}
public sealed record BackdateInfo(Guid ProjectId, DateOnly From, DateOnly? To);
public sealed record DelegationInfo(Role FromRoles, Role ToRoles, string FromGrade, string ToGrade, bool ToIsSubordinateOfFrom);

public sealed record RuleResult(
    string RuleCode, RuleSeverity Severity, string Message,
    IReadOnlyList<string> AffectedEntities, string? SuggestedFix = null);

/// <summary>Editable defaults (stored as ShiftRule.Parameters JSON; never hard-coded in rules).</summary>
public sealed class RuleParameters
{
    public int MaxConsecutiveDays { get; set; } = 6;
    public double MinRestHours { get; set; } = 11;
    public double MaxWeeklyHours { get; set; } = 48;
    public int MinWeeklyOffs { get; set; } = 1;
    public Dictionary<string, RuleSeverity> Severities { get; set; } = new();

    public RuleSeverity SeverityOf(string code, RuleSeverity fallback) => Severities.TryGetValue(code, out var s) ? s : fallback;

    public static RuleParameters From(IEnumerable<ShiftRule> rules, DateOnly on)
    {
        var p = new RuleParameters();
        foreach (var r in rules.Where(r => r.EffectiveFrom <= on && (r.EffectiveTo == null || r.EffectiveTo >= on)))
        {
            p.Severities[r.Code] = r.Severity;
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.Parameters) ? "{}" : r.Parameters);
            var root = doc.RootElement;
            switch (r.Code)
            {
                case "BR-02" when root.TryGetProperty("max", out var v): p.MaxConsecutiveDays = v.GetInt32(); break;
                case "BR-03" when root.TryGetProperty("hours", out var v): p.MinRestHours = v.GetDouble(); break;
                case "BR-07" when root.TryGetProperty("hours", out var v): p.MaxWeeklyHours = v.GetDouble(); break;
                case "BR-09" when root.TryGetProperty("min", out var v): p.MinWeeklyOffs = v.GetInt32(); break;
            }
        }
        return p;
    }
}

public sealed class RuleContext
{
    public Guid EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "Employee";
    public string Grade { get; init; } = "G3";
    public Guid BusinessUnitId { get; init; }
    public Guid ProjectId { get; init; }
    public ProjectInfo? Project { get; init; }
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    /// <summary>All of the employee's roster entries in/around the period, across projects, including proposed ones.</summary>
    public IReadOnlyList<EntryInfo> Entries { get; init; } = Array.Empty<EntryInfo>();
    /// <summary>All of the employee's tags (approved and proposed) across projects.</summary>
    public IReadOnlyList<TagInfo> Tags { get; init; } = Array.Empty<TagInfo>();
    public IReadOnlySet<DateOnly> LeaveDays { get; init; } = new HashSet<DateOnly>();
    public IReadOnlySet<DateOnly> RestrictedHolidays { get; init; } = new HashSet<DateOnly>();
    public IReadOnlyList<RateInfo> Rates { get; init; } = Array.Empty<RateInfo>();
    public RuleParameters Parameters { get; init; } = new();
    public BackdateInfo? Backdate { get; init; }
    public DelegationInfo? Delegation { get; init; }
    /// <summary>Dates for which an allowance has been explicitly claimed (BR-08 judges these).</summary>
    public IReadOnlySet<DateOnly> AllowanceClaims { get; init; } = new HashSet<DateOnly>();
    public bool EvaluateAllowance { get; init; } = true;
}

public interface IRule
{
    string Code { get; }
    RuleSeverity DefaultSeverity { get; }
    IEnumerable<RuleResult> Evaluate(RuleContext ctx);
}

public interface IRuleEngine
{
    IReadOnlyList<RuleResult> Evaluate(RuleContext ctx);
    IReadOnlyList<IRule> Rules { get; }
}
