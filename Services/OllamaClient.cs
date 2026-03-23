using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackupAgent.Services;

public class OllamaClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<OllamaClient> _logger;

    public OllamaClient(HttpClient http, IConfiguration config, ILogger<OllamaClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    private string BaseUrl => _config.GetValue<string>("Rag:OllamaUrl")?.TrimEnd('/') ?? "http://localhost:11434";
    private string Model => _config.GetValue<string>("Rag:Model") ?? "llama2";

    public async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var url = $"{BaseUrl}/api/embeddings";
            var body = new { model = "mxbai-embed-large", input = text };
            var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Ollama embed failed ({Status}): {Error}", (int)resp.StatusCode, err);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            // /api/embed returns { "embeddings": [[...]] }
            if (doc.RootElement.TryGetProperty("embeddings", out var embeddings))
            {
                var first = embeddings.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Array)
                    return first.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            }
            // fallback: older /api/embeddings style
            if (doc.RootElement.TryGetProperty("embedding", out var emb))
            {
                return emb.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama embed threw an exception");
            return null;
        }
    }

    public async Task<string?> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        try
        {
            var url = $"{BaseUrl}/api/generate";
            var body = new
            {
                model = Model,
                prompt = prompt,
                stream = false,
                options = new { num_predict = 512, repeat_penalty = 1.3, temperature = 0.7 }
            };

            var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Ollama generate failed ({Status}): {Error}", (int)resp.StatusCode, err);
                return null;
            }
            var text = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("Ollama generate raw response: {Response}", text);
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("response", out var outp)) return outp.GetString();
            }
            catch { }
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama generate threw an exception");
            return null;
        }
    }
}
