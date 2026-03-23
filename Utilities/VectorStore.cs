using System.Text.Json;
using BackupAgent.Services;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using BackupAgent.Data;
using RedwoodIloilo.Common.Entities;
using Pgvector;

namespace BackupAgent.Utilities;

public class VectorEntry
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public Dictionary<string, string>? Metadata { get; set; }
}

public class VectorStore
{
    private readonly OllamaClient _ollama;
    private readonly string _indexPath;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<VectorStore> _logger;
    private readonly int _embeddingDim;
    private static readonly char[] separator = new[] { '\r', '\n' };

    public VectorStore(OllamaClient ollama, IConfiguration cfg, IDbContextFactory<AppDbContext> dbFactory, ILogger<VectorStore> logger)
    {
        _ollama = ollama;
        _dbFactory = dbFactory;
        _logger = logger;
        _indexPath = cfg.GetValue<string>("Rag:IndexPath") ?? "./rag_index";
        _embeddingDim = cfg.GetValue<int>("Rag:EmbeddingDimension", 1024);
        Directory.CreateDirectory(_indexPath);
    }

    public async Task UpsertFailureAsync(string error, string? context, Dictionary<string, string>? metadata = null)
    {
        var normalized = NormalizeError(error);
        var signature = ComputeSha256Hex(normalized);

        var combined = (error ?? string.Empty) + "\n" + (context ?? string.Empty);

        // Derive a human-friendly title: first non-empty line of the error, truncated.
        var title = (error ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? signature;
        if (title.Length > 200) title = title[..200];
        var emb = await _ollama.GetEmbeddingAsync(combined);

        await using var db = await _dbFactory.CreateDbContextAsync();

        // look up document by Signature (explicit field) — no reflection
        var existingDoc = await db.RagDocuments.FirstOrDefaultAsync(d => d.Signature == signature);
        RagDocument docObj;
        if (existingDoc != null)
        {
            // update MetadataJson with last seen timestamp (preserve existing JSON as "prev")
            try
            {
                existingDoc.MetadataJson = JsonSerializer.Serialize(new { prev = existingDoc.MetadataJson, lastSeen = DateTime.UtcNow });
                db.Update(existingDoc);
                await db.SaveChangesAsync();
            }
            catch
            {
                // ignore metadata update failures
            }

            docObj = existingDoc;
        }
        else
        {
            var newDoc = new RagDocument
            {
                Id = Guid.NewGuid(),
                Title = title,
                Signature = signature,
                CreatedAt = DateTime.UtcNow,
                MetadataJson = JsonSerializer.Serialize(new { error, context })
            };

            db.RagDocuments.Add(newDoc);
            await db.SaveChangesAsync();
            docObj = newDoc;
        }

        // now create a RagChunk for this occurrence and associate with the document
        var chunkText = combined;
        var chunk = new RagChunk
        {
            Id = Guid.NewGuid(),
            RagDocumentId = docObj.Id,
            ChunkIndex = 0,
            Text = chunkText,
            CreatedAt = DateTime.UtcNow
        };

        // Only set embedding if we actually received a vector with the expected dimension
        if (emb != null && emb.Length > 0)
        {
            if (emb.Length != _embeddingDim)
            {
                _logger.LogWarning("Embedding dimension mismatch: expected {Expected}, got {Got}. Skipping embedding.", _embeddingDim, emb.Length);
            }
            else
            {
                try
                {
                    chunk.Embedding = new Vector(emb);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set chunk embedding");
                }
            }
        }

        // Pre-insert check: avoid inserting duplicate chunks for the same document+text.
        var existingChunk = await db.RagChunks.FirstOrDefaultAsync(c => c.RagDocumentId == docObj.Id && c.Text == chunkText);
        if (existingChunk != null)
        {
            // If existing chunk lacks an embedding but we have one, update it.
            try
            {
                var existingEmb = ExtractEmbedding(existingChunk);
                if ((existingChunk.Embedding == null || existingEmb == null || existingEmb.Length == 0) && (emb?.Length > 0))
                {
                    if (emb.Length != _embeddingDim)
                    {
                        _logger.LogWarning("Not updating existing chunk embedding: embedding dim {Got} doesn't match expected {Expected}", emb.Length, _embeddingDim);
                    }
                    else
                    {
                        existingChunk.Embedding = chunk.Embedding;
                        db.Update(existingChunk);
                        await db.SaveChangesAsync();
                    }
                }
            }
            catch { }

            return;
        }

        db.RagChunks.Add(chunk);
        await db.SaveChangesAsync();
    }

    private static string NormalizeError(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        // strip numeric sequences, timestamps, GUIDs
        var noTs = System.Text.RegularExpressions.Regex.Replace(s, "\\d{4}-\\d{2}-\\d{2}[ T]\\d{2}:\\d{2}:\\d{2}(.\\d+)?", "");
        var noNums = System.Text.RegularExpressions.Regex.Replace(noTs, "\\d+", "");
        var noGuid = System.Text.RegularExpressions.Regex.Replace(noNums, "[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", "");
        return noGuid.Trim().ToLowerInvariant();
    }

    private static string ComputeSha256Hex(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public async Task<List<(VectorEntry Entry, float Score)>> SearchAsync(string query, int topK = 3)
    {
        var qEmb = await _ollama.GetEmbeddingAsync(query) ?? Array.Empty<float>();
        if (qEmb.Length == 0) return new List<(VectorEntry, float)>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var chunks = await db.RagChunks.AsNoTracking().ToListAsync();

        var results = new List<(VectorEntry, float)>();
        foreach (var c in chunks)
        {
            var emb = ExtractEmbedding(c);
            if (emb == null || emb.Length == 0) continue;
            var score = CosineSimilarity(qEmb, emb);
            var entry = new VectorEntry { Id = c.Id.ToString(), Text = c.Text ?? string.Empty, Embedding = emb };
            results.Add((entry, score));
        }

        return results.OrderByDescending(r => r.Item2).Take(topK).ToList();
    }

    private static float[]? ExtractEmbedding(RagChunk entity)
    {
        if (entity == null) return null;

        var vec = entity.Embedding;
        if (vec == null) return null;

        // Fast path: Memory backer
        try
        {
            return vec.Memory.Span.ToArray();
        }
        catch { }

        // Try common conversion helpers on Pgvector.Vector
        var vt = vec.GetType();
        var values = vt.GetProperty("Values")?.GetValue(vec) as System.Collections.IEnumerable;
        if (values != null)
        {
            var arr = values.Cast<object>().Select(o => Convert.ToSingle(o)).ToArray();
            if (arr.Length > 0) return arr;
        }

        var toArr = vt.GetMethod("ToArray", Type.EmptyTypes)?.Invoke(vec, null) as System.Array;
        if (toArr != null) return toArr.Cast<object>().Select(o => Convert.ToSingle(o)).ToArray();

        return null;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
