using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Rosterix.Application.Data;
using Rosterix.Domain;
using Rosterix.Infrastructure.Services;
using Xunit;

namespace Rosterix.Api.Tests;

public class SecurityTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _f;
    public SecurityTests(ApiFixture f) => _f = f;

    private async Task<string> FirstRunId() => (string)(await _f.As("fathima").GetJ("/api/allowance-runs"))[0]!["id"]!;

    // ---- SEC-01
    [Theory] [InlineData("priya")] [InlineData("arjun")] [InlineData("meera")] [InlineData("karthik")] [InlineData("ravi")]
    public async Task SEC01_non_hrss_cannot_approve_compensation(string persona)
    {
        var r = await _f.As(persona).Post($"/api/allowance-runs/{await FirstRunId()}/approve-compensation");
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact] public async Task SEC01_hrss_can_approve_compensation()
    {
        var f = new ApiFixture(); var id = (string)(await f.As("fathima").GetJ("/api/allowance-runs"))[0]!["id"]!;
        (await f.As("fathima").Post($"/api/allowance-runs/{id}/approve-compensation")).StatusCode.Should().Be(HttpStatusCode.OK);
        var run = (await f.As("fathima").GetJ("/api/allowance-runs")).AsArray().First(x => (string)x!["id"]! == id)!;
        ((string)run["status"]!).Should().Be("CompensationApproved");
    }
    [Theory] [InlineData("priya")] [InlineData("arjun")] [InlineData("karthik")] [InlineData("meera")]
    public async Task SEC01_compensation_cannot_be_decided_through_the_generic_approvals_endpoint_either(string persona)
    {
        var comp = await _f.Scope(db => db.ApprovalRequests.Where(r => r.Type == ApprovalType.AllowanceCompensation && r.Status == ApprovalStatus.Pending).Select(r => r.Id).FirstAsync());
        var r = await _f.As(persona).Post($"/api/approvals/{comp}/decide", new { approve = true, comment = "ok" });
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact] public void SEC01_delegation_scope_cannot_express_compensation() => Enum.GetNames<DelegationScope>().Should().NotContain(n => n.Contains("Compensation"));

    // ---- SEC-02
    [Theory] [InlineData("POST")] [InlineData("PUT")] [InlineData("PATCH")] [InlineData("DELETE")]
    public async Task SEC02_approval_chains_have_no_write_verb(string verb)
    {
        var req = new HttpRequestMessage(new HttpMethod(verb), "/api/approval-chains") { Content = JsonContent.Create(new { }) };
        (await _f.As("fathima").SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
    [Fact] public async Task SEC02_allowance_lines_and_runs_have_no_modify_endpoints()
    {
        var id = await FirstRunId(); var line = (string)(await _f.As("fathima").GetJ($"/api/allowance-runs/{id}/lines"))["rows"]![0]!["id"]!;
        foreach (var (verb, url) in new[] { ("PUT", $"/api/allowance-runs/{id}"), ("PATCH", $"/api/allowance-runs/{id}"), ("DELETE", $"/api/allowance-runs/{id}"), ("PUT", $"/api/allowance-runs/{id}/lines/{line}"), ("PATCH", $"/api/allowance-runs/{id}/lines/{line}"), ("DELETE", $"/api/allowance-runs/{id}/lines/{line}") })
            (await _f.As("fathima").SendAsync(new HttpRequestMessage(new HttpMethod(verb), url) { Content = JsonContent.Create(new { amount = 1 }) })).StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);
    }
    [Fact] public async Task SEC02_chains_are_readable_and_match_the_brief()
    {
        var chains = (await _f.As("priya").GetJ("/api/approval-chains")).AsArray();
        chains.First(c => (string)c!["requestType"]! == "ProjectShiftTagging")!["steps"]!.AsArray().Select(s => (string)s!).Should().Equal("LM", "HRBP", "RH");
        chains.First(c => (string)c!["requestType"]! == "AllowanceCompensation")!["steps"]!.AsArray().Select(s => (string)s!).Should().Equal("HRSS");
    }

    // ---- SEC-03
    [Fact] public async Task SEC03_delegation_to_a_subordinate_is_400()
    {
        var deepa = await _f.Scope(db => db.Employees.Where(e => e.FirstName == "Deepa").Select(e => e.Id).FirstAsync());
        var r = await _f.As("priya").Post("/api/delegations", new { toEmployeeId = deepa, scope = "ExceptionReview", validFrom = "2026-10-11", validTo = "2026-10-20", reason = "Leave" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest); ((string)(await r.J())["title"]!).Should().Contain("reports to you");
    }
    [Fact] public async Task SEC03_delegation_to_a_different_grade_or_role_is_400()
    {
        var sunita = Personas.Gid("emp:sunita");
        (await _f.As("arjun").Post("/api/delegations", new { toEmployeeId = sunita, scope = "ProjectApproval", validFrom = "2026-10-20", validTo = "2026-10-25", reason = "Leave" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _f.As("arjun").Post("/api/delegations", new { toEmployeeId = Personas.Priya, scope = "ProjectApproval", validFrom = "2026-10-20", validTo = "2026-10-25", reason = "Leave" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
    [Fact] public async Task SEC03_delegation_to_a_same_level_peer_works_and_notifies()
    {
        var f = new ApiFixture();
        var r = await f.As("arjun").Post("/api/delegations", new { toEmployeeId = Personas.Gid("emp:nikhil"), scope = "ExceptionReview", validFrom = "2026-10-11", validTo = "2026-10-18", reason = "Travelling" });
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.As("arjun").Post("/api/delegations", new { toEmployeeId = Personas.Gid("emp:nikhil"), scope = "ExceptionReview", validFrom = "2026-10-12", validTo = "2026-10-14", reason = "Dup" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
    [Fact] public async Task SEC03_only_pm_and_lm_can_delegate() => (await _f.As("ravi").Post("/api/delegations", new { toEmployeeId = Personas.Priya, scope = "ExceptionReview", validFrom = "2026-10-11", validTo = "2026-10-12", reason = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    [Fact] public async Task SEC03_delegate_can_act_for_the_delegator_only_within_scope()
    {
        var f = new ApiFixture();
        var p01 = await f.Scope(db => db.ApprovalRequests.Where(r => r.Title.StartsWith("Shift-tag Click-and-collect")).Select(r => r.Id).FirstAsync());
        var q = await f.As(Personas.Gid("emp:nikhil").ToString()).GetJ("/api/approvals/pending");
        q["items"]!.AsArray().Select(i => (string)i!["id"]!).Should().Contain(p01.ToString());
        var ok = await f.As(Personas.Gid("emp:nikhil").ToString()).Post($"/api/approvals/{p01}/decide", new { approve = true, comment = "Covering for Arjun" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK); ((bool)(await ok.J())["viaDelegation"]!).Should().BeTrue();
        // tagging scope was not delegated
        var tag = await f.Scope(db => db.ApprovalRequests.Where(r => r.Title.StartsWith("Tag 8 people")).Select(r => r.Id).FirstAsync());
        (await f.As(Personas.Gid("emp:nikhil").ToString()).Post($"/api/approvals/{tag}/decide", new { approve = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- SEC-06
    [Fact] public void SEC06_sensitive_fields_are_masked_in_audit_detail()
    {
        var m = Masking.Mask("{\"password\":\"hunter2\",\"amount\":1200,\"email\":\"a@b.c\",\"employeeNumber\":\"E1001\",\"token\":\"abc\",\"nested\":{\"Secret\":\"s\"},\"name\":\"ok\"}");
        m.Should().NotContain("hunter2").And.NotContain("1200").And.NotContain("a@b.c").And.NotContain("E1001").And.NotContain("abc");
        m.Should().Contain("\"name\":\"ok\"").And.Contain("***");
    }

    // ---- SEC-07
    [Fact] public async Task SEC07_every_state_change_writes_an_audit_row_with_actor_ip_and_endpoint()
    {
        var f = new ApiFixture(); var c = f.As("priya");
        var before = await f.Scope(db => db.AuditLogs.CountAsync());
        var res = await c.Post("/api/projects", new { name = "Audit probe", businessUnitId = await f.Scope(db => db.BusinessUnits.Select(b => b.Id).FirstAsync()), startDate = "2026-11-01", costCentre = "CC-9", shiftTypeIds = new Guid[0], isShiftProject = false });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var row = await f.Scope(db => db.AuditLogs.OrderByDescending(a => a.OccurredAtUtc).FirstAsync());
        (await f.Scope(db => db.AuditLogs.CountAsync())).Should().Be(before + 1);
        row.ActorEmployeeId.Should().Be(Personas.Priya); row.Succeeded.Should().BeTrue(); row.Endpoint.Should().Be("/api/projects"); row.OriginIp.Should().NotBeNullOrEmpty(); row.EventType.Should().StartWith("POST");
        row.OccurredAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1)); row.OccurredAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }
    [Fact] public async Task SEC07_failed_and_denied_changes_are_audited_as_failures()
    {
        var f = new ApiFixture();
        await f.As("priya").Post("/api/projects", new { name = "", businessUnitId = Guid.NewGuid(), startDate = "2026-11-01", costCentre = "", shiftTypeIds = new Guid[0], isShiftProject = false }); // 400
        await f.As("ravi").Post($"/api/allowance-runs/{Guid.NewGuid()}/approve-compensation");                                                                                                       // 403
        var rows = await f.Scope(db => db.AuditLogs.OrderBy(a => a.OccurredAtUtc).ToListAsync());
        rows.Should().HaveCount(2); rows.Should().OnlyContain(r => !r.Succeeded); rows.Last().EventType.Should().Contain("DENIED");
    }
    [Fact] public async Task SEC07_reads_and_previews_are_not_audited()
    {
        var f = new ApiFixture(); var c = f.As("priya"); var ret = await f.Scope(db => db.Projects.Where(p => p.Code == "P-RET-01").Select(p => p.Id).FirstAsync());
        await c.GetAsync("/api/projects"); await c.Post("/api/ai/validate", new { projectId = ret, month = "2026-10", edits = new object[0] });
        (await f.Scope(db => db.AuditLogs.CountAsync())).Should().Be(0);
    }
    [Fact] public async Task SEC07_audit_masks_sensitive_values_in_the_stored_detail()
    {
        var f = new ApiFixture();
        await f.As("priya").Post("/api/arrears", new { employeeId = Personas.Ravi, projectId = await f.Scope(db => db.Projects.Select(p => p.Id).FirstAsync()), forPeriodMonth = "2026-09", amount = 98765, reason = "probe" });
        var detail = (await f.Scope(db => db.AuditLogs.OrderByDescending(a => a.OccurredAtUtc).FirstAsync())).Detail; detail.Should().NotContain("98765");
    }
    [Fact] public async Task SEC07_audit_trail_is_hrss_only()
    {
        (await _f.As("fathima").GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var p in new[] { "priya", "arjun", "meera", "karthik", "ravi" }) (await _f.As(p).GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _f.As("priya").GetAsync("/api/reports/audit-trail")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- SEC-08
    [Fact] public async Task SEC08_unauthenticated_and_unknown_personas_are_rejected()
    {
        (await _f.CreateClient().GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _f.As("nobody").GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _f.CreateClient().GetAsync("/api/personas")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Fact] public async Task SEC08_dev_auth_is_not_available_outside_development()
    {
        using var prod = new ApiFixture { Env = "Production" };
        var c = prod.As("fathima");
        var r = await c.GetAsync("/api/me");
        r.StatusCode.Should().NotBe(HttpStatusCode.OK);
        var p = await prod.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/api/personas");
        if (p.StatusCode == HttpStatusCode.OK) (await p.J()).AsArray().Should().BeEmpty();
    }

    // ---- SEC-04/09/10 and the repo hygiene gates
    static string Root { get { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !File.Exists(Path.Combine(d.FullName, "Rosterix.sln"))) d = d.Parent; return d!.FullName; } }
    [Fact] public void SEC04_no_secrets_in_source_or_config()
    {
        var patterns = new[] { new System.Text.RegularExpressions.Regex(@"AKIA[0-9A-Z]{16}"), new(@"-----BEGIN (RSA |EC )?PRIVATE KEY-----"), new(@"(?i)password\s*=\s*[^;'""\s{$][^;'""]{3,}"), new(@"(?i)(api[_-]?key|client[_-]?secret)\s*[:=]\s*['""][A-Za-z0-9/+_\-]{12,}") };
        var files = Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains("node_modules") && !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}.angular{Path.DirectorySeparatorChar}") && !p.Contains("SecurityTests.cs") && !p.EndsWith(".env.example") && new[] { ".cs", ".json", ".yml", ".yaml", ".ts", ".html", ".md", ".sh", ".props", ".csproj" }.Contains(Path.GetExtension(p))).ToList();
        files.Should().NotBeEmpty();
        var hits = files.SelectMany(p => patterns.Where(rx => rx.IsMatch(File.ReadAllText(p))).Select(rx => $"{p}: {rx}")).ToList();
        hits.Should().BeEmpty();
    }
    [Fact] public void SEC09_containers_run_as_non_root()
    {
        foreach (var d in new[] { "deploy/Dockerfile.api", "deploy/Dockerfile.web" }) File.ReadAllText(Path.Combine(Root, d)).Should().MatchRegex(@"(?m)^USER\s+(app|\d+|nginx|node)\s*$", d);
    }
    [Fact] public void SEC05_tls12_minimum_and_hsts_are_configured()
    {
        var program = File.ReadAllText(Path.Combine(Root, "src/Rosterix.Api/Program.cs"));
        program.Should().Contain("Tls12").And.Contain("UseHsts").And.Contain("UseHttpsRedirection");
        program.Should().NotContain("Tls11").And.NotContain("Ssl3");
    }
    [Fact] public async Task SEC10_exports_are_streamed_not_persisted()
    {
        var before = Directory.GetFiles(Path.GetTempPath()).Length;
        var r = await _f.As("fathima").GetAsync("/api/reports/payroll?format=csv"); r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        Directory.GetFiles(Path.GetTempPath()).Length.Should().BeLessOrEqualTo(before + 2);
    }
}
