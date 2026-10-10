using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Rosterix.Application.Data;
using Rosterix.Domain;
using Xunit;

namespace Rosterix.Api.Tests;

public class FlowTests
{
    static async Task<Guid> Proj(ApiFixture f, string code) => await f.Scope(db => db.Projects.Where(p => p.Code == code).Select(p => p.Id).FirstAsync());

    [Fact] public async Task PM_to_LM_to_HRBP_to_RH_shift_tagging_journey_completes()
    {
        var f = new ApiFixture(); var pm = f.As("priya"); var draft = await Proj(f, "P-RET-04");
        var res = await pm.Post($"/api/projects/{draft}/shift-tag"); res.StatusCode.Should().Be(HttpStatusCode.OK); var req = (string)(await res.J())["requestId"]!;
        (await f.As("karthik").GetJ($"/api/approvals/pending"))["items"]!.AsArray().Select(i => (string)i!["id"]!).Should().NotContain(req); // not yet at RH
        foreach (var who in new[] { "arjun", "meera", "karthik" })
        {
            var q = await f.As(who).GetJ("/api/approvals/pending"); q["items"]!.AsArray().Select(i => (string)i!["id"]!).Should().Contain(req, who);
            var d = await f.As(who).Post($"/api/approvals/{req}/decide", new { approve = true, comment = "Approved" }); d.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        var proj = await f.As("priya").GetJ($"/api/projects/{draft}"); ((string)proj["project"]!["status"]!).Should().Be("Active"); ((bool)proj["project"]!["isShiftProject"]!).Should().BeTrue();
        var detail = await f.As("priya").GetJ($"/api/approvals/{req}"); detail["steps"]!.AsArray().Should().OnlyContain(s => (string)s!["decision"]! == "Approved");
        (await f.As("priya").GetJ("/api/notifications")).AsArray().Select(n => (string)n!["title"]!).Should().Contain(t => t.StartsWith("Approved:"));
    }
    [Fact] public async Task Rejection_needs_a_reason_and_returns_the_project_to_draft()
    {
        var f = new ApiFixture(); var draft = await Proj(f, "P-RET-04");
        var req = (string)(await (await f.As("priya").Post($"/api/projects/{draft}/shift-tag")).J())["requestId"]!;
        (await f.As("arjun").Post($"/api/approvals/{req}/decide", new { approve = false })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.As("arjun").Post($"/api/approvals/{req}/decide", new { approve = false, comment = "Dates don't match the plan" })).StatusCode.Should().Be(HttpStatusCode.OK);
        ((string)(await f.As("priya").GetJ($"/api/projects/{draft}"))["project"]!["status"]!).Should().Be("Draft");
        (await f.As("arjun").Post($"/api/approvals/{req}/decide", new { approve = true })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
    [Fact] public async Task A_person_cannot_decide_a_step_assigned_to_someone_else()
    {
        var f = new ApiFixture(); var req = await f.Scope(db => db.ApprovalRequests.Where(r => r.Title.StartsWith("Shift-tag Click-and-collect")).Select(r => r.Id).FirstAsync());
        (await f.As("meera").Post($"/api/approvals/{req}/decide", new { approve = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await f.As("ravi").Post($"/api/approvals/{req}/decide", new { approve = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact] public async Task Queue_is_risk_sorted_with_an_explicit_override()
    {
        var f = new ApiFixture(); var risk = await f.As("karthik").GetJ("/api/approvals/pending");
        var scores = risk["items"]!.AsArray().Select(i => (double)i!["risk"]!["score"]!).ToList(); scores.Should().BeInDescendingOrder(); scores.First().Should().BeGreaterThan(0.5);
        var oldest = await f.As("karthik").GetJ("/api/approvals/pending?sort=oldest"); ((string)oldest["sort"]!).Should().Be("oldest");
        oldest["items"]!.AsArray().Select(i => DateTime.Parse((string)i!["raisedAtUtc"]!)).Should().BeInAscendingOrder();
    }
    [Fact] public async Task Bulk_approve_requires_every_item_to_have_been_reviewed()
    {
        var f = new ApiFixture(); var items = (await f.As("arjun").GetJ("/api/approvals/pending"))["items"]!.AsArray().Select(i => (string)i!["id"]!).ToList(); items.Count.Should().BeGreaterThan(1);
        (await f.As("arjun").Post("/api/approvals/bulk-approve", new { ids = items, reviewedIds = items.Take(1) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var ok = await f.As("arjun").Post("/api/approvals/bulk-approve", new { ids = items, reviewedIds = items }); ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.J())["results"]!.AsArray().Should().OnlyContain(r => (bool)r!["ok"]!);
    }
    [Fact] public async Task Approval_detail_carries_inline_context_for_the_reviewer()
    {
        var f = new ApiFixture(); var overlap = await f.Scope(db => db.ApprovalRequests.Where(r => r.Title.StartsWith("Tag 3 people from Festive")).Select(r => r.Id).FirstAsync());
        var d = await f.As("meera").GetJ($"/api/approvals/{overlap}");
        ((int)d["context"]!["overlapCount"]!).Should().Be(3); d["diff"]!.AsArray().Should().HaveCount(3); d["risk"]!["reasons"]!.AsArray().Should().NotBeEmpty();
        ((int)d["requesterHistory"]!["total"]!).Should().BeGreaterThan(3); ((bool)d["canDecide"]!).Should().BeTrue();
    }

    // ---- tagging
    [Fact] public async Task Tagging_validates_overlaps_then_raises_one_approval()
    {
        var f = new ApiFixture(); var ret1 = await Proj(f, "P-RET-01"); var ret2 = await Proj(f, "P-RET-02");
        var busy = await f.Scope(db => db.EmployeeTags.Where(t => t.ProjectId == ret2 && t.Status == TagStatus.Approved).Select(t => t.EmployeeId).FirstAsync());
        var bad = await f.As("priya").Post("/api/employee-tags", new { projectId = ret1, employeeIds = new[] { busy }, from = "2026-10-20" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var bu = await f.Scope(db => db.Projects.Where(p => p.Id == ret1).Select(p => p.BusinessUnitId).FirstAsync());
        var free = await f.Scope(async db => { var l = new List<Guid>(); for (var i = 0; i < 2; i++) { var e = new Employee { EmployeeNumber = "E9" + i + "00", FirstName = "New", LastName = "Joiner" + i, Email = $"new{i}@rosterix.example", BusinessUnitId = bu, IsShiftEligible = true, JoinedOn = new DateOnly(2026, 10, 1) }; db.Employees.Add(e); l.Add(e.Id); } await db.SaveChangesAsync(); return l; });
        var ok = await f.As("priya").Post("/api/employee-tags", new { projectId = ret1, employeeIds = free, from = "2026-11-02" }); ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Scope(db => db.EmployeeTags.CountAsync(t => t.Status == TagStatus.PendingApproval && free.Contains(t.EmployeeId)))).Should().Be(free.Count);
    }
    [Fact] public async Task Bulk_tag_csv_reports_row_level_errors()
    {
        var f = new ApiFixture(); var ret1 = await Proj(f, "P-RET-01");
        var res = await f.As("priya").Post("/api/employee-tags/bulk-validate", new { projectId = ret1, rows = new object[] { new { employeeNumber = "E9999", from = "2026-11-01" }, new { employeeNumber = "E1020", from = "2026-11-10", to = "2026-11-01" } } });
        var rows = (await res.J()).AsArray(); rows.Should().HaveCount(2); rows.Should().OnlyContain(r => !(bool)r!["ok"]!); ((string)rows[0]!["messages"]![0]!).Should().Contain("No employee");
    }

    // ---- roster
    static async Task<(string id, JsonNode grid)> Roster(ApiFixture f, string who, string code, string month)
    {
        var list = await f.As(who).GetJ($"/api/rosters?projectId={await Proj(f, code)}&month={month}"); var id = (string)list[0]!["id"]!; return (id, await f.As(who).GetJ($"/api/rosters/{id}"));
    }
    [Fact] public async Task Roster_edit_that_breaks_a_blocking_rule_is_refused_with_an_explanation()
    {
        var f = new ApiFixture(); var (id, grid) = await Roster(f, "priya", "P-RET-01", "2026-10");
        var emp = (string)grid["employees"]![0]!["id"]!; var morn = await f.Scope(db => db.ShiftTypes.Where(s => s.Code == "MORN").Select(s => s.Id).FirstAsync());
        var edits = Enumerable.Range(19, 13).Select(d => new { employeeId = emp, workDate = $"2026-10-{d:00}", shiftTypeId = morn, entryType = "Shift" }).ToArray();
        var r = await f.As("priya").Put($"/api/rosters/{id}/entries", new { edits }); r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await r.J(); ((string)body["title"]!).Should().Contain("blocking rule"); body["extra"]!["violations"]!.AsArray().Select(v => (string)v!["ruleCode"]!).Should().Contain("BR-02");
    }
    [Fact] public async Task Roster_warning_needs_acknowledgement_then_saves_and_is_logged()
    {
        var f = new ApiFixture(); var (id, grid) = await Roster(f, "priya", "P-RET-02", "2026-10");
        var emp = (string)grid["employees"]![0]!["id"]!; var gen = await f.Scope(db => db.ShiftTypes.Where(s => s.Code == "GEN").Select(s => s.Id).FirstAsync());
        var edit = new[] { new { employeeId = emp, workDate = "2026-10-20", shiftTypeId = gen, entryType = "Shift" } }; // Dussehra: a restricted holiday
        var first = await f.As("priya").Put($"/api/rosters/{id}/entries", new { edits = edit }); first.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await first.J())["extra"]!["needsAcknowledgement"]!.AsArray().Select(v => (string)v!["ruleCode"]!).Should().Contain("BR-06");
        var ok = await f.As("priya").Put($"/api/rosters/{id}/entries", new { edits = edit, acknowledged = new[] { "BR-06" }, reason = "Festival cover" }); ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var log = await f.Scope(db => db.RosterChangeLogs.OrderByDescending(l => l.ChangedAtUtc).FirstAsync());
        log.Reason.Should().Contain("Acknowledged BR-06").And.Contain("Festival cover"); log.Before.Should().Be("Holiday"); log.After.Should().Be("General"); log.ChangedBy.Should().Be(Personas.Priya.ToString());
    }
    [Fact] public async Task Roster_publish_is_a_distinct_confirmed_action_that_notifies_staff()
    {
        var f = new ApiFixture(); var anil = Personas.Gid("emp:anil").ToString();
        var (id, _) = await Roster(f, anil, "P-LOG-01", "2026-10");
        (await f.As(anil).Post($"/api/rosters/{id}/publish", new { confirm = false })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var preview = await f.As(anil).GetJ($"/api/rosters/{id}/publish-preview"); ((int)preview["affectedCount"]!).Should().BeGreaterThan(5);
        var r = await f.As(anil).Post($"/api/rosters/{id}/publish", new { confirm = true, acknowledged = new[] { "BR-06", "BR-07" } });
        var body = await r.J(); r.StatusCode.Should().Be(HttpStatusCode.OK, body.ToJsonString()); ((int)body["notified"]!).Should().BeGreaterThan(5);
        (await f.As(anil).Post($"/api/rosters/{id}/publish", new { confirm = true })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await f.Scope(db => db.Notifications.CountAsync(n => n.Type == "RosterPublished" && n.Title.Contains("October")))).Should().BeGreaterThan(5);
    }
    [Fact] public async Task Roster_with_blocking_conflicts_cannot_be_published()
    {
        var f = new ApiFixture(); var (id, _) = await Roster(f, "priya", "P-RET-01", "2026-10");
        var r = await f.As("priya").Post($"/api/rosters/{id}/publish", new { confirm = true, acknowledged = new[] { "BR-06", "BR-07" } }); r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ((string)(await r.J())["title"]!).Should().Contain("blocking");
    }
    [Fact] public async Task Roster_csv_upload_validates_per_row_and_download_round_trips()
    {
        var f = new ApiFixture(); var (id, grid) = await Roster(f, "priya", "P-RET-02", "2026-10");
        var no = await f.Scope(db => db.Employees.Where(e => e.Id == Guid.Parse((string)grid["employees"]![0]!["id"]!)).Select(e => e.EmployeeNumber).FirstAsync());
        var csv = $"employeeNumber,date,shift\n{no},2026-10-05,GEN\nE0000,2026-10-06,GEN\n{no},2026-11-01,GEN\n{no},2026-10-07,ZZZ\n";
        var v = await (await f.As("priya").Post($"/api/rosters/{id}/upload?commit=false", new { csv })).J();
        ((int)v["validRows"]!).Should().Be(1); ((int)v["errorRows"]!).Should().Be(3); ((bool)v["committed"]!).Should().BeFalse();
        (await f.As("priya").Post($"/api/rosters/{id}/upload?commit=true", new { csv })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var dl = await f.As("priya").GetAsync($"/api/rosters/{id}/download"); dl.StatusCode.Should().Be(HttpStatusCode.OK); (await dl.Content.ReadAsStringAsync()).Should().StartWith("employeeNumber,employeeName,date,shift");
    }
    [Fact] public async Task Only_roster_builders_can_edit_and_employees_cannot()
    {
        var f = new ApiFixture(); var (id, grid) = await Roster(f, "priya", "P-RET-01", "2026-10");
        (await f.As("ravi").Put($"/api/rosters/{id}/entries", new { edits = new object[] { new { employeeId = Personas.Ravi, workDate = "2026-10-20", entryType = "WeeklyOff" } } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await f.As("meera").Put($"/api/rosters/{id}/entries", new { edits = new object[] { new { employeeId = Personas.Ravi, workDate = "2026-10-20", entryType = "WeeklyOff" } } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ((bool)grid["canEdit"]!).Should().BeTrue(); ((bool)(await f.As("meera").GetJ($"/api/rosters/{id}"))["canEdit"]!).Should().BeFalse();
    }
    [Fact] public async Task Accepting_an_AI_draft_creates_a_draft_roster_and_never_publishes()
    {
        var f = new ApiFixture(); var ret1 = await Proj(f, "P-RET-01");
        var sug = await f.As("priya").GetJ($"/api/ai/roster-draft?projectId={ret1}&month=2026-11");
        (await f.Scope(db => db.Rosters.AnyAsync(r => r.ProjectId == ret1 && r.PeriodMonth == "2026-11"))).Should().BeFalse(); // suggesting writes nothing
        var entries = sug["payload"]!["entries"]!.AsArray().Select(e => new { employeeId = (string)e!["employeeId"]!, workDate = (string)e["workDate"]!, shiftTypeId = (string?)e["shiftTypeId"], entryType = (string)e["entryType"]! }).ToArray();
        var r = await f.As("priya").Post("/api/rosters/accept-draft", new { projectId = ret1, month = "2026-11", entries, confidence = (double)sug["confidence"]! }); r.StatusCode.Should().Be(HttpStatusCode.OK);
        var roster = await f.Scope(db => db.Rosters.FirstAsync(x => x.ProjectId == ret1 && x.PeriodMonth == "2026-11"));
        roster.Status.Should().Be(RosterStatus.Draft); roster.Source.Should().Be(RosterSource.AiDraft);
        (await f.Scope(db => db.RosterEntries.CountAsync(e => e.RosterId == roster.Id && e.IsAiSuggested))).Should().Be(entries.Length);
        (await f.Scope(db => db.AiFeedback.CountAsync(a => a.UseCaseCode == "AI-UC-03" && a.Action == AiAction.Accept))).Should().Be(1);
    }

    // ---- allowance, exceptions, payroll
    [Fact] public async Task Calculate_process_approve_then_payroll_is_gated_by_the_checklist()
    {
        var f = new ApiFixture(); var ret1 = await Proj(f, "P-RET-01");
        var calc = await f.As("priya").Post("/api/allowance-runs/calculate", new { projectId = ret1, month = "2026-08" }); calc.StatusCode.Should().Be(HttpStatusCode.OK); var aug = (string)(await calc.J())["id"]!;
        (await f.As("priya").Post("/api/allowance-runs/calculate", new { projectId = ret1, month = "2026-10" })).StatusCode.Should().Be(HttpStatusCode.BadRequest); // roster not published
        (await f.As("priya").Post($"/api/allowance-runs/{aug}/process")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.As("priya").Post($"/api/allowance-runs/{aug}/approve-compensation")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await f.As("fathima").Post($"/api/allowance-runs/{aug}/approve-compensation")).StatusCode.Should().Be(HttpStatusCode.OK);
        var lines = await f.As("ravi").GetAsync($"/api/allowance-runs/{aug}/lines"); lines.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        var cl = await f.As("fathima").GetJ("/api/payroll/checklist?month=2026-09");
        cl["checks"]!.AsArray().Should().Contain(c => (string)c!["id"]! == "pending" && (string)c["status"]! == "Warn"); // RET-01 September is still waiting for approval
        var sep = cl["runs"]!.AsArray().First(r => (bool)r!["selectable"]!)["id"]!.ToString();
        (await f.As("fathima").Post("/api/payroll/batches", new { month = "2026-09", runIds = new[] { sep } })).StatusCode.Should().Be(HttpStatusCode.Conflict); // warning needs an explicit acknowledgement
        (await f.As("priya").Post("/api/payroll/batches", new { month = "2026-09", runIds = new[] { sep } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact] public async Task Payroll_batch_submits_once_anomalies_are_dismissed_with_a_reason_and_never_rewrites_on_rate_change()
    {
        var f = new ApiFixture(); var h = f.As("fathima");
        var pend0 = await f.Scope(db => db.Runs.Where(r => r.PeriodMonth == "2026-09" && r.Status == RunStatus.PendingCompensation).Select(r => r.Id).FirstAsync());
        (await h.Post($"/api/allowance-runs/{pend0}/approve-compensation")).StatusCode.Should().Be(HttpStatusCode.OK);
        var gate = await h.GetJ("/api/payroll/checklist?month=2026-09"); ((bool)gate["canSubmit"]!).Should().BeFalse();
        gate["checks"]!.AsArray().Should().Contain(c => (string)c!["id"]! == "anomalies" && (string)c["status"]! == "Fail"); // duplicate + rate mismatch block payroll
        var an = (await h.GetJ("/api/ai/anomalies"))["payload"]!["items"]!.AsArray().Where(i => (string)i!["kind"]! is "Duplicate claim" or "Rate mismatch").ToList(); an.Should().HaveCount(2);
        (await h.Post("/api/ai/feedback", new { useCaseCode = "AI-UC-09", contextType = "Anomaly", contextId = (string)an[0]!["id"]!, action = "Dismiss" })).StatusCode.Should().Be(HttpStatusCode.BadRequest); // reason required
        foreach (var a in an) (await h.Post("/api/ai/feedback", new { useCaseCode = "AI-UC-09", contextType = "Anomaly", contextId = (string)a!["id"]!, action = "Dismiss", reason = "Verified with the roster desk" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var blocked = await h.GetJ("/api/payroll/checklist?month=2026-09"); ((bool)blocked["canSubmit"]!).Should().BeTrue(); // anomalies already dismissed above
        var cl = await h.GetJ("/api/payroll/checklist?month=2026-09"); cl["checks"]!.AsArray().Where(c => (string)c!["status"]! == "Fail").Should().BeEmpty();
        var runs = cl["runs"]!.AsArray().Where(r => (bool)r!["selectable"]!).Select(r => (string)r!["id"]!).ToArray(); runs.Should().HaveCount(2);
        var total = await f.Scope(db => db.Lines.Where(l => db.Runs.Any(r => r.Id == l.AllowanceRunId && r.PeriodMonth == "2026-09")).SumAsync(l => l.Amount));
        var sub = await h.Post("/api/payroll/batches", new { month = "2026-09", runIds = runs, acknowledgeWarnings = true }); sub.StatusCode.Should().Be(HttpStatusCode.OK, (await sub.J()).ToJsonString());
        (await f.Scope(db => db.Runs.CountAsync(r => r.PeriodMonth == "2026-09" && r.Status == RunStatus.Submitted))).Should().Be(2);
        // a later rate revision adds a row but never touches submitted lines
        var night = await f.Scope(db => db.ShiftTypes.Where(s => s.Code == "NIGHT_A").Select(s => s.Id).FirstAsync());
        var ret = await f.Scope(db => db.BusinessUnits.Where(b => b.Code == "RET").Select(b => b.Id).FirstAsync());
        (await h.Post("/api/foundation/rates", new { shiftTypeId = night, businessUnitId = ret, amount = 600, effectiveFrom = "2026-11-01" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Scope(db => db.Lines.Where(l => db.Runs.Any(r => r.Id == l.AllowanceRunId && r.PeriodMonth == "2026-09")).SumAsync(l => l.Amount))).Should().Be(total);
        (await h.Post("/api/payroll/batches", new { month = "2026-09", runIds = runs, acknowledgeWarnings = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest); // can't submit twice
    }
    [Fact] public async Task Exceptions_can_be_acknowledged_in_bulk_and_resolved_with_a_reason()
    {
        var f = new ApiFixture(); var items = (await f.As("meera").GetJ("/api/exceptions?status=Open")).AsArray().Take(3).Select(e => (string)e!["id"]!).ToList(); items.Should().HaveCount(3);
        var r = await f.As("meera").Post("/api/exceptions/bulk-acknowledge", new { ids = items }); ((int)(await r.J())["acknowledged"]!).Should().Be(3);
        (await f.As("meera").Post($"/api/exceptions/{items[0]}/resolve", new { resolution = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.As("meera").Post($"/api/exceptions/{items[0]}/resolve", new { resolution = "Confirmed with PM" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await f.As("ravi").GetAsync("/api/exceptions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- movements, arrears, settlement, self-service, foundation
    [Fact] public async Task Backdated_movement_shows_a_ripple_and_needs_a_two_step_commit_with_approval()
    {
        var f = new ApiFixture(); var h = f.As("fathima"); var mv = (await h.GetJ("/api/movements")).AsArray().First(m => (string)m!["status"]! == "Staged"); var id = (string)mv["id"]!;
        (await h.Post($"/api/movements/{id}/commit")).StatusCode.Should().Be(HttpStatusCode.Conflict); // backdated: approval first
        var ret2 = await Proj(f, "P-RET-02"); var ret1 = await Proj(f, "P-RET-01"); var emp = await f.Scope(db => db.Movements.Where(m => m.Id == Guid.Parse(id)).Select(m => m.EmployeeId).FirstAsync());
        var rip = await h.Post("/api/ai/ripple", new { employeeId = emp, fromProjectId = ret2, toProjectId = ret1, effectiveFrom = "2026-09-20" }); var body = await rip.J();
        body["payload"]!["rows"]!.AsArray().Count.Should().BeGreaterThan(3); ((string)body["impact"]!).Should().Contain("preview");
        (await f.As("ravi").Post("/api/ai/ripple", new { employeeId = emp, toProjectId = ret1, effectiveFrom = "2026-09-20" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var req = await f.Scope(db => db.ApprovalRequests.Where(r => r.Type == ApprovalType.Movement && r.SubjectId == Guid.Parse(id)).Select(r => r.Id).FirstAsync());
        (await f.As("meera").Post($"/api/approvals/{req}/decide", new { approve = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.Post($"/api/approvals/{req}/decide", new { approve = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        var c = await h.Post($"/api/movements/{id}/commit"); c.StatusCode.Should().Be(HttpStatusCode.OK, (await c.J()).ToJsonString());
        (await f.Scope(db => db.EmployeeTags.AnyAsync(t => t.EmployeeId == emp && t.ProjectId == ret1 && t.SourceMovementId == Guid.Parse(id)))).Should().BeTrue();
        (await f.Scope(db => db.Arrears.AnyAsync(a => a.EmployeeId == emp && a.Reason.Contains("movement")))).Should().BeTrue(); // submitted lines → arrear exposure
    }
    [Fact] public async Task Arrears_route_by_age_bucket()
    {
        var f = new ApiFixture(); var all = (await f.As("fathima").GetJ("/api/arrears")).AsArray();
        foreach (var (bucket, route) in new[] { ("0-3", "HRBP"), ("4-6", "Regional Head"), (">6", "Regional Head, then HRSS") })
            all.Where(a => (string)a!["bucket"]! == bucket).Should().OnlyContain(a => (string)a!["route"]! == route).And.NotBeEmpty();
        var raised = all.Where(a => (bool)a!["canRoute"]!).Select(a => a!).ToList();
        var old = raised.First(a => (string)a["bucket"]! == "4-6"); var r = await f.As("priya").Post($"/api/arrears/{(string)old["id"]!}/route");
        r.StatusCode.Should().Be(HttpStatusCode.OK); var req = (string)(await r.J())["requestId"]!;
        var detail = await f.As("karthik").GetJ($"/api/approvals/{req}"); detail["steps"]!.AsArray().Select(s => (string)s!["role"]!).Should().Equal("RH");
    }
    [Fact] public async Task Settlement_blocks_clearance_until_pending_items_are_acknowledged()
    {
        var f = new ApiFixture(); var s = (await f.As("fathima").GetJ("/api/settlements")).AsArray()[0]!; var id = (string)s["id"]!;
        ((decimal)s["pendingAllowance"]!).Should().BeGreaterThan(0);
        (await f.As("fathima").Post($"/api/settlements/{id}/clear", new { acknowledgePending = false })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await f.As("fathima").Post($"/api/settlements/{id}/clear", new { acknowledgePending = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.As("meera").GetAsync("/api/settlements")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact] public async Task Employee_self_service_roster_declaration_query_and_allowance()
    {
        var f = new ApiFixture(); var ravi = f.As("ravi");
        var roster = await ravi.GetJ("/api/me/roster?month=2026-09"); ((int)roster["shifts"]!).Should().BeGreaterThan(15);
        var my = await ravi.GetJ("/api/me/allowance?month=2026-09"); ((string)my["summary"]!).Should().Contain("₹"); my["payload"]!["lines"]![0]!["trace"]!.ToString().Should().Contain("allowance-eligible");
        var decl = (await ravi.GetJ("/api/me/declarations")).AsArray().First(d => (string)d!["status"]! == "Due");
        (await ravi.Post($"/api/me/declarations/{(string)decl!["id"]!}/sign")).StatusCode.Should().Be(HttpStatusCode.OK); (await ravi.Post($"/api/me/declarations/{(string)decl["id"]!}/sign")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ravi.Post("/api/me/queries", new { month = "2026-09", message = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ravi.Post("/api/me/queries", new { month = "2026-09", message = "Why is 12 Sep ₹450?" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.As("priya").GetAsync("/api/me/roster?month=2026-09")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Fact] public async Task Foundation_data_is_hrss_only_and_effective_dated()
    {
        var f = new ApiFixture(); var night = await f.Scope(db => db.ShiftTypes.Where(s => s.Code == "NIGHT_A").Select(s => s.Id).FirstAsync()); var ret = await f.Scope(db => db.BusinessUnits.Where(b => b.Code == "RET").Select(b => b.Id).FirstAsync());
        (await f.As("priya").Post("/api/foundation/rates", new { shiftTypeId = night, businessUnitId = ret, amount = 600, effectiveFrom = "2026-11-01" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await f.As("fathima").Post("/api/foundation/rates", new { shiftTypeId = night, businessUnitId = ret, amount = 600, effectiveFrom = "2026-01-01" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.As("fathima").Post("/api/foundation/rates", new { shiftTypeId = night, businessUnitId = ret, amount = 600, effectiveFrom = "2026-11-01" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var rates = (await f.As("fathima").GetJ("/api/foundation/rates")).AsArray().Where(r => (string)r!["code"]! == "NIGHT_A" && (string)r["businessUnit"]! == "Retail Ops").ToList();
        rates.Should().HaveCount(3); rates.Select(r => (string?)r!["effectiveTo"]).Should().Contain("2026-10-31");
        (await f.As("fathima").Post("/api/foundation/shift-rules", new { code = "BR-02", parameters = "{\"max\":-1}", severity = "Block", effectiveFrom = "2026-12-01" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.As("fathima").Post("/api/foundation/shift-rules", new { code = "BR-02", parameters = "{\"max\":5}", severity = "Block", effectiveFrom = "2026-12-01" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.As("fathima").Post("/api/foundation/shift-types", new { code = "TEST", name = "Test shift", start = "23:00", end = "07:00", isAllowanceEligible = true, isActive = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.As("fathima").Post("/api/foundation/shift-types", new { code = "TEST", name = "Dup", start = "23:00", end = "07:00", isAllowanceEligible = true, isActive = true })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
    [Fact] public async Task Reports_page_sort_and_export_in_three_formats()
    {
        var f = new ApiFixture(); var h = f.As("fathima");
        var p = await h.GetJ("/api/reports/tagging-register?page=2&pageSize=10&sort=employee&dir=desc"); ((int)p["page"]!).Should().Be(2); p["rows"]!.AsArray().Should().HaveCount(10); ((int)p["total"]!).Should().BeGreaterThan(50);
        var csv = await h.GetAsync("/api/reports/allowance-summary?format=csv"); (await csv.Content.ReadAsStringAsync()).Should().StartWith("Project,Month");
        var x = await h.GetAsync("/api/reports/allowance-summary?format=xlsx"); (await x.Content.ReadAsByteArrayAsync()).Take(2).Should().Equal((byte)'P', (byte)'K');
        var scoped = await f.As("priya").GetJ("/api/reports/tagging-register?pageSize=200"); ((int)scoped["total"]!).Should().BeLessThan((int)p["total"]!); // PM sees only their portfolio
    }
    [Fact] public async Task Notifications_link_to_records_and_can_be_marked_read()
    {
        var f = new ApiFixture(); var n = (await f.As("ravi").GetJ("/api/notifications")).AsArray(); n.Should().HaveCountGreaterThan(2); n.Should().OnlyContain(x => ((string)x!["linkRoute"]!).StartsWith("/"));
        var unread = (await f.As("ravi").GetJ("/api/me"))["unreadNotifications"]!.GetValue<int>(); unread.Should().BeGreaterThan(0);
        (await f.As("ravi").Post("/api/notifications/read-all")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        ((int)(await f.As("ravi").GetJ("/api/me"))["unreadNotifications"]!).Should().Be(0);
    }
}
