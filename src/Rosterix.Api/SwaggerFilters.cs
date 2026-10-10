using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Rosterix.Api;

/// <summary>Documents the error contract once for every operation, and adds request examples for the endpoints people try first.</summary>
public sealed class StandardResponsesFilter : IOperationFilter
{
    private static readonly Dictionary<string, string> Examples = new()
    {
        ["POST /api/approvals/{id}/decide"] = """{ "approve": false, "comment": "Start date predates the project plan" }""",
        ["POST /api/allowance-runs/calculate"] = """{ "projectId": "00000000-0000-0000-0000-000000000000", "month": "2026-09" }""",
        ["POST /api/delegations"] = """{ "toEmployeeId": "00000000-0000-0000-0000-000000000000", "scope": "ProjectApproval", "validFrom": "2026-10-20", "validTo": "2026-10-27", "reason": "Annual leave" }""",
        ["POST /api/employee-tags"] = """{ "projectId": "00000000-0000-0000-0000-000000000000", "employeeIds": ["00000000-0000-0000-0000-000000000000"], "from": "2026-11-02", "to": null, "tagType": "Primary" }""",
        ["PUT /api/rosters/{id}/entries"] = """{ "edits": [ { "employeeId": "00000000-0000-0000-0000-000000000000", "workDate": "2026-10-20", "shiftTypeId": "00000000-0000-0000-0000-000000000000", "entryType": "Shift" } ], "acknowledged": ["BR-06"], "reason": "Festival cover" }""",
        ["POST /api/rosters/{id}/publish"] = """{ "confirm": true, "acknowledged": ["BR-06", "BR-07"] }""",
        ["POST /api/ai/validate"] = """{ "projectId": "00000000-0000-0000-0000-000000000000", "month": "2026-10", "edits": [ { "employeeId": "00000000-0000-0000-0000-000000000000", "workDate": "2026-10-20", "shiftTypeId": null, "entryType": "WeeklyOff" } ] }""",
        ["POST /api/ai/feedback"] = """{ "useCaseCode": "AI-UC-09", "contextType": "Anomaly", "contextId": "dup:...", "action": "Dismiss", "reason": "Verified with the roster desk", "confidence": 0.95 }""",
        ["POST /api/payroll/batches"] = """{ "month": "2026-09", "runIds": ["00000000-0000-0000-0000-000000000000"], "acknowledgeWarnings": true }""",
        ["POST /api/foundation/rates"] = """{ "shiftTypeId": "00000000-0000-0000-0000-000000000000", "businessUnitId": null, "grade": null, "amount": 550, "effectiveFrom": "2026-11-01" }""",
    };

    public void Apply(OpenApiOperation op, OperationFilterContext ctx)
    {
        var anon = ctx.MethodInfo.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any();
        var verb = ctx.ApiDescription.HttpMethod ?? ""; var path = "/" + ctx.ApiDescription.RelativePath?.Split('?')[0];
        path = System.Text.RegularExpressions.Regex.Replace(path, @"\{(\w+):?[^}]*\}", "{$1}");
        void Add(string code, string text) { if (!op.Responses.ContainsKey(code)) op.Responses[code] = new OpenApiResponse { Description = text }; }
        if (verb is "POST" or "PUT") Add("400", "The request failed validation or a blocking business rule. Body: { title, status, extra } where `title` says what happened and how to fix it, and `extra` may list rule violations.");
        if (!anon) { Add("401", "No valid sign-in. In Development send the X-Dev-Persona header."); Add("403", "Your role may not do this (for example only HRSS approves allowance compensation). The attempt is audited."); }
        if (path.Contains('{')) Add("404", "The record doesn't exist, or isn't visible to your role.");
        if (verb is "POST" or "PUT") Add("409", "The record is in the wrong state (already decided, already published, needs acknowledgement of warnings).");
        if (Examples.TryGetValue($"{verb} {path}", out var json) && op.RequestBody?.Content.TryGetValue("application/json", out var media) == true) media.Example = Swashbuckle.AspNetCore.SwaggerGen.OpenApiAnyFactory.CreateFromJson(json);
    }
}
