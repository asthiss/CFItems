using System.Text;
using System.Text.Json;

namespace CFItems.Bot.Services;

/// <summary>
/// Reads all scraped data, chunks it, generates embeddings via Ollama, saves to JSON.
/// </summary>
public class Indexer
{
    private readonly OllamaClient _ollama;
    private readonly string _embeddingModel;
    private readonly string _dataDir;

    public Indexer(OllamaClient ollama, string embeddingModel, string dataDir)
    {
        _ollama = ollama;
        _embeddingModel = embeddingModel;
        _dataDir = dataDir;
    }

    public async Task BuildAsync(string outputPath)
    {
        Console.WriteLine($"Building index using embedding model: {_embeddingModel}");
        var chunks = new List<IndexChunk>();

        chunks.AddRange(LoadWikiChunks());
        chunks.AddRange(LoadItemChunks());
        chunks.AddRange(LoadAreaChunks());
        chunks.AddRange(LoadHelpfileChunks());
        chunks.AddRange(LoadQhcfSiteChunks());
        chunks.AddRange(LoadForumChunks());

        Console.WriteLine($"\nTotal text chunks to embed: {chunks.Count}");
        Console.WriteLine("Embedding (this can take a while)...\n");

        var dimensions = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < chunks.Count; i++)
        {
            try
            {
                var v = await _ollama.EmbedAsync(_embeddingModel, chunks[i].Text);
                chunks[i].Embedding = v;
                if (dimensions == 0) dimensions = v.Length;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Embed failed at chunk {i}: {ex.Message}");
            }

            if ((i + 1) % 100 == 0 || i == chunks.Count - 1)
            {
                var pct = (i + 1) * 100.0 / chunks.Count;
                var rate = (i + 1) / Math.Max(1.0, sw.Elapsed.TotalSeconds);
                var etaMin = (chunks.Count - i - 1) / rate / 60;
                Console.WriteLine($"  {i + 1}/{chunks.Count} ({pct:F1}%) | {rate:F1} chunks/s | ETA {etaMin:F1} min");
            }
        }

        var index = new IndexFile
        {
            EmbeddingModel = _embeddingModel,
            Dimensions = dimensions,
            BuiltAt = DateTime.UtcNow,
            Chunks = chunks.Where(c => c.Embedding.Length > 0).ToList()
        };

        var opts = new JsonSerializerOptions { WriteIndented = false };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(index, opts));
        Console.WriteLine($"\nWrote {index.Chunks.Count} chunks ({dimensions}D) to {outputPath}");
        Console.WriteLine($"File size: {new FileInfo(outputPath).Length / 1024 / 1024} MB");
    }

    // -------- Loaders for each source --------

    private IEnumerable<IndexChunk> LoadWikiChunks()
    {
        var path = Path.Combine(_dataDir, "knowledge", "qhcf-wiki-all.json");
        if (!File.Exists(path)) yield break;

        var doc = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var title = prop.Name;
            var text = "";
            if (prop.Value.TryGetProperty("RawText", out var t) && t.ValueKind == JsonValueKind.String)
                text = t.GetString() ?? "";
            if (text.Length < 30) continue;

            // Split long pages into ~600-char chunks at paragraph boundaries
            foreach (var (chunkText, idx) in SplitParagraphs(text, 600).Select((c, i) => (c, i)))
            {
                yield return new IndexChunk
                {
                    Id = $"wiki:{title}:{idx}",
                    Text = $"[Wiki: {title}]\n{chunkText}",
                    Source = "wiki",
                    Title = title,
                    Url = $"http://wiki.qhcf.net/index.php?title={Uri.EscapeDataString(title.Replace(" ", "_"))}"
                };
                count++;
            }
        }
        Console.WriteLine($"  Wiki chunks: {count}");
    }

    private IEnumerable<IndexChunk> LoadItemChunks()
    {
        var path = Path.Combine(_dataDir, "items-export.json");
        if (!File.Exists(path)) yield break;

        var doc = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var name = GetStr(item, "Name");
            if (string.IsNullOrEmpty(name)) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"[Item: {name}]");
            AppendIfPresent(sb, item, "Level", "Level");
            AppendIfPresent(sb, item, "Group", "Type");
            AppendIfPresent(sb, item, "Type", "Slot");
            AppendIfPresent(sb, item, "Material", "Material");
            AppendIfPresent(sb, item, "Weight", "Weight");
            AppendIfPresent(sb, item, "Damnoun", "Damage type");
            AppendIfPresent(sb, item, "Avg", "Avg damage");
            AppendIfPresent(sb, item, "Area", "Area");
            AppendIfPresent(sb, item, "MobSource", "Dropped by");
            AppendIfPresent(sb, item, "ContainerSource", "Found in container");
            AppendIfPresent(sb, item, "PathFromCrossroads", "Path from Eastern Crossroads");
            AppendIfPresent(sb, item, "FlaggsPiped", "Flags");
            AppendIfPresent(sb, item, "ModifiersPiped", "Modifiers");
            AppendIfPresent(sb, item, "ArmorLine", "Armor");

            yield return new IndexChunk
            {
                Id = $"item:{name}",
                Text = sb.ToString().Trim(),
                Source = "item",
                Title = name
            };
            count++;
        }
        Console.WriteLine($"  Item chunks: {count}");
    }

    private IEnumerable<IndexChunk> LoadAreaChunks()
    {
        var path = Path.Combine(_dataDir, "knowledge", "areas.json");
        if (!File.Exists(path)) yield break;

        var doc = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        foreach (var area in doc.RootElement.EnumerateArray())
        {
            var name = GetStr(area, "Name");
            if (string.IsNullOrEmpty(name)) continue;
            var sb = new StringBuilder();
            sb.AppendLine($"[Area: {name}]");
            AppendIfPresent(sb, area, "LevelRange", "Level range");
            AppendIfPresent(sb, area, "Builder", "Builder");
            AppendIfPresent(sb, area, "Category", "Category");
            yield return new IndexChunk
            {
                Id = $"area:{name}",
                Text = sb.ToString().Trim(),
                Source = "area",
                Title = name
            };
            count++;
        }
        Console.WriteLine($"  Area chunks: {count}");
    }

    private IEnumerable<IndexChunk> LoadHelpfileChunks()
    {
        var path = Path.Combine(_dataDir, "knowledge", "game-info.json");
        if (!File.Exists(path)) yield break;

        var doc = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        foreach (var category in doc.RootElement.EnumerateObject())
        {
            if (category.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in category.Value.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                var content = entry.Value.GetString() ?? "";
                if (content.Length < 30) continue;
                var title = $"{category.Name}: {entry.Name}";
                foreach (var (chunkText, idx) in SplitParagraphs(content, 600).Select((c, i) => (c, i)))
                {
                    yield return new IndexChunk
                    {
                        Id = $"help:{title}:{idx}",
                        Text = $"[Helpfile {title}]\n{chunkText}",
                        Source = "helpfile",
                        Title = title
                    };
                    count++;
                }
            }
        }
        Console.WriteLine($"  Helpfile chunks: {count}");
    }

    private IEnumerable<IndexChunk> LoadQhcfSiteChunks()
    {
        var path = Path.Combine(_dataDir, "knowledge", "qhcf-site.json");
        if (!File.Exists(path)) yield break;

        var doc = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var title = prop.Value.TryGetProperty("Title", out var t) ? t.GetString() ?? prop.Name : prop.Name;
            var text = prop.Value.TryGetProperty("Text", out var x) ? x.GetString() ?? "" : "";
            var url = prop.Value.TryGetProperty("Url", out var u) ? u.GetString() : null;
            if (text.Length < 50) continue;

            foreach (var (chunkText, idx) in SplitParagraphs(text, 600).Select((c, i) => (c, i)))
            {
                yield return new IndexChunk
                {
                    Id = $"qhcf-site:{title}:{idx}",
                    Text = $"[qhcf.net: {title}]\n{chunkText}",
                    Source = "qhcf-site",
                    Title = title,
                    Url = url
                };
                count++;
            }
        }
        Console.WriteLine($"  qhcf-site chunks: {count}");
    }

    private IEnumerable<IndexChunk> LoadForumChunks()
    {
        var forumsDir = Path.Combine(_dataDir, "forums");
        if (!Directory.Exists(forumsDir)) yield break;

        var totalCount = 0;
        foreach (var file in Directory.GetFiles(forumsDir, "*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch { continue; }

            var fileCount = 0;
            foreach (var thread in doc.RootElement.EnumerateObject())
            {
                var threadId = thread.Name;
                if (thread.Value.ValueKind != JsonValueKind.Object) continue;

                var title = thread.Value.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                var url = thread.Value.TryGetProperty("Url", out var u) ? u.GetString() : null;
                var posts = thread.Value.TryGetProperty("Posts", out var p) ? p : default;
                if (posts.ValueKind != JsonValueKind.Array) continue;

                // Each post is a chunk, with thread title as context
                var postIdx = 0;
                foreach (var post in posts.EnumerateArray())
                {
                    var body = post.TryGetProperty("Body", out var b) ? b.GetString() ?? "" : "";
                    var author = post.TryGetProperty("Author", out var a) ? a.GetString() : null;
                    if (body.Length < 30) { postIdx++; continue; }

                    // Cap very long forum posts (game logs can be huge)
                    if (body.Length > 2500) body = body.Substring(0, 2500) + "...[truncated]";

                    yield return new IndexChunk
                    {
                        Id = $"{fileName}:{threadId}:{postIdx}",
                        Text = $"[{fileName} thread: {title}]\nPost by {author ?? "?"}: {body}",
                        Source = fileName,
                        Title = title,
                        Url = url
                    };
                    fileCount++; totalCount++;
                    postIdx++;
                }
            }
            doc.Dispose();
            if (fileCount > 0) Console.WriteLine($"  {fileName}: {fileCount} post chunks");
        }
        Console.WriteLine($"  Forum chunks total: {totalCount}");
    }

    // -------- Helpers --------

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void AppendIfPresent(StringBuilder sb, JsonElement el, string prop, string label)
    {
        if (!el.TryGetProperty(prop, out var v)) return;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
        if (string.IsNullOrEmpty(s)) return;
        sb.AppendLine($"{label}: {s.Replace("|", ", ")}");
    }

    /// <summary>Split text into chunks of ~maxLen chars, preferring paragraph breaks.</summary>
    private static IEnumerable<string> SplitParagraphs(string text, int maxLen)
    {
        text = text.Trim();
        if (text.Length <= maxLen) { yield return text; yield break; }

        var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();
        foreach (var para in paragraphs)
        {
            if (current.Length + para.Length + 2 > maxLen && current.Length > 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            // If a single paragraph is too long, hard split it
            if (para.Length > maxLen)
            {
                if (current.Length > 0) { yield return current.ToString().Trim(); current.Clear(); }
                for (var i = 0; i < para.Length; i += maxLen)
                    yield return para.Substring(i, Math.Min(maxLen, para.Length - i));
            }
            else
            {
                current.AppendLine(para);
                current.AppendLine();
            }
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }
}
