using Microsoft.Extensions.Logging;
using Rosterix.Application.Data;
using Rosterix.Domain;
using Rosterix.Infrastructure.Persistence;

namespace Rosterix.Infrastructure.Services;

public interface INotifier
{
    void Notify(Guid recipient, string type, string title, string body, NotificationSeverity sev, string route);
}

/// <summary>In-app notification plus an email stub that is logged in dev (never sends). Does not save; callers save with their own unit of work.</summary>
public sealed class Notifier : INotifier
{
    private readonly RosterixDbContext _db; private readonly IClock _clock; private readonly ILogger<Notifier> _log;
    public Notifier(RosterixDbContext db, IClock clock, ILogger<Notifier> log) { _db = db; _clock = clock; _log = log; }
    public void Notify(Guid recipient, string type, string title, string body, NotificationSeverity sev, string route)
    {
        _db.Notifications.Add(new Notification { RecipientEmployeeId = recipient, Type = type, Title = title, Body = body, Severity = sev, LinkRoute = route, SentAtUtc = _clock.UtcNow });
        _log.LogInformation("EMAIL(stub) to={Recipient} type={Type} subject={Subject}", recipient, type, title);
    }
}
