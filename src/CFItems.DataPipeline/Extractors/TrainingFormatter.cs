using System.Text;
using System.Text.Json;
using CFItems.DataPipeline.Models;

namespace CFItems.DataPipeline.Extractors;

/// <summary>
/// Combines all scraped data into AI training formats:
///  1. documents.jsonl   - one doc per line for fine-tuning/embedding
///  2. conversations.jsonl - forum threads as role-played conversations
///  3. text/{source}/ - plain text files per page/thread
/// </summary>
public class TrainingFormatter
{
    private readonly string _dataDir;
    private readonly string _outputDir;

    private const string SystemPrompt = "You are a knowledgeable Carrion Fields MUD expert. Carrion Fields is a roleplay-intensive playerkilling MUD that has been running since 1994. You know its areas, races, classes, cabals, mechanics, items, and community lore.";

    public TrainingFormatter(string dataDir)
    {
        _dataDir = dataDir;
        _outputDir = Path.Combine(dataDir, "training");
        Directory.CreateDirectory(_outputDir);
        Directory.CreateDirectory(Path.Combine(_outputDir, "text"));
    }

    public void Build()
    {
        var allDocs = new List<TrainingDocument>();
        var allConversations = new List<object>();

        // === Wiki pages ===
        Console.WriteLine("Processing wiki pages...");
        var wikiPath = Path.Combine(_dataDir, "knowledge", "qhcf-wiki-all.json");
        if (File.Exists(wikiPath))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(wikiPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var title = prop.Name;
                var text = "";
                if (prop.Value.TryGetProperty("RawText", out var t) && t.ValueKind == JsonValueKind.String)
                    text = t.GetString() ?? "";
                if (text.Length < 30) continue;

                var td = new TrainingDocument
                {
                    Text = text,
                    Source = "wiki",
                    Title = title,
                    Url = $"http://wiki.qhcf.net/index.php?title={Uri.EscapeDataString(title.Replace(" ", "_"))}",
                    Metadata = new Dictionary<string, string> { { "type", "wiki_page" } }
                };
                allDocs.Add(td);
                WriteTextFile("wiki", SanitizeName(title), td);
            }
            Console.WriteLine($"  Added {allDocs.Count} wiki documents");
        }

        // === Game info pages (qhcf.net premium + carrionfields help files) ===
        Console.WriteLine("Processing game info pages...");
        var gameInfoCount = 0;
        var qhcfSitePath = Path.Combine(_dataDir, "knowledge", "qhcf-site.json");
        if (File.Exists(qhcfSitePath))
        {
            var pages = JsonSerializer.Deserialize<Dictionary<string, ScrapedPage>>(File.ReadAllText(qhcfSitePath));
            if (pages != null)
            {
                foreach (var (key, page) in pages)
                {
                    if (page.Text.Length < 50) continue;
                    var td = new TrainingDocument
                    {
                        Text = page.Text,
                        Source = "qhcf-site",
                        Title = page.Title,
                        Url = page.Url,
                        Metadata = new Dictionary<string, string> { { "type", "game_info" } }
                    };
                    allDocs.Add(td);
                    gameInfoCount++;
                    WriteTextFile("qhcf-site", SanitizeName(page.Title), td);
                }
            }
        }

        var gameInfoFile = Path.Combine(_dataDir, "knowledge", "game-info.json");
        if (File.Exists(gameInfoFile))
        {
            var j = JsonDocument.Parse(File.ReadAllText(gameInfoFile));
            foreach (var category in j.RootElement.EnumerateObject())
            {
                if (category.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in category.Value.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String) continue;
                    var content = entry.Value.GetString() ?? "";
                    if (content.Length < 30) continue;
                    var title = $"{category.Name}: {entry.Name}";
                    var td = new TrainingDocument
                    {
                        Text = content,
                        Source = "helpfile",
                        Title = title,
                        Url = $"https://carrionfields.net/helpsearch.php?keywords={Uri.EscapeDataString(entry.Name)}",
                        Metadata = new Dictionary<string, string>
                        {
                            { "type", "helpfile" },
                            { "category", category.Name }
                        }
                    };
                    allDocs.Add(td);
                    gameInfoCount++;
                    WriteTextFile("helpfile", SanitizeName(title), td);
                }
            }
        }
        Console.WriteLine($"  Added {gameInfoCount} game info documents");

        // === Items ===
        Console.WriteLine("Processing items...");
        var itemsCount = 0;
        var itemsPath = Path.Combine(_dataDir, "items-export.json");
        if (File.Exists(itemsPath))
        {
            var items = JsonSerializer.Deserialize<List<ItemRecord>>(File.ReadAllText(itemsPath));
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.Name)) continue;
                    var text = FormatItemAsText(item);
                    var td = new TrainingDocument
                    {
                        Text = text,
                        Source = "item",
                        Title = item.Name,
                        Metadata = new Dictionary<string, string>
                        {
                            { "type", "item" },
                            { "group", item.Group ?? "" },
                            { "level", item.Level ?? "" }
                        }
                    };
                    if (!string.IsNullOrEmpty(item.Area)) td.Metadata["area"] = item.Area;
                    allDocs.Add(td);
                    itemsCount++;
                }
            }
        }
        Console.WriteLine($"  Added {itemsCount} item documents");

        // === Forum threads (phorum + dcboard) ===
        Console.WriteLine("Processing forum threads...");
        var threadCount = 0;
        var conversationCount = 0;
        var forumsDir = Path.Combine(_dataDir, "forums");
        if (Directory.Exists(forumsDir))
        {
            foreach (var forumFile in Directory.GetFiles(forumsDir, "*.json"))
            {
                var fileName = Path.GetFileNameWithoutExtension(forumFile);
                try
                {
                    var threads = JsonSerializer.Deserialize<Dictionary<string, ForumThread>>(File.ReadAllText(forumFile));
                    if (threads == null) continue;

                    foreach (var (tid, thread) in threads)
                    {
                        if (thread.Posts.Count == 0) continue;

                        // Build thread as single document
                        var text = FormatThreadAsText(thread);
                        var td = new TrainingDocument
                        {
                            Text = text,
                            Source = fileName,
                            Title = thread.Title,
                            Url = thread.Url,
                            Metadata = new Dictionary<string, string>
                            {
                                { "type", "forum_thread" },
                                { "forum_id", thread.ForumId },
                                { "forum_name", thread.ForumName },
                                { "thread_id", thread.ThreadId },
                                { "post_count", thread.Posts.Count.ToString() }
                            }
                        };
                        if (!string.IsNullOrEmpty(thread.FirstPostDate))
                            td.Metadata["first_post_date"] = thread.FirstPostDate;
                        allDocs.Add(td);
                        threadCount++;

                        WriteTextFile(fileName, tid, td);

                        // Build conversations for threads with replies
                        if (thread.Posts.Count >= 2)
                        {
                            var conv = BuildConversation(thread);
                            if (conv != null)
                            {
                                allConversations.Add(conv);
                                conversationCount++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error processing {fileName}: {ex.Message}");
                }
            }
        }
        Console.WriteLine($"  Added {threadCount} thread documents, {conversationCount} conversations");

        // === Write output files ===
        var docsPath = Path.Combine(_outputDir, "documents.jsonl");
        using (var writer = new StreamWriter(docsPath))
        {
            var opts = new JsonSerializerOptions { WriteIndented = false };
            foreach (var doc in allDocs)
            {
                writer.WriteLine(JsonSerializer.Serialize(doc, opts));
            }
        }
        Console.WriteLine($"\nWrote {allDocs.Count} documents to {docsPath}");

        var convPath = Path.Combine(_outputDir, "conversations.jsonl");
        using (var writer = new StreamWriter(convPath))
        {
            var opts = new JsonSerializerOptions { WriteIndented = false };
            foreach (var conv in allConversations)
            {
                writer.WriteLine(JsonSerializer.Serialize(conv, opts));
            }
        }
        Console.WriteLine($"Wrote {allConversations.Count} conversations to {convPath}");

        // Summary file
        var summaryPath = Path.Combine(_outputDir, "SUMMARY.txt");
        File.WriteAllText(summaryPath,
            $"CFItems Training Data Summary\n" +
            $"=============================\n" +
            $"Documents: {allDocs.Count}\n" +
            $"  Wiki: {allDocs.Count(d => d.Source == "wiki")}\n" +
            $"  Helpfiles: {allDocs.Count(d => d.Source == "helpfile")}\n" +
            $"  Qhcf-site: {allDocs.Count(d => d.Source == "qhcf-site")}\n" +
            $"  Items: {allDocs.Count(d => d.Source == "item")}\n" +
            $"  Forum threads: {threadCount}\n" +
            $"\nConversations: {allConversations.Count}\n" +
            $"\nPlain text files: in text/ subdirectory by source\n" +
            $"\nGenerated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n");

        Console.WriteLine($"Summary saved to {summaryPath}");
    }

    private object? BuildConversation(ForumThread thread)
    {
        // Build messages: system + user (first post) + assistant (reply) + user + assistant...
        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt }
        };

        var role = "user";
        foreach (var post in thread.Posts)
        {
            if (string.IsNullOrWhiteSpace(post.Body)) continue;
            var content = post.Body.Trim();
            if (!string.IsNullOrEmpty(post.Author)) content = $"[{post.Author}] {content}";
            messages.Add(new { role, content });
            role = role == "user" ? "assistant" : "user";
        }

        // Need at least user+assistant+user+... = 2 non-system messages
        if (messages.Count < 3) return null;

        return new { messages };
    }

    private string FormatItemAsText(ItemRecord item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Item: {item.Name}");
        if (!string.IsNullOrEmpty(item.Level)) sb.AppendLine($"Level: {item.Level}");
        if (!string.IsNullOrEmpty(item.Group)) sb.AppendLine($"Type: {item.Group}");
        if (!string.IsNullOrEmpty(item.Type)) sb.AppendLine($"Slot: {item.Type}");
        if (!string.IsNullOrEmpty(item.Material)) sb.AppendLine($"Material: {item.Material}");
        if (!string.IsNullOrEmpty(item.Weight)) sb.AppendLine($"Weight: {item.Weight}");
        if (item.Worth > 0) sb.AppendLine($"Worth: {item.Worth} copper");
        if (!string.IsNullOrEmpty(item.Damnoun)) sb.AppendLine($"Damage type: {item.Damnoun}");
        if (!string.IsNullOrEmpty(item.Avg)) sb.AppendLine($"Avg damage: {item.Avg}");
        if (!string.IsNullOrEmpty(item.ArmorLine)) sb.AppendLine(item.ArmorLine);
        if (!string.IsNullOrEmpty(item.FlaggsPiped)) sb.AppendLine($"Flags: {item.FlaggsPiped.Replace("|", ", ")}");
        if (!string.IsNullOrEmpty(item.ModifiersPiped)) sb.AppendLine($"Modifiers: {item.ModifiersPiped.Replace("|", "; ")}");
        if (!string.IsNullOrEmpty(item.Area)) sb.AppendLine($"Area: {item.Area}");
        if (!string.IsNullOrEmpty(item.MobSource)) sb.AppendLine($"Mob: {item.MobSource}");
        if (!string.IsNullOrEmpty(item.ContainerSource)) sb.AppendLine($"Container: {item.ContainerSource}");
        if (!string.IsNullOrEmpty(item.PathFromCrossroads)) sb.AppendLine($"Path from crossroads: {item.PathFromCrossroads}");
        return sb.ToString().Trim();
    }

    private string FormatThreadAsText(ForumThread thread)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(thread.Title)) sb.AppendLine($"Thread: {thread.Title}");
        sb.AppendLine($"Forum: {thread.ForumName}");
        if (!string.IsNullOrEmpty(thread.FirstPostDate)) sb.AppendLine($"Date: {thread.FirstPostDate}");
        sb.AppendLine();
        for (var i = 0; i < thread.Posts.Count; i++)
        {
            var p = thread.Posts[i];
            sb.AppendLine($"--- Post {i + 1} ---");
            if (!string.IsNullOrEmpty(p.Author)) sb.AppendLine($"Author: {p.Author}");
            if (!string.IsNullOrEmpty(p.Timestamp)) sb.AppendLine($"Date: {p.Timestamp}");
            if (!string.IsNullOrEmpty(p.Title)) sb.AppendLine($"Subject: {p.Title}");
            sb.AppendLine();
            sb.AppendLine(p.Body);
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    private void WriteTextFile(string source, string id, TrainingDocument doc)
    {
        try
        {
            var dir = Path.Combine(_outputDir, "text", source);
            Directory.CreateDirectory(dir);
            var fileName = SanitizeName(id) + ".txt";
            var path = Path.Combine(dir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine($"Source: {doc.Source}");
            if (!string.IsNullOrEmpty(doc.Title)) sb.AppendLine($"Title: {doc.Title}");
            if (!string.IsNullOrEmpty(doc.Url)) sb.AppendLine($"URL: {doc.Url}");
            foreach (var (k, v) in doc.Metadata)
                sb.AppendLine($"{k}: {v}");
            sb.AppendLine("---");
            sb.AppendLine(doc.Text);

            File.WriteAllText(path, sb.ToString());
        }
        catch { /* filename issues etc - skip */ }
    }

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "unnamed";
        var safe = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') safe.Append(c);
            else if (c == ' ' || c == '.') safe.Append('_');
        }
        var result = safe.ToString();
        if (result.Length > 80) result = result.Substring(0, 80);
        return string.IsNullOrEmpty(result) ? "unnamed" : result;
    }
}
