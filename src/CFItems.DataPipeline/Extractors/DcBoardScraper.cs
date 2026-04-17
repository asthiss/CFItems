using System.Text.Json;
using System.Text.RegularExpressions;
using CFItems.DataPipeline.Models;
using HtmlAgilityPack;

namespace CFItems.DataPipeline.Extractors;

/// <summary>
/// Scrapes forums.carrionfields.com (DCForum).
/// URL patterns:
///   dcboard.php?az=show_topics&forum=F[&page=N]
///   dcboard.php?az=printer_friendly&forum=F&topic_id=T  (cleaner view with all posts)
/// </summary>
public class DcBoardScraper
{
    private readonly HttpClient _http;
    private readonly string _outputDir;
    private const int RateLimitMs = 1000;

    private static readonly int[] ForumIds = { 3, 4, 5, 6, 7, 17, 25, 43, 53, 56 };

    public DcBoardScraper(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(outputDir);
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "CFItems-PersonalResearch/1.0");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task ScrapeAllForumsAsync(int maxThreadsPerForum = int.MaxValue)
    {
        foreach (var forumId in ForumIds)
        {
            Console.WriteLine($"\n=== DCForum {forumId} ===");
            await ScrapeForumAsync(forumId, maxThreadsPerForum);
        }
    }

    public async Task ScrapeForumAsync(int forumId, int maxThreads = int.MaxValue)
    {
        var outputPath = Path.Combine(_outputDir, $"dcboard-forum{forumId}.json");

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

        string forumName = $"Forum{forumId}";

        Console.WriteLine("  Collecting topic IDs...");
        var topicIds = new List<string>();
        var seenIds = new HashSet<string>();
        var page = 1;
        var consecutiveEmpty = 0;

        while (consecutiveEmpty < 3 && page < 500)
        {
            var listUrl = page == 1
                ? $"https://forums.carrionfields.com/dcboard.php?az=show_topics&forum={forumId}"
                : $"https://forums.carrionfields.com/dcboard.php?az=show_topics&forum={forumId}&page={page}";

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

            if (html.Contains("Please Login", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("You must login", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  Forum {forumId} requires login, skipping");
                return;
            }

            if (page == 1)
            {
                var nameMatch = Regex.Match(html, @"show_topics&forum=" + forumId + @"[^""]*""[^>]*>([^<]+)</a>");
                if (nameMatch.Success) forumName = System.Net.WebUtility.HtmlDecode(nameMatch.Groups[1].Value).Trim();
            }

            var matches = Regex.Matches(html, @"topic_id=(\d+)");
            var newThisPage = 0;
            foreach (Match m in matches)
            {
                var tid = m.Groups[1].Value;
                if (seenIds.Add(tid)) { topicIds.Add(tid); newThisPage++; }
            }

            if (page == 1 || page % 10 == 0)
                Console.WriteLine($"  Page {page}: {newThisPage} new topics (total: {topicIds.Count})");

            if (newThisPage == 0) consecutiveEmpty++;
            else consecutiveEmpty = 0;

            page++;
            if (topicIds.Count >= maxThreads) break;
        }

        Console.WriteLine($"  Forum {forumId} '{forumName}': {topicIds.Count} topics found");

        var toFetch = topicIds.Where(t => !existing.ContainsKey(t)).Take(maxThreads).ToList();
        Console.WriteLine($"  New to fetch: {toFetch.Count}");

        var lastSave = DateTime.UtcNow;
        var fetched = 0;
        var failed = 0;
        foreach (var tid in toFetch)
        {
            await Task.Delay(RateLimitMs);
            try
            {
                var thread = await FetchThreadAsync(forumId, forumName, tid);
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
                if (failed < 5) Console.WriteLine($"    Error on topic {tid}: {ex.Message}");
            }

            if ((DateTime.UtcNow - lastSave).TotalMinutes > 2)
            {
                Save(outputPath, existing);
                lastSave = DateTime.UtcNow;
                Console.WriteLine($"  [checkpoint] {existing.Count} threads saved, {fetched} new, {failed} failed");
            }

            if (fetched > 0 && fetched % 20 == 0)
                Console.WriteLine($"  Progress: {fetched}/{toFetch.Count} (failed: {failed})");
        }

        Save(outputPath, existing);
        Console.WriteLine($"  Forum {forumId} complete: {existing.Count} threads saved ({failed} failed)");
    }

    private async Task<ForumThread?> FetchThreadAsync(int forumId, string forumName, string topicId)
    {
        // printer_friendly shows all posts in a clean, parse-friendly format
        var url = $"https://forums.carrionfields.com/dcboard.php?az=printer_friendly&forum={forumId}&topic_id={topicId}";
        var html = await _http.GetStringAsync(url);
        return ParseThread(html, forumId, forumName, topicId, url);
    }

    /// <summary>
    /// Parse a DCForum printer_friendly page.
    /// Format: &lt;tr class="dclite"&gt; contains:
    ///   &lt;b&gt;NNN, SUBJECT&lt;/b&gt;&lt;br /&gt;
    ///   Posted by AUTHOR on DATE&lt;br /&gt;
    ///   &lt;blockquote&gt;BODY&lt;/blockquote&gt;
    /// The first dclite row with colspan="2" is the "Topic subject" header (skip that).
    /// </summary>
    public ForumThread? ParseThread(string html, int forumId, string forumName, string topicId, string url)
    {
        var thread = new ForumThread
        {
            ThreadId = topicId,
            ForumId = forumId.ToString(),
            ForumName = forumName,
            Url = url,
            Posts = new List<ForumPost>()
        };

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Extract topic subject from the header row (first dcdark row with "Topic subject")
        var topicSubjectMatch = Regex.Match(html,
            @"Topic subject</td>\s*<td[^>]*>\s*([^<]+?)\s*</td>",
            RegexOptions.Singleline);
        if (topicSubjectMatch.Success)
            thread.Title = System.Net.WebUtility.HtmlDecode(topicSubjectMatch.Groups[1].Value).Trim();

        // Also extract forum name if we have it
        var forumNameMatch = Regex.Match(html,
            @"Forum Name</td>\s*<td[^>]*>\s*([^<]+?)\s*</td>",
            RegexOptions.Singleline);
        if (forumNameMatch.Success)
        {
            var fname = System.Net.WebUtility.HtmlDecode(forumNameMatch.Groups[1].Value).Trim();
            if (!string.IsNullOrEmpty(fname)) thread.ForumName = fname;
        }

        // Each post is a <tr class="dclite"><td colspan="2"> with <b>, "Posted by", <blockquote>
        // Use regex because HAP may normalize structure
        var postPattern = new Regex(
            @"<b>\s*(\d+)\s*,\s*([^<]*?)\s*</b>\s*<br\s*/?>\s*" +
            @"Posted by\s+([^<]+?)\s+on\s+([^<]+?)\s*<br\s*/?>\s*" +
            @"<blockquote>\s*(.*?)\s*</blockquote>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var postMatches = postPattern.Matches(html);
        foreach (Match m in postMatches)
        {
            var subject = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            var author = System.Net.WebUtility.HtmlDecode(m.Groups[3].Value).Trim();
            var date = System.Net.WebUtility.HtmlDecode(m.Groups[4].Value).Trim();
            var bodyHtml = m.Groups[5].Value;

            // Clean body HTML - convert <br>s and strip tags
            var body = Regex.Replace(bodyHtml, @"<br\s*/?>", "\n");
            body = Regex.Replace(body, @"</?p[^>]*>", "\n");
            body = Regex.Replace(body, @"<[^>]+>", "");
            body = System.Net.WebUtility.HtmlDecode(body).Trim();
            body = Regex.Replace(body, @"\n{3,}", "\n\n");

            if (body.Length < 3) continue;

            thread.Posts.Add(new ForumPost
            {
                Title = subject,
                Author = author,
                Timestamp = date,
                Body = body
            });
        }

        if (thread.Posts.Count > 0 && !string.IsNullOrEmpty(thread.Posts[0].Timestamp))
            thread.FirstPostDate = thread.Posts[0].Timestamp;

        return thread.Posts.Count > 0 ? thread : null;
    }

    private static string CleanText(string s)
    {
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\r\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private void Save(string path, Dictionary<string, ForumThread> threads)
    {
        var json = JsonSerializer.Serialize(threads, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
