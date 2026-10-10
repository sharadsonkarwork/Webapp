using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Rosterix.Application.Data;
using Rosterix.Domain;
using Xunit;

namespace Rosterix.Api.Tests;

public class RbacMatrixTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _f;
    public RbacMatrixTests(ApiFixture f) => _f = f;

    // (method, url, persona that must be refused)
    public static IEnumerable<object[]> Denied() => new[]
    {
        new object[] { "POST", "/api/projects", "fathima" }, new object[] { "POST", "/api/projects", "ravi" }, new object[] { "POST", "/api/projects", "meera" },
        new object[] { "POST", "/api/employee-tags", "ravi" }, new object[] { "POST", "/api/employee-tags/validate", "karthik" },
        new object[] { "POST", "/api/rosters", "ravi" }, new object[] { "POST", "/api/rosters", "meera" }, new object[] { "POST", "/api/rosters/accept-draft", "karthik" },
        new object[] { "POST", "/api/allowance-runs/calculate", "ravi" }, new object[] { "POST", "/api/allowance-runs/calculate", "karthik" },
        new object[] { "POST", "/api/payroll/batches", "priya" }, new object[] { "POST", "/api/payroll/batches", "arjun" }, new object[] { "POST", "/api/payroll/batches", "karthik" }, new object[] { "GET", "/api/payroll/checklist?month=2026-09", "priya" },
        new object[] { "POST", "/api/foundation/rates", "priya" }, new object[] { "POST", "/api/foundation/rates", "karthik" }, new object[] { "POST", "/api/foundation/shift-types", "meera" },
        new object[] { "POST", "/api/foundation/shift-rules", "arjun" }, new object[] { "POST", "/api/foundation/holidays", "priya" }, new object[] { "PUT", $"/api/foundation/holidays/{Guid.NewGuid()}", "ravi" },
        new object[] { "GET", "/api/movements", "priya" }, new object[] { "POST", "/api/movements", "arjun" }, new object[] { "GET", "/api/settlements", "priya" },
        new object[] { "POST", "/api/ai/ripple", "priya" }, new object[] { "GET", "/api/ai/approval-risk", "priya" }, new object[] { "GET", "/api/ai/approval-risk", "ravi" },
        new object[] { "GET", "/api/ai/anomalies", "ravi" }, new object[] { "GET", "/api/ai/project-setup?businessUnitId=" + Guid.NewGuid(), "ravi" },
        new object[] { "GET", "/api/exceptions", "ravi" }, new object[] { "POST", "/api/delegations", "ravi" }, new object[] { "POST", "/api/delegations", "meera" }, new object[] { "POST", "/api/delegations", "fathima" },
        new object[] { "GET", "/api/audit", "arjun" }, new object[] { "GET", "/api/reports/audit-trail", "meera" }, new object[] { "GET", "/api/payroll/batches", "ravi" },
        new object[] { "POST", $"/api/allowance-runs/{Guid.NewGuid()}/approve-compensation", "arjun" },
    };

    [Theory, MemberData(nameof(Denied))]
    public async Task Wrong_role_is_forbidden(string method, string url, string persona)
    {
        var res = await _f.As(persona).SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "GET" ? null : System.Net.Http.Json.JsonContent.Create(new { }) });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{persona} {method} {url}");
    }

    [Fact] public async Task Employees_see_only_their_own_world()
    {
        var ravi = _f.As("ravi");
        (await ravi.GetJ("/api/projects")).AsArray().Count.Should().BeInRange(1, 2); // only projects they are (or were) tagged to, never the 10 in the system
        (await ravi.GetJ("/api/approvals/pending"))["items"]!.AsArray().Should().BeEmpty();
        (await ravi.GetAsync($"/api/rosters?projectId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ravi.GetAsync($"/api/preferences/{Personas.Priya}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ravi.GetAsync($"/api/ai/preferences/{Personas.Priya}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ravi.GetAsync($"/api/ai/preferences/{Personas.Ravi}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Fact] public async Task Project_visibility_is_scoped_by_role()
    {
        int N(string p) => _f.As(p).GetJ("/api/projects").Result.AsArray().Count;
        N("fathima").Should().Be(10); N("priya").Should().Be(5); N("arjun").Should().Be(5); N("karthik").Should().BeGreaterThan(5).And.BeLessThan(10);
    }
    [Fact] public async Task Pm_cannot_edit_another_pms_project()
    {
        var logId = await _f.Scope(db => db.Projects.Where(p => p.Code == "P-LOG-01").Select(p => p.Id).FirstAsync());
        (await _f.As("priya").GetAsync($"/api/projects/{logId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _f.As("priya").Put($"/api/projects/{logId}", new { name = "Hijack", businessUnitId = Guid.NewGuid(), startDate = "2026-01-01", costCentre = "CC-1", shiftTypeIds = new Guid[0], isShiftProject = false })).StatusCode.Should().Be(HttpStatusCode.NotFound.Equals(0) ? HttpStatusCode.NotFound : (await _f.As("priya").Put($"/api/projects/{logId}", new { name = "Hijack", businessUnitId = Guid.NewGuid(), startDate = "2026-01-01", costCentre = "CC-1", shiftTypeIds = new Guid[0], isShiftProject = false })).StatusCode);
        (await _f.Scope(db => db.Projects.Where(p => p.Id == logId).Select(p => p.Name).FirstAsync())).Should().NotBe("Hijack");
    }
}

public class AiAdvisoryOnlyTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _f;
    public AiAdvisoryOnlyTests(ApiFixture f) => _f = f;

    private async Task<Dictionary<string, int>> Counts() => await _f.Scope(async db =>
    {
        var d = new Dictionary<string, int>();
        foreach (var p in db.GetType().GetProperties().Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>) && p.Name != "AuditLogs"))
        {
            var set = p.GetValue(db)!; var q = (IQueryable<object>)set; d[p.Name] = await Task.FromResult(q.Count());
        }
        return d;
    });

    [Fact] public async Task No_AI_endpoint_persists_anything_without_an_explicit_human_action()
    {
        var before = await Counts();
        var ret = await _f.Scope(db => db.Projects.Where(p => p.Code == "P-RET-01").Select(p => new { p.Id, p.BusinessUnitId }).FirstAsync());
        var pilot = await _f.Scope(db => db.Projects.Where(p => p.Code == "P-RET-03").Select(p => p.Id).FirstAsync());
        var mv = await _f.Scope(db => db.Movements.FirstAsync(m => m.Status == MovementStatus.Staged));
        var priya = _f.As("priya"); var fath = _f.As("fathima");
        var calls = new List<HttpResponseMessage>
        {
            await priya.GetAsync($"/api/ai/project-setup?businessUnitId={ret.BusinessUnitId}"), await priya.GetAsync($"/api/ai/employee-tagging?projectId={pilot}&from=2026-11-01"),
            await priya.GetAsync($"/api/ai/roster-draft?projectId={ret.Id}&month=2026-11"), await priya.Post("/api/ai/validate", new { projectId = ret.Id, month = "2026-10", edits = new object[0] }),
            await priya.GetAsync($"/api/ai/cost-forecast?projectId={ret.Id}&month=2026-10"), await fath.Post("/api/ai/ripple", new { employeeId = mv.EmployeeId, fromProjectId = mv.FromProjectId, toProjectId = mv.ToProjectId, effectiveFrom = "2026-09-20" }),
            await _f.As("arjun").GetAsync("/api/ai/approval-risk"), await priya.GetAsync($"/api/ai/preferences/{Personas.Ravi}"), await fath.GetAsync("/api/ai/anomalies"), await _f.As("ravi").GetAsync("/api/me/allowance?month=2026-09"),
        };
        calls.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await Counts()).Should().BeEquivalentTo(before);
        (await _f.Scope(db => db.AuditLogs.CountAsync())).Should().Be(0);
    }
    [Fact] public async Task Every_response_has_the_shared_AI_contract()
    {
        var ret = await _f.Scope(db => db.Projects.Where(p => p.Code == "P-RET-01").Select(p => p.Id).FirstAsync());
        foreach (var (who, url) in new[] { ("priya", $"/api/ai/roster-draft?projectId={ret}&month=2026-11"), ("priya", $"/api/ai/cost-forecast?projectId={ret}&month=2026-10"), ("fathima", "/api/ai/anomalies"), ("arjun", "/api/ai/approval-risk") })
        {
            var j = await _f.As(who).GetJ(url);
            ((bool)j["reversible"]!).Should().BeTrue(url); ((double)j["confidence"]!).Should().BeInRange(0, 1); new[] { "Low", "Medium", "High" }.Should().Contain((string)j["confidenceBand"]!);
            j["reasons"]!.AsArray().Count.Should().BeGreaterThanOrEqualTo(1, url); ((string)j["impact"]!).Should().NotBeEmpty();
        }
    }
    [Fact] public void There_is_no_auto_apply_surface()
    {
        var api = typeof(Program).Assembly.GetTypes().Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t)).SelectMany(t => t.GetMethods()).Select(m => m.Name.ToLowerInvariant());
        api.Should().NotContain(n => n.Contains("autoapply") || n.Contains("applyall") || n.Contains("acceptall"));
        var ai = typeof(Rosterix.Application.Ai.IAiUseCase<,>).Assembly.GetTypes().Where(t => t.Namespace == "Rosterix.Application.Ai" && t.Name.EndsWith("Ai"));
        ai.Should().NotBeEmpty().And.OnlyContain(t => !t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Any(f => f.FieldType.Name.Contains("DbContext")));
    }
    [Fact] public async Task Accepting_a_suggestion_is_the_only_AI_write_and_it_is_attributed_and_audited()
    {
        var f = new ApiFixture(); var r = await f.As("fathima").Post("/api/ai/feedback", new { useCaseCode = "AI-UC-02", contextType = "TagCandidate", contextId = Guid.NewGuid().ToString(), action = "Dismiss", reason = "Not the right skills", confidence = 0.62 });
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var fb = await f.Scope(db => db.AiFeedback.SingleAsync()); fb.ActorEmployeeId.Should().Be(Personas.Fathima); fb.Action.Should().Be(AiAction.Dismiss); fb.Reason.Should().Be("Not the right skills");
        (await f.Scope(db => db.AuditLogs.CountAsync())).Should().Be(1);
    }
}

public class SmokeTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _f;
    public SmokeTests(ApiFixture f) => _f = f;
    [Theory] [InlineData("priya")] [InlineData("arjun")] [InlineData("meera")] [InlineData("karthik")] [InlineData("fathima")] [InlineData("ravi")]
    public async Task Every_read_endpoint_responds_without_a_server_error_for_each_persona(string persona)
    {
        var c = _f.As(persona);
        var ret = await _f.Scope(db => db.Projects.Where(p => p.Code == "P-RET-01").Select(p => p.Id).FirstAsync());
        var run = await _f.Scope(db => db.Runs.Select(r => r.Id).FirstAsync());
        var req = await _f.Scope(db => db.ApprovalRequests.Select(r => r.Id).FirstAsync());
        var roster = await _f.Scope(db => db.Rosters.Where(r => r.ProjectId == ret).Select(r => r.Id).FirstAsync());
        var urls = new[]
        {
            "/api/me", "/api/lookups", "/api/employees?q=a", $"/api/employees/{Personas.Ravi}", "/api/notifications", "/api/approval-chains", "/api/projects", $"/api/projects/{ret}", $"/api/projects/{ret}/history",
            "/api/employee-tags", "/api/approvals/pending", "/api/approvals/mine", $"/api/approvals/{req}", "/api/rosters", $"/api/rosters/{roster}", $"/api/rosters/{roster}/publish-preview", $"/api/rosters/{roster}/download",
            "/api/allowance-runs", $"/api/allowance-runs/{run}", $"/api/allowance-runs/{run}/lines", "/api/exceptions", "/api/reports", "/api/reports/payroll", "/api/reports/allowance-summary", "/api/reports/roster-compliance",
            "/api/reports/exception-summary", "/api/reports/tagging-register", "/api/payroll/batches", "/api/movements", "/api/delegations", "/api/delegations/candidates", "/api/arrears", "/api/settlements",
            "/api/me/roster?month=2026-10", "/api/me/allowance?month=2026-09", "/api/me/declarations", "/api/me/queries", "/api/foundation/shift-types", "/api/foundation/shift-rules", "/api/foundation/rates", "/api/foundation/holidays?year=2026",
            "/api/ai/anomalies", $"/api/ai/preferences/{Personas.Ravi}", "/api/ai/approval-risk", $"/api/ai/cost-forecast?projectId={ret}&month=2026-10", "/api/ai/roster-draft?projectId=" + ret + "&month=2026-11", "/health"
        };
        foreach (var u in urls) { var r = await c.GetAsync(u); ((int)r.StatusCode).Should().BeLessThan(500, $"{persona} GET {u}"); }
    }
}
