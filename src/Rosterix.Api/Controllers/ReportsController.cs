using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rosterix.Application.Ai;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Api.Controllers;

public sealed record Col(string Key, string Label, string Type = "text");
public sealed class ReportData { public List<Col> Columns { get; init; } = new(); public List<Dictionary<string, object?>> Rows { get; init; } = new(); }

[Route("api/reports")]
public class ReportsController : RxController
{
    public static readonly string[] Names = { "payroll", "allowance-summary", "roster-compliance", "exception-summary", "tagging-register", "audit-trail" };

    [HttpGet]
    public IActionResult Catalogue() => Ok(Names.Where(n => n != "audit-trail" || Me.Has(Role.HRSS)).Select(n => new { name = n, title = Title(n) }));
    static string Title(string n) => n switch { "payroll" => "Payroll report", "allowance-summary" => "Allowance summary", "roster-compliance" => "Roster compliance", "exception-summary" => "Exception summary", "tagging-register" => "Tagging register", _ => "Audit trail" };

    [HttpGet("{name}")]
    public async Task<IActionResult> Run(string name, [FromQuery] string? month, [FromQuery] Guid? projectId, [FromQuery] string? q, [FromQuery] string? sort, [FromQuery] string dir = "asc",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string format = "json", [FromQuery] string? columns = null, CancellationToken ct = default)
    {
        if (!Names.Contains(name)) return NotFound();
        if (name == "audit-trail" && !Me.Has(Role.HRSS)) return Fail(403, "The audit trail is available to HRSS only.");
        var ds = await LoadAsync(ct); var vis = VisibleProjects(ds);
        var data = name switch
        {
            "payroll" => Payroll(ds, vis, month, projectId), "allowance-summary" => AllowanceSummary(ds, vis, month, projectId), "roster-compliance" => RosterCompliance(ds, vis, month, projectId),
            "exception-summary" => ExceptionSummary(ds, vis, projectId), "tagging-register" => Tagging(ds, vis, projectId), _ => await Audit(ct)
        };
        var rows = data.Rows.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(q)) rows = rows.Where(r => r.Values.Any(v => v?.ToString()?.Contains(q, StringComparison.OrdinalIgnoreCase) == true));
        if (!string.IsNullOrWhiteSpace(sort) && data.Columns.Any(c => c.Key == sort)) rows = dir == "desc" ? rows.OrderByDescending(r => r[sort] is IComparable c ? c : r[sort]?.ToString()) : rows.OrderBy(r => r[sort] is IComparable c ? c : r[sort]?.ToString());
        var list = rows.ToList();
        var cols = string.IsNullOrWhiteSpace(columns) ? data.Columns : data.Columns.Where(c => columns.Split(',').Contains(c.Key)).ToList();
        if (format == "csv") return File(Encoding.UTF8.GetBytes(Csv(cols, list)), "text/csv", $"{name}.csv");
        if (format == "xlsx") return File(Xlsx(Title(name), cols, list), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}.xlsx");
        pageSize = Math.Clamp(pageSize, 1, 200);
        return Ok(new { name, title = Title(name), columns = data.Columns, total = list.Count, page, pageSize, rows = list.Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize) });
    }

    static Dictionary<string, object?> R(params (string k, object? v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);

    ReportData Payroll(Dataset ds, HashSet<Guid> vis, string? month, Guid? pid)
    {
        var runs = ds.Runs.Where(r => vis.Contains(r.ProjectId) && r.Status is RunStatus.CompensationApproved or RunStatus.Submitted && (month == null || r.PeriodMonth == month) && (pid == null || r.ProjectId == pid)).ToList();
        var rows = runs.SelectMany(r => ds.Lines.Where(l => l.AllowanceRunId == r.Id).GroupBy(l => l.EmployeeId).Select(g => R(("employeeNumber", ds.Emp(g.Key).EmployeeNumber), ("employee", ds.Emp(g.Key).FullName), ("project", ds.Proj(r.ProjectId).Code), ("month", r.PeriodMonth), ("shifts", g.Count()), ("amount", g.Sum(l => l.Amount)), ("status", r.Status.ToString())))).ToList();
        return new ReportData { Columns = new() { new("employeeNumber", "Employee no."), new("employee", "Employee"), new("project", "Project"), new("month", "Month"), new("shifts", "Shifts", "number"), new("amount", "Amount (₹)", "money"), new("status", "Run status") }, Rows = rows };
    }
    ReportData AllowanceSummary(Dataset ds, HashSet<Guid> vis, string? month, Guid? pid) => new()
    {
        Columns = new() { new("project", "Project"), new("month", "Month"), new("status", "Status"), new("lines", "Lines", "number"), new("total", "Total (₹)", "money"), new("exceptions", "Exceptions", "number"), new("calculatedBy", "Calculated by") },
        Rows = ds.Runs.Where(r => vis.Contains(r.ProjectId) && (month == null || r.PeriodMonth == month) && (pid == null || r.ProjectId == pid)).Select(r => R(("project", ds.Proj(r.ProjectId).Name), ("month", r.PeriodMonth), ("status", r.Status.ToString()), ("lines", r.LineCount), ("total", r.TotalAmount), ("exceptions", r.ExceptionCount), ("calculatedBy", ds.Emp(r.CalculatedBy).FullName))).ToList()
    };
    ReportData RosterCompliance(Dataset ds, HashSet<Guid> vis, string? month, Guid? pid)
    {
        var ai = S<ConstraintValidationAi>(); var rows = new List<Dictionary<string, object?>>();
        foreach (var r in ds.Rosters.Where(r => vis.Contains(r.ProjectId) && (month == null || r.PeriodMonth == month) && (pid == null || r.ProjectId == pid)).OrderByDescending(r => r.PeriodMonth))
        {
            var v = ai.Evaluate(ds, new ValidationRequest(r.ProjectId, r.PeriodMonth, new())).Payload; var staff = ds.EntriesFor(r.Id).Select(e => e.EmployeeId).Distinct().Count();
            var blocks = v.Violations.Count(x => x.Severity == RuleSeverity.Block); var warns = v.Violations.Count - blocks;
            rows.Add(R(("project", ds.Proj(r.ProjectId).Name), ("month", r.PeriodMonth), ("status", r.Status.ToString()), ("employees", staff), ("blocking", blocks), ("warnings", warns), ("compliance", blocks == 0 ? (warns == 0 ? "Compliant" : "Compliant with warnings") : "Non-compliant")));
        }
        return new ReportData { Columns = new() { new("project", "Project"), new("month", "Month"), new("status", "Status"), new("employees", "Employees", "number"), new("blocking", "Blocking", "number"), new("warnings", "Warnings", "number"), new("compliance", "Result") }, Rows = rows };
    }
    ReportData ExceptionSummary(Dataset ds, HashSet<Guid> vis, Guid? pid) => new()
    {
        Columns = new() { new("project", "Project"), new("category", "Category"), new("severity", "Severity"), new("open", "Open", "number"), new("acknowledged", "Acknowledged", "number"), new("resolved", "Resolved", "number") },
        Rows = ds.Exceptions.Where(e => e.ProjectId != null && vis.Contains(e.ProjectId.Value) && (pid == null || e.ProjectId == pid)).GroupBy(e => (e.ProjectId, e.Category, e.Severity))
            .Select(g => R(("project", ds.Proj(g.Key.ProjectId!.Value).Name), ("category", g.Key.Category.ToString()), ("severity", g.Key.Severity.ToString()), ("open", g.Count(x => x.Status == ExceptionStatus.Open)), ("acknowledged", g.Count(x => x.Status == ExceptionStatus.Acknowledged)), ("resolved", g.Count(x => x.Status == ExceptionStatus.Resolved)))).ToList()
    };
    ReportData Tagging(Dataset ds, HashSet<Guid> vis, Guid? pid) => new()
    {
        Columns = new() { new("employeeNumber", "Employee no."), new("employee", "Employee"), new("project", "Project"), new("from", "From", "date"), new("to", "To", "date"), new("type", "Type"), new("status", "Status") },
        Rows = ds.EmployeeTags.Where(t => vis.Contains(t.ProjectId) && (pid == null || t.ProjectId == pid)).Select(t => R(("employeeNumber", ds.Emp(t.EmployeeId).EmployeeNumber), ("employee", ds.Emp(t.EmployeeId).FullName), ("project", ds.Proj(t.ProjectId).Code), ("from", t.ValidFrom.ToString("yyyy-MM-dd")), ("to", t.ValidTo?.ToString("yyyy-MM-dd")), ("type", t.TagType.ToString()), ("status", t.Status.ToString()))).ToList()
    };
    async Task<ReportData> Audit(CancellationToken ct)
    {
        var names = await Db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.FirstName + " " + e.LastName, ct);
        var rows = (await Db.AuditLogs.AsNoTracking().OrderByDescending(a => a.OccurredAtUtc).Take(2000).ToListAsync(ct)).Select(a => R(("when", a.OccurredAtUtc.ToString("yyyy-MM-dd HH:mm:ss")), ("actor", a.ActorEmployeeId != null && names.TryGetValue(a.ActorEmployeeId.Value, out var n) ? n : "—"), ("event", a.EventType), ("result", a.Succeeded ? "Succeeded" : "Failed"), ("endpoint", a.Endpoint), ("ip", a.OriginIp))).ToList();
        return new ReportData { Columns = new() { new("when", "When (UTC)"), new("actor", "Actor"), new("event", "Event"), new("result", "Result"), new("endpoint", "Endpoint"), new("ip", "Origin IP") }, Rows = rows };
    }

    static string Csv(List<Col> cols, List<Dictionary<string, object?>> rows)
    {
        static string E(object? v) { var s = v?.ToString() ?? ""; if (s.Length > 0 && "=+-@".Contains(s[0]) && !decimal.TryParse(s, out _)) s = "'" + s; return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
        var sb = new StringBuilder(string.Join(',', cols.Select(c => E(c.Label))) + "\n");
        foreach (var r in rows) sb.Append(string.Join(',', cols.Select(c => E(r.GetValueOrDefault(c.Key)))) + "\n");
        return sb.ToString();
    }

    /// <summary>Minimal OOXML writer: streamed from memory, never persisted (SEC-10).</summary>
    static byte[] Xlsx(string title, List<Col> cols, List<Dictionary<string, object?>> rows)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        static string ColRef(int i) => ((char)('A' + i)).ToString();
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        sheet.Append("<row r=\"1\">" + string.Concat(cols.Select((c, i) => $"<c r=\"{ColRef(i)}1\" t=\"inlineStr\"><is><t>{X(c.Label)}</t></is></c>")) + "</row>");
        for (var r = 0; r < rows.Count; r++)
        {
            sheet.Append($"<row r=\"{r + 2}\">");
            for (var i = 0; i < cols.Count; i++)
            {
                var v = rows[r].GetValueOrDefault(cols[i].Key);
                if (v is int or long or decimal or double) sheet.Append($"<c r=\"{ColRef(i)}{r + 2}\"><v>{Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                else sheet.Append($"<c r=\"{ColRef(i)}{r + 2}\" t=\"inlineStr\"><is><t>{X(v?.ToString() ?? "")}</t></is></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string path, string content) { var e = zip.CreateEntry(path); using var w = new StreamWriter(e.Open(), new UTF8Encoding(false)); w.Write(content); }
            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{X(title.Length > 28 ? title[..28] : title)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Add("xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return ms.ToArray();
    }
}

[Route("api/payroll")]
public class PayrollController : RxController
{
    public sealed record Check(string Id, string Label, string Status, string Detail, string? Link);

    private List<Check> Checklist(Dataset ds, string month, List<Guid>? runIds)
    {
        var all = ds.Runs.Where(r => r.PeriodMonth == month).ToList();
        var sel = runIds == null ? all.Where(r => r.Status == RunStatus.CompensationApproved).ToList() : all.Where(r => runIds.Contains(r.Id)).ToList();
        var pending = all.Where(r => r.Status is RunStatus.Calculated or RunStatus.PendingCompensation).ToList();
        var projects = sel.Select(r => r.ProjectId).ToHashSet();
        var anomalies = S<AnomalyDetectionAi>().Scan(ds, new AnomalyRequest()).Payload.Items.Where(a => a.ProjectId != null && projects.Contains(a.ProjectId.Value) && a.Status == "Open" && a.Kind is "Duplicate claim" or "Rate mismatch").ToList();
        var highExc = ds.Exceptions.Count(e => e.ProjectId != null && projects.Contains(e.ProjectId.Value) && e.Status == ExceptionStatus.Open && e.Severity == ExceptionSeverity.High);
        var checks = new List<Check>
        {
            new("runs", "At least one approved allowance run for the month", sel.Count > 0 ? "Pass" : "Fail", sel.Count > 0 ? $"{sel.Count} run(s), ₹{sel.Sum(r => r.TotalAmount):N0}" : "No run has compensation approval yet.", "/allowance"),
            new("notSubmitted", "No run is already in a payroll batch", sel.All(r => r.Status != RunStatus.Submitted) ? "Pass" : "Fail", sel.Any(r => r.Status == RunStatus.Submitted) ? "A selected run was already submitted." : "All selected runs are unsubmitted.", null),
            new("pending", "No other run for this month is waiting for approval", pending.Count == 0 ? "Pass" : "Warn", pending.Count == 0 ? "Nothing is waiting." : $"{pending.Count} run(s) still waiting: {string.Join(", ", pending.Select(r => ds.Proj(r.ProjectId).Code))}. They will miss this batch.", "/allowance"),
            new("anomalies", "Duplicate claims and rate mismatches are resolved or dismissed", anomalies.Count == 0 ? "Pass" : "Fail", anomalies.Count == 0 ? "None open." : $"{anomalies.Count} open: {string.Join("; ", anomalies.Select(a => $"{a.Kind} for {a.EmployeeName}"))}.", "/anomalies"),
            new("exceptions", "High-severity exceptions are acknowledged", highExc == 0 ? "Pass" : "Warn", highExc == 0 ? "None open." : $"{highExc} high-severity exception(s) are still open.", "/exceptions"),
        };
        return checks;
    }

    [HttpGet("checklist"), Authorize(Policy = Policies.CanSubmitPayroll)]
    public async Task<IActionResult> GetChecklist([FromQuery] string month, CancellationToken ct)
    {
        if (!ValidMonth(month)) return Fail(400, "Use a month like 2026-09.");
        var ds = await LoadAsync(ct); var c = Checklist(ds, month, null);
        var runs = ds.Runs.Where(r => r.PeriodMonth == month).Select(r => new { r.Id, project = ds.Proj(r.ProjectId).Name, status = r.Status.ToString(), r.TotalAmount, r.LineCount, selectable = r.Status == RunStatus.CompensationApproved });
        return Ok(new { month, checks = c, runs, canSubmit = c.All(x => x.Status != "Fail") });
    }

    [HttpGet("batches")]
    public async Task<IActionResult> Batches(CancellationToken ct)
    {
        var ds = await LoadAsync(ct);
        if (!Me.Has(Role.HRSS | Role.HRBP | Role.RH)) return Forbid();
        return Ok(ds.Batches.OrderByDescending(b => b.SubmittedAtUtc).Select(b => new { b.Id, month = b.PeriodMonth, b.TotalAmount, b.LineCount, b.FileRef, status = b.Status.ToString(), submittedBy = ds.Emp(b.SubmittedBy).FullName, b.SubmittedAtUtc, runs = b.RunIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => ds.Runs.FirstOrDefault(r => r.Id == Guid.Parse(x))).Where(r => r != null).Select(r => new { r!.Id, project = ds.Proj(r.ProjectId).Name, r.TotalAmount }) }));
    }

    public sealed record SubmitDto(string Month, List<Guid> RunIds, bool AcknowledgeWarnings);

    [HttpPost("batches"), Authorize(Policy = Policies.CanSubmitPayroll)]
    public async Task<IActionResult> Submit(SubmitDto d, CancellationToken ct)
    {
        if (!ValidMonth(d.Month)) return Fail(400, "Use a month like 2026-09.");
        if (d.RunIds == null || d.RunIds.Count == 0) return Fail(400, "Select at least one approved run.");
        var ds = await LoadAsync(ct); var checks = Checklist(ds, d.Month, d.RunIds);
        if (checks.Any(c => c.Status == "Fail")) return Fail(400, "The pre-submission checklist has failures. Resolve them first.", checks.Where(c => c.Status == "Fail"));
        if (checks.Any(c => c.Status == "Warn") && !d.AcknowledgeWarnings) return Fail(409, "Acknowledge the checklist warnings to submit.", checks.Where(c => c.Status == "Warn"));
        var runs = await Db.Runs.Where(r => d.RunIds.Contains(r.Id) && r.PeriodMonth == d.Month).ToListAsync(ct);
        if (runs.Count != d.RunIds.Distinct().Count() || runs.Any(r => r.Status != RunStatus.CompensationApproved)) return Fail(400, "Every selected run must have compensation approval and not be in a batch already.");
        var seq = await Db.Batches.CountAsync(ct) + 1;
        var batch = new PayrollBatch { PeriodMonth = d.Month, SubmittedBy = Me.Id, SubmittedAtUtc = Clock.UtcNow, RunIdsCsv = string.Join(',', runs.Select(r => r.Id)), TotalAmount = runs.Sum(r => r.TotalAmount), LineCount = runs.Sum(r => r.LineCount), FileRef = $"cnb/{d.Month}/batch-{seq:0000}.csv", Status = BatchStatus.Submitted, CreatedBy = Me.Id.ToString() };
        Db.Batches.Add(batch);
        foreach (var r in runs) { r.Status = RunStatus.Submitted; r.PayrollBatchId = batch.Id; Notifier.Notify(r.CalculatedBy, "PayrollSubmitted", $"Payroll submitted for {d.Month}", $"₹{r.TotalAmount:N0} went to CNB in batch {batch.FileRef}.", NotificationSeverity.Success, "/payroll"); }
        await Db.SaveChangesAsync(ct);
        return Ok(new { batch.Id, batch.FileRef, batch.TotalAmount, batch.LineCount });
    }
}
