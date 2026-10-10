using System.Globalization;
using Rosterix.Application.Data;
using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Application.Allowance;

public sealed record CalculatedLine(Guid EmployeeId, DateOnly WorkDate, Guid ShiftTypeId, decimal Rate, decimal Amount, string Trace, RateInfo? RateRule);
public sealed record CalculatedException(Guid? EmployeeId, DateOnly? Date, ExceptionCategory Category, string? RuleCode, ExceptionSeverity Severity, string Description);
public sealed record CalculationResult(List<CalculatedLine> Lines, List<CalculatedException> Exceptions)
{ public decimal Total => Lines.Sum(l => l.Amount); }

/// <summary>Batch caller of the rule engine. Pure over a Dataset: no I/O, no persistence.</summary>
public sealed class AllowanceCalculator
{
    private readonly IRuleEngine _rules;
    public AllowanceCalculator(IRuleEngine rules) => _rules = rules;

    public CalculationResult Calculate(Dataset ds, Guid projectId, string month, IEnumerable<RosterEntry>? entriesOverride = null)
    {
        var roster = ds.FindRoster(projectId, month);
        var entries = entriesOverride?.ToList() ?? (roster == null ? new List<RosterEntry>() : ds.EntriesFor(roster.Id).ToList());
        var lines = new List<CalculatedLine>(); var exc = new List<CalculatedException>();
        foreach (var g in entries.GroupBy(e => e.EmployeeId))
        {
            var ctx = RuleContextFactory.For(ds, g.Key, projectId, month, g);
            foreach (var r in _rules.Evaluate(ctx).Where(r => r.RuleCode is not ("BR-10" or "BR-11" or "BR-12")))
                exc.Add(new CalculatedException(g.Key, null, ExceptionCategory.RuleViolation, r.RuleCode,
                    r.Severity == RuleSeverity.Block ? ExceptionSeverity.High : ExceptionSeverity.Medium, r.Message));
            var emp = ds.Emp(g.Key); var bu = ds.BU(emp.BusinessUnitId);
            foreach (var e in g.Where(x => x.EntryType == EntryType.Shift && x.ShiftTypeId != null).OrderBy(x => x.WorkDate))
            {
                var st = ds.Shift(e.ShiftTypeId!.Value);
                if (!st.IsAllowanceEligible) continue;
                var rate = Br10RateMustExist.Resolve(ctx, st.Id, e.WorkDate);
                if (rate == null) { exc.Add(new CalculatedException(g.Key, e.WorkDate, ExceptionCategory.DataGap, "BR-10", ExceptionSeverity.High, $"No effective rate for {st.Name} on {e.WorkDate:d MMM yyyy} (grade {emp.Grade}, {bu.Name}).")); continue; }
                lines.Add(new CalculatedLine(g.Key, e.WorkDate, st.Id, rate.Amount, rate.Amount, Trace(ds, emp, bu, st, e.WorkDate, rate), rate));
            }
        }
        return new CalculationResult(lines.OrderBy(l => l.EmployeeId).ThenBy(l => l.WorkDate).ToList(), exc);
    }

    public static string Trace(Dataset ds, Employee emp, BusinessUnit bu, ShiftType st, DateOnly date, RateInfo rate)
    {
        var ci = CultureInfo.GetCultureInfo("en-IN");
        var sb = new System.Text.StringBuilder();
        sb.Append($"{st.Name} shift on {date:dddd d MMMM yyyy} ({st.StartTime:HH\\:mm}–{st.EndTime:HH\\:mm}, {st.DurationHours:0.#} hours). ");
        sb.Append("This shift type is allowance-eligible. ");
        var scope = rate.BusinessUnitId != null && rate.Grade != null ? $"{bu.Name}, grade {emp.Grade}" : rate.BusinessUnitId != null ? bu.Name : rate.Grade != null ? $"grade {emp.Grade}" : "all business units";
        sb.Append($"The rate in effect on that date for {scope} is {rate.Amount.ToString("C0", ci)}");
        var prior = ds.Rates.Where(r => r.ShiftTypeId == st.Id && r.BusinessUnitId == rate.BusinessUnitId && r.Grade == rate.Grade && r.EffectiveTo != null && r.EffectiveTo.Value.AddDays(1) == rate.From).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        if (prior != null) sb.Append($" (revised from {prior.Amount.ToString("C0", ci)} on {rate.From:d MMM yyyy})");
        sb.Append($". Allowance for the day: {rate.Amount.ToString("C0", ci)}.");
        return sb.ToString();
    }
}
