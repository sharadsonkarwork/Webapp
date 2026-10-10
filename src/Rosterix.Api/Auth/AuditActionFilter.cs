using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Controllers;
using Rosterix.Domain;
using Rosterix.Infrastructure.Services;

namespace Rosterix.Api.Auth;

/// <summary>Marks an endpoint that is a POST only because it carries a body (previews/validation). It changes no state, so it writes no audit row.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ReadOnlyOperationAttribute : Attribute { }

/// <summary>SEC-07 — every state-changing request (POST/PUT/PATCH/DELETE) writes an AuditLog row: type, UTC time, success/failure, actor, IP, endpoint, resource.</summary>
public sealed class AuditActionFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> Mutating = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };
    private readonly IAuditSink _sink; private readonly ILogger<AuditActionFilter> _log;
    public AuditActionFilter(IAuditSink sink, ILogger<AuditActionFilter> log) { _sink = sink; _log = log; }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var http = ctx.HttpContext;
        var cad = ctx.ActionDescriptor as ControllerActionDescriptor;
        var readOnly = cad?.MethodInfo.GetCustomAttributes(typeof(ReadOnlyOperationAttribute), true).Any() == true;
        if (!Mutating.Contains(http.Request.Method) || readOnly) { await next(); return; }

        var executed = await next();
        var ok = executed.Exception == null || executed.ExceptionHandled;
        var status = (executed.Result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode ?? http.Response.StatusCode;
        if (executed.Result is Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult sc && sc.StatusCode is { } code) ok = ok && code < 400;
        var detail = new Dictionary<string, object?> { ["status"] = status, ["action"] = cad?.ActionName, ["args"] = ctx.ActionArguments.ToDictionary(k => k.Key, k => k.Value) };
        string json;
        try { json = Masking.Mask(JsonSerializer.Serialize(detail, new JsonSerializerOptions { WriteIndented = false, MaxDepth = 8, ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles })); }
        catch { json = "{}"; }
        var id = ctx.RouteData.Values.TryGetValue("id", out var rid) ? rid?.ToString() : null;
        var actor = Guid.TryParse(http.User.FindFirst(CurrentUser.EmployeeClaim)?.Value, out var g) ? g : (Guid?)null;
        try
        {
            await _sink.WriteAsync(new AuditLog
            {
                EventType = $"{http.Request.Method} {cad?.ControllerName}.{cad?.ActionName}", OccurredAtUtc = DateTime.UtcNow, ActorEmployeeId = actor, Succeeded = ok,
                OriginIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown", Endpoint = http.Request.Path.Value ?? "", ResourceType = cad?.ControllerName ?? "", ResourceId = id, Detail = json
            });
        }
        catch (Exception ex) { _log.LogError(ex, "Audit write failed for {Path}", http.Request.Path); throw; }
    }
}

/// <summary>SEC-07 (denials): an attempted state change that authorization refuses is still audited, as a failure.</summary>
public sealed class AuditDeniedMiddleware
{
    private static readonly HashSet<string> Mutating = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };
    private readonly RequestDelegate _next;
    public AuditDeniedMiddleware(RequestDelegate next) => _next = next;
    public async Task InvokeAsync(HttpContext ctx, IAuditSink sink)
    {
        await _next(ctx);
        if (!Mutating.Contains(ctx.Request.Method) || ctx.Response.StatusCode is not (401 or 403) || ctx.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>() == null) return;
        var cad = ctx.GetEndpoint()!.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()!;
        if (cad.MethodInfo.GetCustomAttributes(typeof(ReadOnlyOperationAttribute), true).Any()) return;
        // Authorization short-circuits before action filters, so no row exists yet for this request.
        var actor = Guid.TryParse(ctx.User.FindFirst(CurrentUser.EmployeeClaim)?.Value, out var g) ? g : (Guid?)null;
        await sink.WriteAsync(new AuditLog
        {
            EventType = $"{ctx.Request.Method} {cad.ControllerName}.{cad.ActionName} DENIED", ActorEmployeeId = actor, Succeeded = false, OriginIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Endpoint = ctx.Request.Path.Value ?? "", ResourceType = cad.ControllerName, ResourceId = ctx.GetRouteValue("id")?.ToString(), Detail = $"{{\"status\":{ctx.Response.StatusCode}}}"
        });
    }
}
