using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace BackupAgent.Services;

public class OllamaClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;

    public OllamaClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    private string BaseUrl => _config.GetValue<string>("Rag:OllamaUrl")?.TrimEnd('/') ?? "http://localhost:11434";
    private string Model => _config.GetValue<string>("Rag:Model") ?? "llama2";

    public async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var url = $"{BaseUrl}/embed";
            var body = new { model = Model, input = text };
            var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("embedding", out var emb))
            {
                var list = emb.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
                return list;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        try
        {
            var url = $"{BaseUrl}/generate";
            var body = new
            {
                model = Model,
                prompt = prompt,
                max_tokens = 512
            };

            var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("output", out var outp)) return outp.GetString();
            }
            catch { }
            return text;
        }
        catch
        {
            return null;
        }
    }
}
