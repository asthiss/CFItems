using System.Text.Json;
using System.Text.RegularExpressions;
using CFItems.DataPipeline.Models;
using HtmlAgilityPack;

namespace CFItems.DataPipeline.Extractors;

/// <summary>
/// Scrapes qhcf.net's Phorum BB (8 boards).
/// URL patterns:
///   list.php?N[,page=M]         - forum list (paginated)
///   read.php?N,threadId,msgId   - thread (shows all messages)
/// </summary>
public class PhorumScraper
{
    private readonly HttpClient _http;
    private readonly string _outputDir;
    private const int RateLimitMs = 500;

    private static readonly Dictionary<int, string> BoardNames = new()
    {
        {2, "Main"}, {3, "Log"}, {4, "VIP"}, {5, "Char"},
        {6, "Event"}, {7, "Script"}, {8, "OT"}, {9, "Feedback"}
    };

    public PhorumScraper(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(outputDir);
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "CFItems-PersonalResearch/1.0");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task ScrapeAllBoardsAsync(int maxThreadsPerBoard = int.MaxValue)
    {
        foreach (var (boardId, boardName) in BoardNames)
        {
            Console.WriteLine($"\n=== Scraping Board {boardId}: {boardName} ===");
            await ScrapeBoardAsync(boardId, boardName, maxThreadsPerBoard);
        }
    }

    public async Task ScrapeBoardAsync(int boardId, string boardName, int maxThreads = int.MaxValue)
    {
        var outputPath = Path.Combine(_outputDir, $"qhcf-phorum-board{boardId}.json");

        var existing = new Dictionary<string, ForumThread>();
        if (File.Exists(outputPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, ForumThread>>(File.ReadAllText(outputPath));
                if (loaded != null) existing = loaded;
                Console.WriteLine($"  Resuming: {existing.Count} threads already scraped");
            }
            catch { }
        }

        Console.WriteLine("  Collecting thread IDs from list pages...");
        var threadIds = new List<string>();
        var seenIds = new HashSet<string>();
        var page = 1;
        var maxPage = int.MaxValue;
        var consecutiveEmpty = 0;

        while (page <= maxPage && consecutiveEmpty < 3)
        {
            var listUrl = page == 1
                ? $"http://www.qhcf.net/phorum/list.php?{boardId}"
                : $"http://www.qhcf.net/phorum/list.php?{boardId},page={page}";

            await Task.Delay(RateLimitMs);
            string html;
            try { html = await _http.GetStringAsync(listUrl); }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error fetching list page={page}: {ex.Message}. Retrying in 5s...");
                await Task.Delay(5000);
                try { html = await _http.GetStringAsync(listUrl); }
                catch { consecutiveEmpty++; page++; continue; }
            }

            if (page == 1)
            {
                var pageNumbers = Regex.Matches(html, @"list\.php\?" + boardId + @",page=(\d+)");
                if (pageNumbers.Count > 0)
                {
                    maxPage = pageNumbers.Cast<Match>().Max(m => int.Parse(m.Groups[1].Value));
                    Console.WriteLine($"  Board has {maxPage} pages");
                }
            }

            var matches = Regex.Matches(html, @"read\.php\?" + boardId + @",(\d+),\d+");
            var newThisPage = 0;
            foreach (Match m in matches)
            {
                var tid = m.Groups[1].Value;
                if (seenIds.Add(tid)) { threadIds.Add(tid); newThisPage++; }
            }

            if (page % 20 == 0 || page == 1)
                Console.WriteLine($"  List page {page}/{maxPage}: {newThisPage} new threads (total: {threadIds.Count})");

            if (newThisPage == 0) consecutiveEmpty++;
            else consecutiveEmpty = 0;

            page++;
            if (threadIds.Count >= maxThreads) break;
        }

        Console.WriteLine($"  Total thread IDs found: {threadIds.Count}");

        var toFetch = threadIds.Where(t => !existing.ContainsKey(t)).Take(maxThreads).ToList();
        Console.WriteLine($"  New to fetch: {toFetch.Count}");

        var lastSave = DateTime.UtcNow;
        var fetched = 0;
        var failed = 0;
        foreach (var tid in toFetch)
        {
            await Task.Delay(RateLimitMs);
            try
            {
                var thread = await FetchThreadAsync(boardId, boardName, tid);
                if (thread != null && thread.Posts.Count > 0)
                {
                    existing[tid] = thread;
                    fetched++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (failed < 5) Console.WriteLine($"    Error on thread {tid}: {ex.Message}");
            }

            if ((DateTime.UtcNow - lastSave).TotalMinutes > 2)
            {
                SaveThreads(outputPath, existing);
                lastSave = DateTime.UtcNow;
                Console.WriteLine($"  [checkpoint] {existing.Count} threads saved, {fetched} new, {failed} failed");
            }

            if (fetched > 0 && fetched % 25 == 0)
                Console.WriteLine($"  Progress: {fetched}/{toFetch.Count} (failed: {failed})");
        }

        SaveThreads(outputPath, existing);
        Console.WriteLine($"  Board {boardId} complete: {existing.Count} threads saved ({failed} failed)");
    }

    private async Task<ForumThread?> FetchThreadAsync(int boardId, string boardName, string threadId)
    {
        var url = $"http://www.qhcf.net/phorum/read.php?{boardId},{threadId},{threadId}";
        var html = await _http.GetStringAsync(url);
        return ParseThread(html, boardId, boardName, threadId, url);
    }

    /// <summary>
    /// Parses a Phorum read.php page using HtmlAgilityPack for proper DOM parsing.
    /// Each message is in a &lt;div class="message"&gt; with child divs for author/date/body.
    /// </summary>
    public ForumThread? ParseThread(string html, int boardId, string boardName, string threadId, string url)
    {
        var thread = new ForumThread
        {
            ThreadId = threadId,
            ForumId = boardId.ToString(),
            ForumName = boardName,
            Url = url,
            Posts = new List<ForumPost>()
        };

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Title from #top > h1
        var topH1 = doc.DocumentNode.SelectSingleNode("//div[@id='top']//h1");
        if (topH1 != null)
            thread.Title = CleanText(topH1.InnerText);

        // Find all message divs
        var messageDivs = doc.DocumentNode.SelectNodes("//div[@class='message']");
        if (messageDivs == null || messageDivs.Count == 0)
            return null;

        foreach (var msgDiv in messageDivs)
        {
            var authorNode = msgDiv.SelectSingleNode(".//div[contains(@class,'message-author')]");
            var dateNode = msgDiv.SelectSingleNode(".//div[@class='message-date']");
            var bodyNode = msgDiv.SelectSingleNode(".//div[@class='message-body']");

            if (bodyNode == null) continue;

            var body = ExtractBodyText(bodyNode);
            if (body.Length < 3) continue;

            var post = new ForumPost
            {
                Author = authorNode != null ? CleanText(authorNode.InnerText) : null,
                Timestamp = dateNode != null ? CleanText(dateNode.InnerText) : null,
                Body = body
            };
            thread.Posts.Add(post);
        }

        if (thread.Posts.Count > 0 && !string.IsNullOrEmpty(thread.Posts[0].Timestamp))
            thread.FirstPostDate = thread.Posts[0].Timestamp;

        return thread.Posts.Count > 0 ? thread : null;
    }

    /// <summary>
    /// Extract body text from a message-body node. Preserves game-log formatting
    /// from &lt;pre&gt; blocks, strips ads/scripts.
    /// </summary>
    private static string ExtractBodyText(HtmlNode bodyNode)
    {
        // Remove scripts, ads, iframes
        var toRemove = bodyNode.SelectNodes(".//script|.//ins|.//iframe");
        if (toRemove != null)
            foreach (var n in toRemove.ToList()) n.Remove();

        // Prefer <pre> content if present (game logs)
        var preNode = bodyNode.SelectSingleNode(".//pre");
        if (preNode != null)
            return CleanText(preNode.InnerText);

        return CleanText(bodyNode.InnerText);
    }

    private static string CleanText(string s)
    {
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\r\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private void SaveThreads(string path, Dictionary<string, ForumThread> threads)
    {
        var json = JsonSerializer.Serialize(threads, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
