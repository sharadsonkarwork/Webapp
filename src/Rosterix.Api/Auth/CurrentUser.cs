using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Rosterix.Domain;
using Rosterix.Infrastructure.Persistence;

namespace Rosterix.Api.Auth;

public interface ICurrentUser
{
    Guid Id { get; }
    Role Roles { get; }
    bool Has(Role r);
    Task<Employee> LoadAsync(CancellationToken ct = default);
}

public sealed class CurrentUser : ICurrentUser
{
    public const string EmployeeClaim = "rx_emp";
    private readonly IHttpContextAccessor _http; private readonly RosterixDbContext _db; private Employee? _emp;
    public CurrentUser(IHttpContextAccessor http, RosterixDbContext db) { _http = http; _db = db; }
    public Guid Id => Guid.TryParse(_http.HttpContext?.User.FindFirstValue(EmployeeClaim), out var g) ? g : Guid.Empty;
    public Role Roles => (_http.HttpContext?.User.FindAll(ClaimTypes.Role).Select(c => Enum.TryParse<Role>(c.Value, out var r) ? r : Role.None).Aggregate(Role.None, (a, b) => a | b)) ?? Role.None;
    public bool Has(Role r) => (Roles & r) != 0;
    public async Task<Employee> LoadAsync(CancellationToken ct = default) => _emp ??= await _db.Employees.AsNoTracking().FirstAsync(e => e.Id == Id, ct);
}

public static class ClaimsFactory
{
    public static ClaimsPrincipal For(Employee e, string scheme)
    {
        var claims = new List<Claim> { new(CurrentUser.EmployeeClaim, e.Id.ToString()), new(ClaimTypes.NameIdentifier, e.Id.ToString()), new(ClaimTypes.Name, e.FullName), new(ClaimTypes.Email, e.Email), new("rx_grade", e.Grade) };
        foreach (var r in Enum.GetValues<Role>().Where(r => r != Role.None && e.Roles.HasFlag(r))) claims.Add(new Claim(ClaimTypes.Role, r.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, ClaimTypes.Name, ClaimTypes.Role));
    }
}
