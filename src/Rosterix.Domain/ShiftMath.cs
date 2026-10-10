namespace Rosterix.Domain;

public static class ShiftMath
{
    public static double DurationHours(TimeOnly start, TimeOnly end, bool spansMidnight)
    {
        var s = start.ToTimeSpan(); var e = end.ToTimeSpan();
        var d = e - s;
        if (spansMidnight || d <= TimeSpan.Zero) d += TimeSpan.FromHours(24);
        return d.TotalHours;
    }
    public static DateTime StartAt(DateOnly day, TimeOnly start) => day.ToDateTime(start);
    public static DateTime EndAt(DateOnly day, TimeOnly start, TimeOnly end, bool spansMidnight)
        => day.ToDateTime(start).AddHours(DurationHours(start, end, spansMidnight));
    public static string Month(DateOnly d) => $"{d.Year:D4}-{d.Month:D2}";
    public static DateOnly MonthStart(string yyyyMm) => new(int.Parse(yyyyMm[..4]), int.Parse(yyyyMm[5..7]), 1);
    public static DateOnly MonthEnd(string yyyyMm) => MonthStart(yyyyMm).AddMonths(1).AddDays(-1);
    public static DateOnly WeekStart(DateOnly d) { var diff = ((int)d.DayOfWeek + 6) % 7; return d.AddDays(-diff); }
}
