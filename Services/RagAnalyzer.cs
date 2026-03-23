using System.Text;
using BackupAgent.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackupAgent.Services;

public class RagAnalyzer
{
    private readonly OllamaClient _ollama;
    private readonly VectorStore _store;
    private readonly ILogger<RagAnalyzer> _logger;
    private readonly IConfiguration _config;

    public RagAnalyzer(OllamaClient ollama, VectorStore store, ILogger<RagAnalyzer> logger, IConfiguration config)
    {
        _ollama = ollama;
        _store = store;
        _logger = logger;
        _config = config;
    }

    public async Task<string> AnalyzeFailureAsync(string error, string context, CancellationToken ct = default)
    {
        try
        {
            // Search for similar past entries
            var hits = await _store.SearchAsync(error, topK: 3);

            var sb = new StringBuilder();
            sb.AppendLine("You are a backup diagnostics assistant.");
            sb.AppendLine("Database backup error:");
            sb.AppendLine(error);
            sb.AppendLine();
            sb.AppendLine("Context:");
            sb.AppendLine(context ?? string.Empty);
            sb.AppendLine();

            if (hits.Count > 0)
            {
                sb.AppendLine("Relevant past documents:");
                foreach (var (entry, score) in hits)
                {
                    sb.AppendLine($"-- Score: {score:F3} --");
                    sb.AppendLine(entry.Text);
                    sb.AppendLine();
                }
            }

            sb.AppendLine("Provide a concise diagnosis, probable causes, and suggested next steps. Keep it short.");

            var prompt = sb.ToString();
            var resp = await _ollama.GenerateAsync(prompt, ct);

            // Index this error for future similarity searches
            try
            {
                var meta = new Dictionary<string, string> { { "type", "error" }, { "source", "backup-agent" } };
                await _store.UpsertFailureAsync(error, context, meta);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index error into vector store");
            }

            // return resp == null ? "No analysis available." : DeduplicateParagraphs(resp);
            return resp ?? "No analysis available.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAG analysis failed");
            return "RAG analysis failed: " + ex.Message;
        }
    }

    private static string DeduplicateParagraphs(string text)
    {
        // Normalize line indentation first
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var normalized = string.Join("\n", lines.Select(l => l.TrimStart()));

        // Split on blank lines, keep only first occurrence of each paragraph (trimmed).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new StringBuilder();
        foreach (var para in normalized.Split(["\n\n"], StringSplitOptions.None))
        {
            var key = para.Trim();
            if (key.Length == 0 || seen.Add(key))
            {
                if (result.Length > 0) result.AppendLine();
                result.AppendLine(para);
            }
        }
        return result.ToString().TrimEnd();
    }
}
