using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rosterix.Api.Auth;
using Rosterix.Application.Ai;
using Rosterix.Application.Data;
using Rosterix.Domain;
using Rosterix.Infrastructure.Persistence;
using Rosterix.Infrastructure.Services;

namespace Rosterix.Api.Controllers;

[ApiController, Authorize]
public abstract class RxController : ControllerBase
{
    protected T S<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();
    protected RosterixDbContext Db => S<RosterixDbContext>();
    protected ICurrentUser Me => S<ICurrentUser>();
    protected IClock Clock => S<IClock>();
    protected INotifier Notifier => S<INotifier>();
    protected Task<Dataset> LoadAsync(CancellationToken ct) => S<IAiDataSource>().LoadAsync(ct);

    protected ObjectResult Fail(int status, string title, object? extra = null) =>
        new(new { title, status, extra }) { StatusCode = status };

    /// <summary>Projects the current user may see: union over each role they hold.</summary>
    protected HashSet<Guid> VisibleProjects(Dataset ds)
    {
        var me = ds.Emp(Me.Id); var ids = new HashSet<Guid>();
        var myRegion = ds.BU(me.BusinessUnitId).RegionId;
        foreach (var p in ds.Projects)
        {
            var owner = ds.Emp(p.OwnerPmId); var pRegion = ds.BU(p.BusinessUnitId).RegionId;
            if (me.Roles.HasFlag(Role.HRSS)) ids.Add(p.Id);
            if (me.Roles.HasFlag(Role.PM) && p.OwnerPmId == me.Id) ids.Add(p.Id);
            if (me.Roles.HasFlag(Role.LM) && owner.ManagerId == me.Id) ids.Add(p.Id);
            if ((me.Roles.HasFlag(Role.HRBP) || me.Roles.HasFlag(Role.RH)) && pRegion == myRegion) ids.Add(p.Id);
            if (ds.EmployeeTags.Any(t => t.EmployeeId == me.Id && t.ProjectId == p.Id)) ids.Add(p.Id);
            // delegations received extend visibility to the delegator's projects
            foreach (var d in ds.Delegations.Where(d => d.ToEmployeeId == me.Id && d.IsActiveOn(ds.Today)))
                if (owner.ManagerId == d.FromEmployeeId || p.OwnerPmId == d.FromEmployeeId) ids.Add(p.Id);
        }
        return ids;
    }

    protected static object EmpRef(Employee e) => new { id = e.Id, name = e.FullName, grade = e.Grade, number = e.EmployeeNumber };
    protected static string? Month(string? m) => m;
    protected static bool ValidMonth(string? m) => m is { Length: 7 } && m[4] == '-' && int.TryParse(m[..4], out _) && int.TryParse(m[5..], out var mm) && mm is >= 1 and <= 12;
}
