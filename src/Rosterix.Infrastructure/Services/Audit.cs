using System.Text.Json;
using System.Text.RegularExpressions;
using Rosterix.Domain;
using Rosterix.Infrastructure.Persistence;

namespace Rosterix.Infrastructure.Services;

public interface IAuditSink { Task WriteAsync(AuditLog entry, CancellationToken ct = default); }

public sealed class DbAuditSink : IAuditSink
{
    private readonly RosterixDbContext _db;
    public DbAuditSink(RosterixDbContext db) => _db = db;
    public async Task WriteAsync(AuditLog entry, CancellationToken ct = default) { _db.AuditLogs.Add(entry); await _db.SaveChangesAsync(ct); }
}

/// <summary>SEC-06 — masks sensitive values before they reach the audit trail or the logs.</summary>
public static class Masking
{
    private static readonly Regex Sensitive = new("password|secret|token|amount|employeenumber|email|salary|authorization", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static string Mask(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try { using var doc = JsonDocument.Parse(json); return JsonSerializer.Serialize(Walk(doc.RootElement, null)); }
        catch (JsonException) { return JsonSerializer.Serialize(new { raw = "[unparseable body omitted]" }); }
    }
    private static object? Walk(JsonElement e, string? name)
    {
        if (name != null && Sensitive.IsMatch(name)) return "***";
        return e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Walk(p.Value, p.Name)),
            JsonValueKind.Array => e.EnumerateArray().Take(50).Select(x => Walk(x, name)).ToList(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetDecimal(),
            JsonValueKind.True => true, JsonValueKind.False => false, _ => null
        };
    }
}

/// <summary>SEC-04 — secrets never live in code or config. Environment first; a vault provider is a drop-in.</summary>
public interface ISecretProvider { string? Get(string name); }
public sealed class EnvSecretProvider : ISecretProvider { public string? Get(string name) => Environment.GetEnvironmentVariable(name.Replace(':', '_').ToUpperInvariant()); }
public sealed class AkeylessSecretProvider : ISecretProvider
{
    public string? Get(string name) => throw new NotSupportedException("Akeyless integration is configured per environment; see DECISIONS.md D-14.");
}
