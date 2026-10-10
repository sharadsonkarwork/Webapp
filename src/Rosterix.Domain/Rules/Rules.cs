namespace Rosterix.Domain.Rules;

internal static class Fmt
{
    public static string D(DateOnly d) => d.ToString("ddd d MMM");
    public static string Dates(IEnumerable<DateOnly> ds) => string.Join(", ", ds.OrderBy(x => x).Select(D));
}

public abstract class RuleBase : IRule
{
    public abstract string Code { get; }
    public abstract RuleSeverity DefaultSeverity { get; }
    public abstract IEnumerable<RuleResult> Evaluate(RuleContext ctx);
    protected RuleResult Make(RuleContext ctx, string message, IEnumerable<string> affected, string? fix = null)
        => new(Code, ctx.Parameters.SeverityOf(Code, DefaultSeverity), message, affected.ToList(), fix);
    protected static IEnumerable<EntryInfo> InPeriod(RuleContext c) => c.Entries.Where(e => e.Date >= c.PeriodStart && e.Date <= c.PeriodEnd);
    protected static IEnumerable<EntryInfo> Shifts(RuleContext c) => c.Entries.Where(e => e.Type == EntryType.Shift && e.Shift != null);
}

/// <summary>BR-01 — no overlapping active tags on two projects for the same date.</summary>
public sealed class Br01NoOverlappingTags : RuleBase
{
    public override string Code => "BR-01";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        var tags = c.Tags.ToList();
        for (var i = 0; i < tags.Count; i++)
            for (var j = i + 1; j < tags.Count; j++)
            {
                var a = tags[i]; var b = tags[j];
                if (a.ProjectId == b.ProjectId || !a.Overlaps(b.From, b.To)) continue;
                var from = a.From > b.From ? a.From : b.From;
                yield return Make(c,
                    $"{c.EmployeeName} would be tagged to two projects from {Fmt.D(from)}. An employee can sit on only one project on any date.",
                    new[] { $"project:{a.ProjectId}", $"project:{b.ProjectId}" },
                    $"End the earlier tag the day before {Fmt.D(from)} or pick a different start date.");
            }
    }
}

/// <summary>BR-02 — max consecutive working days (default 6).</summary>
public sealed class Br02MaxConsecutiveDays : RuleBase
{
    public override string Code => "BR-02";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public static IEnumerable<List<DateOnly>> Runs(RuleContext c)
    {
        var days = Shifts(c).Select(e => e.Date).Distinct().OrderBy(d => d).ToList();
        var run = new List<DateOnly>();
        foreach (var d in days)
        {
            if (run.Count > 0 && d == run[^1].AddDays(1)) run.Add(d);
            else { if (run.Count > 0) yield return run; run = new List<DateOnly> { d }; }
        }
        if (run.Count > 0) yield return run;
    }
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        foreach (var r in Runs(c).Where(r => r.Count > c.Parameters.MaxConsecutiveDays))
            yield return Make(c,
                $"{c.EmployeeName} works {r.Count} days in a row ({Fmt.D(r[0])} to {Fmt.D(r[^1])}). The limit is {c.Parameters.MaxConsecutiveDays}.",
                r.Select(d => $"date:{d:yyyy-MM-dd}"),
                $"Make {Fmt.D(r[c.Parameters.MaxConsecutiveDays])} a weekly off or move the shift to a colleague.");
    }
}

/// <summary>BR-03 — minimum rest between consecutive shifts (default 11 h).</summary>
public sealed class Br03MinRest : RuleBase
{
    public override string Code => "BR-03";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public static IEnumerable<(EntryInfo A, EntryInfo B, double Gap)> Gaps(RuleContext c)
    {
        var s = Shifts(c).OrderBy(e => e.Date).ToList();
        for (var i = 0; i + 1 < s.Count; i++)
        {
            var a = s[i]; var b = s[i + 1];
            if ((b.Date.DayNumber - a.Date.DayNumber) > 1) continue;
            var end = ShiftMath.EndAt(a.Date, a.Shift!.Start, a.Shift.End, a.Shift.SpansMidnight);
            var start = ShiftMath.StartAt(b.Date, b.Shift!.Start);
            yield return (a, b, (start - end).TotalHours);
        }
    }
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        foreach (var (a, b, gap) in Gaps(c).Where(g => g.Gap < c.Parameters.MinRestHours))
            yield return Make(c,
                $"Only {gap:0.#} h of rest between {a.Shift!.Name} on {Fmt.D(a.Date)} and {b.Shift!.Name} on {Fmt.D(b.Date)}. At least {c.Parameters.MinRestHours:0.#} h is required.",
                new[] { $"date:{a.Date:yyyy-MM-dd}", $"date:{b.Date:yyyy-MM-dd}" },
                $"Move {b.Date:ddd d MMM} to a later shift or give {c.EmployeeName} the day off.");
    }
}

/// <summary>BR-04 — a roster entry requires an approved employee tag covering that date.</summary>
public sealed class Br04RequiresApprovedTag : RuleBase
{
    public override string Code => "BR-04";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        var bad = Shifts(c).Where(e => e.ProjectId == c.ProjectId && e.Date >= c.PeriodStart && e.Date <= c.PeriodEnd
                    && !c.Tags.Any(t => t.Approved && t.ProjectId == e.ProjectId && t.Covers(e.Date))).Select(e => e.Date).ToList();
        if (bad.Count > 0)
            yield return Make(c,
                $"{c.EmployeeName} has no approved project tag on {Fmt.Dates(bad.Take(3))}{(bad.Count > 3 ? $" and {bad.Count - 3} more" : "")}.",
                bad.Select(d => $"date:{d:yyyy-MM-dd}"),
                "Tag the employee to this project for those dates and get the tag approved, or remove the shifts.");
    }
}

/// <summary>BR-05 — roster dates must fall within the project's active range.</summary>
public sealed class Br05WithinProjectRange : RuleBase
{
    public override string Code => "BR-05";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        if (c.Project == null) yield break;
        var bad = c.Entries.Where(e => e.ProjectId == c.Project.Id && e.Date >= c.PeriodStart && e.Date <= c.PeriodEnd
                    && e.Type == EntryType.Shift && (e.Date < c.Project.Start || (c.Project.End != null && e.Date > c.Project.End))).Select(e => e.Date).ToList();
        if (bad.Count > 0)
            yield return Make(c,
                $"{Fmt.Dates(bad.Take(3))} {(bad.Count > 3 ? $"(+{bad.Count - 3} more) " : "")}fall outside the project's dates ({c.Project.Start:d MMM yyyy} – {(c.Project.End?.ToString("d MMM yyyy") ?? "open")}).",
                bad.Select(d => $"date:{d:yyyy-MM-dd}"),
                "Remove those shifts or extend the project dates first.");
    }
}

/// <summary>BR-06 — no shift on an approved leave day or restricted holiday (warning).</summary>
public sealed class Br06LeaveOrHoliday : RuleBase
{
    public override string Code => "BR-06";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Warn;
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        foreach (var e in InPeriod(c).Where(e => e.Type == EntryType.Shift))
        {
            if (c.LeaveDays.Contains(e.Date))
                yield return Make(c, $"{c.EmployeeName} is on approved leave on {Fmt.D(e.Date)} but has a shift.", new[] { $"date:{e.Date:yyyy-MM-dd}" }, "Replace the shift with Leave, or confirm the leave was cancelled.");
            else if (c.RestrictedHolidays.Contains(e.Date))
                yield return Make(c, $"{Fmt.D(e.Date)} is a restricted holiday. Scheduling a shift needs an explicit acknowledgement.", new[] { $"date:{e.Date:yyyy-MM-dd}" }, "Acknowledge to continue, or schedule the shift on another day.");
        }
    }
}

/// <summary>BR-07 — max scheduled hours per ISO week (default 48, warning).</summary>
public sealed class Br07MaxWeeklyHours : RuleBase
{
    public override string Code => "BR-07";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Warn;
    public static IEnumerable<(DateOnly WeekStart, double Hours)> Weekly(RuleContext c) =>
        Shifts(c).GroupBy(e => ShiftMath.WeekStart(e.Date)).Select(g => (g.Key, g.Sum(e => e.Shift!.Hours)));
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        foreach (var (ws, h) in Weekly(c).Where(w => w.Hours > c.Parameters.MaxWeeklyHours && w.WeekStart.AddDays(6) >= c.PeriodStart && w.WeekStart <= c.PeriodEnd))
            yield return Make(c, $"{c.EmployeeName} is scheduled for {h:0.#} h in the week of {Fmt.D(ws)}. The guideline is {c.Parameters.MaxWeeklyHours:0.#} h.",
                new[] { $"week:{ws:yyyy-MM-dd}" }, "Shorten one shift or move it to the following week.");
    }
}

/// <summary>BR-08 — allowance only for allowance-eligible shift types.</summary>
public sealed class Br08EligibleShiftOnly : RuleBase
{
    public override string Code => "BR-08";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        if (!c.EvaluateAllowance) yield break;
        foreach (var e in Shifts(c).Where(e => c.AllowanceClaims.Contains(e.Date) && !e.Shift!.AllowanceEligible))
            yield return Make(c, $"{e.Shift!.Name} on {Fmt.D(e.Date)} is not allowance-eligible, so no allowance can be claimed for it.",
                new[] { $"date:{e.Date:yyyy-MM-dd}" }, "Remove the claim or change the shift type.");
    }
}

/// <summary>BR-09 — minimum weekly offs per calendar week (default 1). Only fully-covered weeks are judged.</summary>
public sealed class Br09MinWeeklyOffs : RuleBase
{
    public override string Code => "BR-09";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        var shiftDates = Shifts(c).Select(e => e.Date).ToHashSet();
        for (var ws = ShiftMath.WeekStart(c.PeriodStart); ws <= c.PeriodEnd; ws = ws.AddDays(7))
        {
            if (ws < c.PeriodStart || ws.AddDays(6) > c.PeriodEnd) continue;
            var worked = Enumerable.Range(0, 7).Count(i => shiftDates.Contains(ws.AddDays(i)));
            if (worked == 0) continue;
            var offs = 7 - worked;
            if (offs < c.Parameters.MinWeeklyOffs)
                yield return Make(c, $"{c.EmployeeName} has {offs} weekly off(s) in the week of {Fmt.D(ws)}. At least {c.Parameters.MinWeeklyOffs} is required.",
                    new[] { $"week:{ws:yyyy-MM-dd}" }, "Mark one day that week as a weekly off.");
        }
    }
}

/// <summary>BR-10 — an effective allowance rate must exist for (shift type, BU, grade) on the work date.</summary>
public sealed class Br10RateMustExist : RuleBase
{
    public override string Code => "BR-10";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public static RateInfo? Resolve(RuleContext c, Guid shiftTypeId, DateOnly d) =>
        c.Rates.Where(r => r.Matches(shiftTypeId, c.BusinessUnitId, c.Grade, d))
               .OrderByDescending(r => (r.BusinessUnitId != null ? 2 : 0) + (r.Grade != null ? 1 : 0)).ThenByDescending(r => r.From).FirstOrDefault();
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        if (!c.EvaluateAllowance) yield break;
        var missing = InPeriod(c).Where(e => e.Type == EntryType.Shift && e.Shift!.AllowanceEligible && Resolve(c, e.Shift.Id, e.Date) == null).ToList();
        foreach (var g in missing.GroupBy(e => e.Shift!.Id))
        {
            var first = g.First();
            yield return Make(c, $"No allowance rate is effective for {first.Shift!.Name} (grade {c.Grade}) on {Fmt.Dates(g.Select(x => x.Date).Take(3))}.",
                g.Select(x => $"date:{x.Date:yyyy-MM-dd}"), "Ask HRSS to add an effective-dated rate in Foundation data.");
        }
    }
}

/// <summary>BR-11 — delegation target must be same role + grade, and not a subordinate.</summary>
public sealed class Br11DelegationPeer : RuleBase
{
    public override string Code => "BR-11";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        var d = c.Delegation;
        if (d == null) yield break;
        if (d.ToIsSubordinateOfFrom)
            yield return Make(c, "You can't delegate to someone who reports to you. Choose a peer at your own level.", new[] { "delegation" }, "Pick a colleague with the same role and grade who isn't in your reporting line.");
        else if ((d.FromRoles & d.ToRoles & (Role.PM | Role.LM | Role.HRBP | Role.RH | Role.HRSS)) == 0)
            yield return Make(c, "The delegate must hold the same role as you.", new[] { "delegation" }, "Choose someone with the same approval role.");
        else if (!string.Equals(d.FromGrade, d.ToGrade, StringComparison.OrdinalIgnoreCase))
            yield return Make(c, $"The delegate must be at your grade ({d.FromGrade}). They are {d.ToGrade}.", new[] { "delegation" }, "Choose a same-grade peer.");
    }
}

/// <summary>BR-12 — a backdated change may not overlap or orphan an approved tag elsewhere.</summary>
public sealed class Br12BackdateSafe : RuleBase
{
    public override string Code => "BR-12";
    public override RuleSeverity DefaultSeverity => RuleSeverity.Block;
    public override IEnumerable<RuleResult> Evaluate(RuleContext c)
    {
        var b = c.Backdate;
        if (b == null || b.From >= c.Today) yield break;
        foreach (var t in c.Tags.Where(t => t.Approved && t.ProjectId != b.ProjectId && t.Overlaps(b.From, b.To)))
            yield return Make(c,
                $"Backdating to {Fmt.D(b.From)} would overlap an approved tag on another project (from {Fmt.D(t.From)}). That would double-count {c.EmployeeName}.",
                new[] { $"project:{t.ProjectId}" }, $"Start the backdated tag on or after {Fmt.D(t.To?.AddDays(1) ?? c.Today)}, or end the other tag first.");
    }
}

public sealed class RuleEngine : IRuleEngine
{
    public IReadOnlyList<IRule> Rules { get; } = new IRule[]
    {
        new Br01NoOverlappingTags(), new Br02MaxConsecutiveDays(), new Br03MinRest(), new Br04RequiresApprovedTag(),
        new Br05WithinProjectRange(), new Br06LeaveOrHoliday(), new Br07MaxWeeklyHours(), new Br08EligibleShiftOnly(),
        new Br09MinWeeklyOffs(), new Br10RateMustExist(), new Br11DelegationPeer(), new Br12BackdateSafe()
    };
    public IReadOnlyList<RuleResult> Evaluate(RuleContext ctx) => Rules.SelectMany(r => r.Evaluate(ctx)).ToList();
}
