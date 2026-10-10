using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rosterix.Application.Data;
using Rosterix.Infrastructure.Persistence;

namespace Rosterix.Api.Auth;

/// <summary>SEC-08 — local role-switcher. Registered only when ASPNETCORE_ENVIRONMENT=Development. Header: X-Dev-Persona: priya|arjun|meera|karthik|fathima|ravi (or an employee GUID).</summary>
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Scheme = "DevAuth";
    private readonly RosterixDbContext _db;
    public DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e, RosterixDbContext db) : base(o, l, e) { _db = db; }
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers["X-Dev-Persona"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key)) return AuthenticateResult.NoResult();
        Guid id;
        var persona = Personas.ByKey(key);
        if (persona != null) id = persona.Id; else if (!Guid.TryParse(key, out id)) return AuthenticateResult.Fail("Unknown persona.");
        var emp = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (emp == null || !emp.IsActive) return AuthenticateResult.Fail("Unknown or inactive employee.");
        return AuthenticateResult.Success(new AuthenticationTicket(ClaimsFactory.For(emp, Scheme), Scheme));
    }
}
