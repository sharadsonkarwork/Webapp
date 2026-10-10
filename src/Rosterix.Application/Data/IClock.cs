namespace Rosterix.Application.Data;

public interface IClock { DateOnly Today { get; } DateTime UtcNow { get; } }

/// <summary>Real time, or a pinned date for the demo so the seed's relative dates (expiring delegation, arrear ages) stay meaningful.</summary>
public sealed class SystemClock : IClock
{
    private readonly DateOnly? _fixed;
    public SystemClock(DateOnly? fixedToday = null) => _fixed = fixedToday;
    public DateOnly Today => _fixed ?? DateOnly.FromDateTime(DateTime.UtcNow);
    public DateTime UtcNow => _fixed is { } f ? f.ToDateTime(DateTime.UtcNow.TimeOfDay is var t ? TimeOnly.FromTimeSpan(t) : TimeOnly.MinValue, DateTimeKind.Utc) : DateTime.UtcNow;
}
