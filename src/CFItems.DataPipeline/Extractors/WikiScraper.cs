using System.Text.RegularExpressions;
using CFItems.DataPipeline.Models;

namespace CFItems.DataPipeline.Extractors;

public class WikiScraper
{
    private readonly HttpClient _http;
    private readonly string _outputDir;

    public WikiScraper(string outputDir)
    {
        _outputDir = outputDir;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "CFItems-KnowledgeBuilder/1.0");
        Directory.CreateDirectory(outputDir);
    }

    /// <summary>
    /// Scrape available data from carrionfields.net help system.
    /// Two-step: search by keyword to get IDs, then fetch by ID.
    /// </summary>
    public async Task<Dictionary<string, string>> ScrapeHelpFilesAsync(IEnumerable<string> topics)
    {
        var results = new Dictionary<string, string>();

        foreach (var topic in topics)
        {
            try
            {
                await Task.Delay(800); // Rate limit

                // Step 1: Search for help IDs
                var searchUrl = $"https://carrionfields.net/helpsearch.php?keywords={Uri.EscapeDataString(topic)}";
                var searchHtml = await _http.GetStringAsync(searchUrl);

                // Extract help IDs from search results
                var idMatches = Regex.Matches(searchHtml, @"helpsearch\.php\?id=(\d+)""[^>]*>([^<]+)</a>");
                if (idMatches.Count == 0)
                {
                    Console.WriteLine($"  No help results for: {topic}");
                    continue;
                }

                // Step 2: Pick best match - prefer title that starts with or exactly matches topic
                var bestMatch = idMatches.Cast<Match>()
                    .OrderByDescending(m => {
                        var title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).ToLower();
                        var t = topic.ToLower();
                        if (title.StartsWith(t + " ") || title == t) return 3;
                        if (title.Contains(t + " ") || title.Contains(t + "s")) return 2;
                        if (title.Contains(t)) return 1;
                        return 0;
                    })
                    .First();

                var helpId = bestMatch.Groups[1].Value;
                var helpTitle = System.Net.WebUtility.HtmlDecode(bestMatch.Groups[2].Value);

                await Task.Delay(500);
                var helpUrl = $"https://carrionfields.net/helpsearch.php?id={helpId}";
                var helpHtml = await _http.GetStringAsync(helpUrl);

                // Extract content from <pre class="help_format"> block
                var preMatch = Regex.Match(helpHtml, @"<pre[^>]*class=""help_format""[^>]*>(.*?)</pre>", RegexOptions.Singleline);
                if (preMatch.Success)
                {
                    var content = System.Net.WebUtility.HtmlDecode(preMatch.Groups[1].Value).Trim();
                    results[topic] = content;
                    Console.WriteLine($"  Scraped help: {topic} ({helpTitle})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed to scrape {topic}: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Scrape the edge list from carrionfields.net
    /// </summary>
    public async Task<string?> ScrapeEdgeListAsync()
    {
        try
        {
            var html = await _http.GetStringAsync("https://carrionfields.net/edgelist.php");
            return ExtractMainContent(html);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to scrape edge list: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get all page titles from wiki.qhcf.net via Special:AllPages pagination.
    /// </summary>
    public async Task<List<string>> GetAllQhcfPageTitlesAsync()
    {
        var titles = new HashSet<string>();
        var currentFrom = "";
        var pagesFetched = 0;
        const int maxPages = 50; // safety limit on pagination

        while (pagesFetched < maxPages)
        {
            var url = string.IsNullOrEmpty(currentFrom)
                ? "http://wiki.qhcf.net/index.php?title=Special:AllPages"
                : $"http://wiki.qhcf.net/index.php?title=Special:AllPages&from={Uri.EscapeDataString(currentFrom)}";

            await Task.Delay(300);
            var html = await _http.GetStringAsync(url);

            // Extract page titles from all links that go to /index.php?title=X
            // But only within the body content (not navigation)
            var bodyMatch = Regex.Match(html, @"<div class=""mw-allpages-body"".*?</div>|<table[^>]*class=""mw-allpages-table-chunk""[^>]*>.*?</table>|<ul class=""mw-allpages-chunk"">.*?</ul>", RegexOptions.Singleline);
            var contentToSearch = bodyMatch.Success ? bodyMatch.Value : html;

            var linkMatches = Regex.Matches(contentToSearch, @"title=""([^""]+)""");
            var newThisPage = 0;
            foreach (Match m in linkMatches)
            {
                var title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                // Skip special pages and the header links
                if (title.StartsWith("Special:") || title.StartsWith("DIKU-WIKI") ||
                    title.Contains("(redirect page)") || title.Contains("Talk:"))
                    continue;
                if (titles.Add(title)) newThisPage++;
            }

            Console.WriteLine($"  Page {pagesFetched + 1}: {newThisPage} new titles (total: {titles.Count})");

            // Find next "from=" pagination link. Pattern: href="...from=X" title="Special:AllPages">Next page
            var nextMatch = Regex.Match(html, @"from=([^""&]+)[^""]*""[^>]*title=""Special:AllPages"">Next page");
            if (!nextMatch.Success) break;

            var next = Uri.UnescapeDataString(nextMatch.Groups[1].Value.Replace("+", " "));
            if (next == currentFrom) break; // safety check
            currentFrom = next;
            pagesFetched++;
        }

        Console.WriteLine($"Found {titles.Count} total pages across {pagesFetched + 1} index pages");
        return titles.ToList();
    }

    /// <summary>
    /// Scrape ALL pages from wiki.qhcf.net and return structured data.
    /// </summary>
    public async Task<Dictionary<string, object>> ScrapeAllQhcfPagesAsync(
        List<string> titles, Action<int, int, string>? progressCallback = null)
    {
        var results = new Dictionary<string, object>();
        var total = titles.Count;
        var processed = 0;

        foreach (var title in titles)
        {
            try
            {
                await Task.Delay(350); // Be nice to the wiki
                var urlTitle = title.Replace(" ", "_");
                var url = $"http://wiki.qhcf.net/index.php?title={Uri.EscapeDataString(urlTitle)}";
                var html = await _http.GetStringAsync(url);

                var parsed = ParseQhcfPage(html);
                if (parsed.asciiMap != null || parsed.roomLegend.Count > 0 || !string.IsNullOrEmpty(parsed.rawText))
                {
                    results[title] = new
                    {
                        Title = title,
                        AsciiMap = parsed.asciiMap,
                        RoomLegend = parsed.roomLegend,
                        LinkedAreas = parsed.linkedAreas,
                        RawText = parsed.rawText
                    };
                    progressCallback?.Invoke(processed + 1, total, title);
                }
                else
                {
                    progressCallback?.Invoke(processed + 1, total, $"{title} (empty)");
                }
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                progressCallback?.Invoke(processed + 1, total, $"{title} (404)");
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke(processed + 1, total, $"{title} ERROR: {ex.Message}");
            }

            processed++;
        }

        return results;
    }

    /// <summary>
    /// Scrape area pages directly from wiki.qhcf.net (now back online).
    /// Returns dictionary keyed by URL title -> structured area data.
    /// </summary>
    public async Task<Dictionary<string, object>> ScrapeQhcfAreasAsync(
        List<(string DisplayName, string UrlTitle, string Category)> pages)
    {
        var results = new Dictionary<string, object>();

        var processed = 0;
        foreach (var (displayName, urlTitle, category) in pages)
        {
            try
            {
                await Task.Delay(400); // Rate limit - be nice to the wiki
                var url = $"http://wiki.qhcf.net/index.php?title={urlTitle}";
                var html = await _http.GetStringAsync(url);

                var parsed = ParseQhcfPage(html);
                if (parsed.asciiMap != null || parsed.roomLegend.Count > 0 || !string.IsNullOrEmpty(parsed.rawText))
                {
                    results[urlTitle] = new
                    {
                        DisplayName = displayName,
                        UrlTitle = urlTitle,
                        Category = category,
                        AsciiMap = parsed.asciiMap,
                        RoomLegend = parsed.roomLegend,
                        LinkedAreas = parsed.linkedAreas,
                        LinkedRooms = parsed.linkedRooms,
                        RawText = parsed.rawText
                    };
                    Console.WriteLine($"  [{processed + 1}/{pages.Count}] {displayName} -> {parsed.roomLegend.Count} rooms, {parsed.linkedAreas.Count} links");
                }
                else
                {
                    Console.WriteLine($"  [{processed + 1}/{pages.Count}] {displayName} - no content");
                }
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                Console.WriteLine($"  [{processed + 1}/{pages.Count}] {displayName} - page not found");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{processed + 1}/{pages.Count}] {displayName} - error: {ex.Message}");
            }

            processed++;
        }

        return results;
    }

    /// <summary>
    /// Parse a qhcf.net wiki area page. Extracts:
    /// - ASCII map (content inside the first &lt;pre&gt; tag)
    /// - Room legend (lines like "1 - Small Room", "2 - Attic")
    /// - Links to other areas (linked pages)
    /// - Raw text content
    /// </summary>
    private (string? asciiMap, Dictionary<string, string> roomLegend, List<string> linkedAreas, List<string> linkedRooms, string rawText) ParseQhcfPage(string html)
    {
        // Find the main content div
        var contentMatch = Regex.Match(html, @"<div id=""mw-content-text""[^>]*>(.*?)(?=<div class=""printfooter""|<!-- /bodyContent -->)", RegexOptions.Singleline);
        if (!contentMatch.Success)
            return (null, new Dictionary<string, string>(), new List<string>(), new List<string>(), "");

        var contentHtml = contentMatch.Groups[1].Value;

        // Extract ASCII map from first <pre> block
        string? asciiMap = null;
        var preMatch = Regex.Match(contentHtml, @"<pre[^>]*>(.*?)</pre>", RegexOptions.Singleline);
        if (preMatch.Success)
        {
            var preContent = preMatch.Groups[1].Value;
            // Strip HTML tags but preserve text
            asciiMap = Regex.Replace(preContent, @"<a[^>]*>([^<]+)</a>", "$1");
            asciiMap = Regex.Replace(asciiMap, @"<[^>]+>", "");
            asciiMap = System.Net.WebUtility.HtmlDecode(asciiMap);
        }

        // Extract linked areas from <a> tags
        var linkedAreas = new List<string>();
        var linkedRooms = new List<string>();
        var linkMatches = Regex.Matches(contentHtml, @"<a\s+href=""[^""]*title=([^""&]+)[^""]*""[^>]*>([^<]+)</a>");
        foreach (Match m in linkMatches)
        {
            var title = Uri.UnescapeDataString(m.Groups[1].Value);
            if (!linkedAreas.Contains(title))
                linkedAreas.Add(title);
        }

        // Extract full text (no HTML)
        var rawText = Regex.Replace(contentHtml, @"<br\s*/?>", "\n");
        rawText = Regex.Replace(rawText, @"<p[^>]*>", "\n");
        rawText = Regex.Replace(rawText, @"</p>", "\n");
        rawText = Regex.Replace(rawText, @"<[^>]+>", "");
        rawText = System.Net.WebUtility.HtmlDecode(rawText).Trim();

        // Extract room legend: lines like "1 - Small Room", "23 - The Guildhall"
        var roomLegend = new Dictionary<string, string>();
        var legendMatches = Regex.Matches(rawText, @"^\s*(\d+)\s*-\s*(.+?)\s*$", RegexOptions.Multiline);
        foreach (Match m in legendMatches)
        {
            var num = m.Groups[1].Value;
            var roomName = m.Groups[2].Value.Trim();
            if (roomName.Length > 1 && roomName.Length < 100)
                roomLegend[num] = roomName;
        }

        return (asciiMap, roomLegend, linkedAreas, linkedRooms, rawText);
    }

    /// <summary>
    /// Try to fetch area data from wiki.qhcf.net via Wayback Machine
    /// </summary>
    public async Task<Dictionary<string, string>> ScrapeWaybackAreasAsync(IEnumerable<string> areaNames)
    {
        var results = new Dictionary<string, string>();

        foreach (var area in areaNames)
        {
            try
            {
                await Task.Delay(2000); // Be nice to Wayback Machine
                var wikiTitle = area.Replace(" ", "");
                var waybackUrl = $"https://web.archive.org/web/2024/http://wiki.qhcf.net/index.php?title={wikiTitle}";

                var html = await _http.GetStringAsync(waybackUrl);
                var content = ExtractWikiContent(html);
                if (!string.IsNullOrEmpty(content))
                {
                    results[area] = content;
                    Console.WriteLine($"  Wayback area: {area}");
                }
            }
            catch (Exception ex)
            {
                // Wayback often 404s, that's expected
                if (!ex.Message.Contains("404"))
                    Console.WriteLine($"  Wayback failed for {area}: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Try to fetch the CarrItems list from Wayback Machine
    /// </summary>
    public async Task<string?> ScrapeWaybackItemListAsync()
    {
        try
        {
            var url = "https://web.archive.org/web/2024/http://wiki.qhcf.net/carritems.txt";
            var content = await _http.GetStringAsync(url);
            if (!string.IsNullOrEmpty(content))
            {
                var outputPath = Path.Combine(_outputDir, "carritems-wayback.txt");
                await File.WriteAllTextAsync(outputPath, content);
                Console.WriteLine($"Saved Wayback item list to {outputPath}");
                return content;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch Wayback item list: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Scrape general game info from carrionfields.net main page
    /// </summary>
    public async Task<Dictionary<string, object>> ScrapeGameInfoAsync()
    {
        var info = new Dictionary<string, object>();

        // Known help topics for races, classes, and cabals
        var races = new[] { "human", "elf", "half-elf", "dwarf", "gnome", "duergar",
            "dark-elf", "arial", "felar", "minotaur", "orc", "halfling",
            "svirfnebli", "storm giant", "fire giant", "cloud giant" };

        var classes = new[] { "warrior", "thief", "assassin", "ranger", "paladin",
            "anti-paladin", "bard", "healer", "shaman", "druid", "conjurer",
            "invoker", "transmuter", "necromancer", "shapeshifter" };

        var cabals = new[] { "battle ragers", "fortress", "herald", "tribunal",
            "empire", "outlander", "scion" };

        Console.WriteLine("Scraping race help files...");
        var raceHelp = await ScrapeHelpFilesAsync(races);
        info["races"] = raceHelp;

        Console.WriteLine("Scraping class help files...");
        var classHelp = await ScrapeHelpFilesAsync(classes);
        info["classes"] = classHelp;

        Console.WriteLine("Scraping cabal help files...");
        var cabalHelp = await ScrapeHelpFilesAsync(cabals);
        info["cabals"] = cabalHelp;

        // General game topics
        var generalTopics = new[] { "alignment", "ethos", "skills", "spells",
            "experience", "practice", "train", "religion", "edges", "limited items" };

        Console.WriteLine("Scraping general help files...");
        var generalHelp = await ScrapeHelpFilesAsync(generalTopics);
        info["general"] = generalHelp;

        return info;
    }

    private string? ExtractHelpContent(string html)
    {
        // The help search page wraps content in a <pre> or specific div
        var preMatch = Regex.Match(html, @"<pre[^>]*>(.*?)</pre>", RegexOptions.Singleline);
        if (preMatch.Success)
            return CleanHtml(preMatch.Groups[1].Value);

        // Try to extract from body content
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.Singleline);
        if (bodyMatch.Success)
            return CleanHtml(bodyMatch.Groups[1].Value);

        return null;
    }

    private string? ExtractMainContent(string html)
    {
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.Singleline);
        return bodyMatch.Success ? CleanHtml(bodyMatch.Groups[1].Value) : null;
    }

    private string? ExtractWikiContent(string html)
    {
        // MediaWiki content is in div#mw-content-text
        var contentMatch = Regex.Match(html, @"<div[^>]*id=""mw-content-text""[^>]*>(.*?)</div>\s*</div>", RegexOptions.Singleline);
        if (contentMatch.Success)
            return CleanHtml(contentMatch.Groups[1].Value);

        return ExtractMainContent(html);
    }

    private string CleanHtml(string html)
    {
        // Remove HTML tags but keep line breaks
        html = Regex.Replace(html, @"<br\s*/?>", "\n");
        html = Regex.Replace(html, @"<p[^>]*>", "\n");
        html = Regex.Replace(html, @"</p>", "\n");
        html = Regex.Replace(html, @"<[^>]+>", "");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }
}
