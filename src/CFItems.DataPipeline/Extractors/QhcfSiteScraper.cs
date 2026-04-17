using System.Text.Json;
using System.Text.RegularExpressions;
using CFItems.DataPipeline.Models;

namespace CFItems.DataPipeline.Extractors;

/// <summary>
/// Scrapes qhcf.net /premium/* and /cgi-box/* static game info pages.
/// </summary>
public class QhcfSiteScraper
{
    private readonly HttpClient _http;
    private readonly string _outputDir;
    private const int RateLimitMs = 500;

    private static readonly (string Path, string Label)[] PagesToScrape = new[]
    {
        ("/premium/pkstats.php", "PK Statistics"),
        ("/premium/skills.php", "Skills Reference"),
        ("/premium/titlesearch.php", "Title Search"),
        ("/premium/immortals.php", "Immortals of CF"),
        ("/premium/stats.php", "Statistical Analysis"),
        ("/premium/raceclass.php", "Race/Class Table"),
        ("/premium/warrior.php", "Warrior Page"),
        ("/premium/thiefskills.php", "Thief Skills List"),
        ("/premium/thf.php", "Thief Designer"),
        ("/premium/spheres.php", "Spheres of CF"),
        ("/premium/graveyard.php", "Graveyard"),
        ("/premium/recentkillers.php", "Top 10 Killers"),
        ("/premium/besttime.php", "Immortals: Active and Best Time"),
        ("/premium/wrapchop.php", "Wrap Chop Tool"),
        ("/premium/logbin.php", "Logbin"),
        ("/cgi-box/namegen.cgi", "Name Generator"),
        ("/cgi-box/newitems/", "Item Search")
    };

    public QhcfSiteScraper(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(outputDir);
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "CFItems-PersonalResearch/1.0");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<Dictionary<string, ScrapedPage>> ScrapeAllAsync()
    {
        var results = new Dictionary<string, ScrapedPage>();

        foreach (var (path, label) in PagesToScrape)
        {
            try
            {
                await Task.Delay(RateLimitMs);
                var url = $"http://www.qhcf.net{path}";
                var html = await _http.GetStringAsync(url);

                // Check if content is in an iframe - follow it
                var iframeMatch = Regex.Match(html, @"<iframe\s+src=""([^""]+)""", RegexOptions.IgnoreCase);
                if (iframeMatch.Success)
                {
                    var iframeSrc = iframeMatch.Groups[1].Value;
                    if (!iframeSrc.StartsWith("http")) iframeSrc = "http://www.qhcf.net" + iframeSrc;
                    await Task.Delay(RateLimitMs);
                    try
                    {
                        html = await _http.GetStringAsync(iframeSrc);
                        url = iframeSrc; // update URL to point to actual content
                    }
                    catch { /* if iframe fetch fails, fall through with original html */ }
                }

                var page = new ScrapedPage
                {
                    Title = label,
                    Url = url,
                    Text = ExtractMainContent(html),
                    Metadata = new Dictionary<string, string>
                    {
                        { "source", "qhcf.net" },
                        { "path", path }
                    }
                };
                results[path] = page;
                Console.WriteLine($"  Scraped {path} ({page.Text.Length} chars) - {label}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed {path}: {ex.Message}");
            }
        }

        var outputPath = Path.Combine(_outputDir, "qhcf-site.json");
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);
        Console.WriteLine($"Saved {results.Count} pages to {outputPath}");
        return results;
    }

    /// <summary>
    /// Extract the main content area, skipping navbar/footer/ads.
    /// The qhcf.net layout has a nav, then content, then footer.
    /// </summary>
    private string ExtractMainContent(string html)
    {
        // Find content body - prefer </nav> boundary, fall back to <body> if no nav
        var navEnd = html.IndexOf("</nav>", StringComparison.OrdinalIgnoreCase);
        var bodyStart = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        var bodyEnd = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);

        int start;
        if (navEnd > 0)
            start = navEnd + 6;
        else if (bodyStart > 0)
        {
            // find end of the <body ...> tag
            var gt = html.IndexOf('>', bodyStart);
            start = gt > 0 ? gt + 1 : 0;
        }
        else start = 0;

        var end = bodyEnd > 0 ? bodyEnd : html.Length;
        if (end <= start) { start = 0; end = html.Length; }
        var content = html.Substring(start, end - start);

        // Remove ad blocks
        content = Regex.Replace(content, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline);
        content = Regex.Replace(content, @"<ins[^>]*>.*?</ins>", "", RegexOptions.Singleline);
        content = Regex.Replace(content, @"<div[^>]*adsbygoogle[^>]*>.*?</div>", "", RegexOptions.Singleline);

        // Preserve tables and pre blocks, strip everything else
        content = Regex.Replace(content, @"<br\s*/?>", "\n");
        content = Regex.Replace(content, @"</?(tr|p|div|li|h[1-6])[^>]*>", "\n");
        content = Regex.Replace(content, @"<td[^>]*>", "\t");
        content = Regex.Replace(content, @"</td>", "");
        content = Regex.Replace(content, @"<[^>]+>", "");
        content = System.Net.WebUtility.HtmlDecode(content);
        content = Regex.Replace(content, @"\r\n", "\n");
        content = Regex.Replace(content, @"[ \t]+\n", "\n");
        content = Regex.Replace(content, @"\n{3,}", "\n\n");
        return content.Trim();
    }
}
