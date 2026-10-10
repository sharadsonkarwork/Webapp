using Rosterix.Application.Data;

namespace Rosterix.Application.Ai;

public enum ConfidenceBand { Low, Medium, High }

/// <summary>One plain-language reason, linked to the record that backs it.</summary>
public sealed record AiReason(string Text, string? EvidenceType = null, string? EvidenceId = null, string? EvidenceLabel = null);

/// <summary>Every AI output has this shape: advisory, explained, scored, reversible. No AI service has a write path.</summary>
public sealed class AiSuggestion<T>
{
    public string UseCaseCode { get; init; } = "";
    public Guid SuggestionId { get; init; } = Guid.NewGuid();
    public double Confidence { get; init; }
    public ConfidenceBand ConfidenceBand => AiScoring.Band(Confidence);
    public string Summary { get; init; } = "";
    public List<AiReason> Reasons { get; init; } = new();
    public string Impact { get; init; } = "";
    public bool Reversible { get; } = true;
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public T Payload { get; init; } = default!;
}

public interface IAiUseCase<in TRequest, TPayload>
{
    string Code { get; }
    Task<AiSuggestion<TPayload>> SuggestAsync(TRequest request, CancellationToken ct = default);
}

/// <summary>The only way an AI service reads data. Implementations load a Dataset snapshot; none can write.</summary>
public interface IAiDataSource { Task<Dataset> LoadAsync(CancellationToken ct = default); }

/// <summary>The seam where a language model could rephrase reasons later without changing any data point.</summary>
public interface IAiProvider
{
    string Name { get; }
    AiReason Narrate(AiReason reason);
}

public sealed class HeuristicAiProvider : IAiProvider
{
    public string Name => "heuristic";
    public AiReason Narrate(AiReason reason) => reason;
}

/// <summary>Feature-flagged stub (off by default). Would call an LLM to rephrase text; today it returns the reason unchanged.</summary>
public sealed class LlmNarrationProvider : IAiProvider
{
    private readonly bool _enabled;
    public LlmNarrationProvider(bool enabled = false) => _enabled = enabled;
    public string Name => _enabled ? "llm-narration(stub)" : "llm-narration(off)";
    public AiReason Narrate(AiReason reason) => reason; // data points are never altered
}

public static class AiScoring
{
    public static ConfidenceBand Band(double c) => c >= 0.8 ? ConfidenceBand.High : c >= 0.5 ? ConfidenceBand.Medium : ConfidenceBand.Low;
    public static double Clamp(double v) => Math.Round(Math.Max(0.05, Math.Min(0.99, v)), 2);
    public static double Median(IEnumerable<double> xs) { var a = xs.OrderBy(x => x).ToArray(); if (a.Length == 0) return 0; return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
    public static double Mean(IEnumerable<double> xs) { var a = xs.ToArray(); return a.Length == 0 ? 0 : a.Average(); }
    public static double Std(IEnumerable<double> xs) { var a = xs.ToArray(); if (a.Length < 2) return 0; var m = a.Average(); return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / (a.Length - 1)); }
}

public abstract class AiServiceBase
{
    protected readonly IAiDataSource Source; protected readonly IAiProvider Provider;
    protected AiServiceBase(IAiDataSource s, IAiProvider p) { Source = s; Provider = p; }
    protected List<AiReason> Narrate(IEnumerable<AiReason> rs) => rs.Select(Provider.Narrate).ToList();
    protected static string Name(Dataset ds, Guid id) => ds.EmpOrNull(id)?.FullName ?? "Unknown";
}
