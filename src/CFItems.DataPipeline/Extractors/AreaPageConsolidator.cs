using System.Text;
using System.Text.RegularExpressions;

namespace CFItems.DataPipeline.Extractors;

/// <summary>
/// Consolidates duplicate area HTML pages in src/area/.
/// Many wiki titles are redirects (e.g. MountKiadanaRah -> Mount_Kiadana-Rah).
/// This class:
///   1. Groups pages that represent the same area (identical ASCII map).
///   2. Picks the canonical page = the one with the most items attached.
///   3. Rewrites all links in all HTML files to point to the canonical page.
///   4. Deletes the duplicate files.
/// </summary>
public class AreaPageConsolidator
{
    private readonly string _areaDir;
    private readonly string _srcDir;

    public AreaPageConsolidator(string srcDir)
    {
        _srcDir = srcDir;
        _areaDir = Path.Combine(srcDir, "area");
    }

    public void Consolidate()
    {
        var pages = LoadPages();
        Console.WriteLine($"Loaded {pages.Count} area pages");

        // Group by content signature (map + room legend)
        var groups = pages
            .GroupBy(p => ContentSignature(p.Html))
            .Where(g => g.Count() > 1)
            .ToList();

        Console.WriteLine($"Found {groups.Count} groups of duplicates");

        // Build redirect map: duplicate filename -> canonical filename
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toDelete = new List<string>();

        foreach (var group in groups)
        {
            // Sort by item count desc (most items wins), then by preferred name style
            var ranked = group
                .OrderByDescending(p => p.ItemCount)
                .ThenByDescending(p => PreferCanonicalName(p.FileName))
                .ThenByDescending(p => p.Html.Length)
                .ToList();

            var canonical = ranked[0];
            foreach (var dup in ranked.Skip(1))
            {
                redirects[dup.FileName] = canonical.FileName;
                toDelete.Add(dup.FileName);
            }
        }

        Console.WriteLine($"Will redirect {redirects.Count} duplicate pages to {groups.Count} canonical ones");

        // Print sample
        Console.WriteLine("Sample redirects:");
        foreach (var (dup, canonical) in redirects.Take(10))
            Console.WriteLine($"  {dup} -> {canonical}");

        // Apply redirects to all HTML files and map-links.json
        RewriteAllLinks(redirects);

        // Delete duplicate files
        var deleted = 0;
        foreach (var fn in toDelete)
        {
            var path = Path.Combine(_areaDir, fn);
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted++;
            }
        }

        Console.WriteLine($"Deleted {deleted} duplicate area pages");

        // Save redirect map for future reference
        var redirectsPath = Path.Combine(_areaDir, "_redirects.json");
        var json = System.Text.Json.JsonSerializer.Serialize(redirects,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(redirectsPath, json);
        Console.WriteLine($"Saved redirect map to {redirectsPath}");
    }

    private List<PageInfo> LoadPages()
    {
        var files = Directory.GetFiles(_areaDir, "*.html");
        var pages = new List<PageInfo>();
        foreach (var f in files)
        {
            var fn = Path.GetFileName(f);
            if (fn == "index.html" || fn.StartsWith("_")) continue;
            try
            {
                var html = File.ReadAllText(f);
                var itemCount = Regex.Matches(html, @"<div class=""item-detail""").Count;
                pages.Add(new PageInfo
                {
                    FileName = fn,
                    Html = html,
                    ItemCount = itemCount
                });
            }
            catch { }
        }
        return pages;
    }

    /// <summary>
    /// Generate a signature that is identical for pages representing the same area
    /// even if their item lists differ. Uses the ASCII map content (which should be
    /// identical for wiki redirects) plus the room legend.
    /// </summary>
    private string ContentSignature(string html)
    {
        // Extract ASCII map text and room legend from the HTML.
        // The map is inside <pre id="mapPre"> in area pages.
        var sb = new StringBuilder();

        var mapMatch = Regex.Match(html, @"<pre id=""mapPre"">(.*?)</pre>", RegexOptions.Singleline);
        if (mapMatch.Success)
        {
            // Normalize: strip all HTML tags and whitespace; keep only the raw text shape
            var text = Regex.Replace(mapMatch.Groups[1].Value, @"<[^>]+>", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            sb.Append("MAP:");
            sb.Append(text);
        }

        // Also include room legend entries
        var legendMatches = Regex.Matches(html,
            @"<span class=""num"">(\d+)</span>\s*<span class=""room-name"">([^<]+)</span>");
        if (legendMatches.Count > 0)
        {
            sb.Append("|LEGEND:");
            foreach (Match m in legendMatches.OrderBy(x => int.Parse(x.Groups[1].Value)))
                sb.Append($"{m.Groups[1].Value}={m.Groups[2].Value.Trim()};");
        }

        var sig = sb.ToString();
        // If signature is empty or too short, use filename as fallback (no dedup)
        if (sig.Length < 20) return Guid.NewGuid().ToString();
        return sig;
    }

    /// <summary>
    /// Preference score for naming style. Higher = more preferred as canonical.
    /// "Mount_Kiadana-Rah.html" preferred over "MountKiadanaRah.html".
    /// </summary>
    private int PreferCanonicalName(string fileName)
    {
        var score = 0;
        // Prefer names with underscores (word separators) over PascalCase smushed
        if (fileName.Contains('_')) score += 10;
        // Prefer names with hyphens (e.g. Kiadana-Rah)
        if (fileName.Contains('-')) score += 5;
        // Prefer longer names (more descriptive)
        score += fileName.Length / 10;
        return score;
    }

    /// <summary>
    /// Rewrite all area/... references in:
    /// - every .html file in src/area/
    /// - src/map.html
    /// - src/areas.html
    /// - src/map-links.json
    /// </summary>
    private void RewriteAllLinks(Dictionary<string, string> redirects)
    {
        if (redirects.Count == 0) return;

        var htmlFiles = new List<string>();
        htmlFiles.AddRange(Directory.GetFiles(_areaDir, "*.html"));
        var mapPath = Path.Combine(_srcDir, "map.html");
        if (File.Exists(mapPath)) htmlFiles.Add(mapPath);
        var areasPath = Path.Combine(_srcDir, "areas.html");
        if (File.Exists(areasPath)) htmlFiles.Add(areasPath);

        var rewritten = 0;
        foreach (var file in htmlFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var original = content;
                foreach (var (dup, canonical) in redirects)
                {
                    // Rewrite href patterns: href="X" and href="area/X" (depends on location)
                    content = content.Replace($"href=\"{dup}\"", $"href=\"{canonical}\"");
                    content = content.Replace($"href=\"area/{dup}\"", $"href=\"area/{canonical}\"");
                    content = content.Replace($"href='../area/{dup}'", $"href='../area/{canonical}'");
                    content = content.Replace($"href=\"../area/{dup}\"", $"href=\"../area/{canonical}\"");
                }
                if (content != original)
                {
                    File.WriteAllText(file, content);
                    rewritten++;
                }
            }
            catch { }
        }
        Console.WriteLine($"Rewrote links in {rewritten} HTML files");

        // Also rewrite map-links.json
        var mapLinksPath = Path.Combine(_srcDir, "map-links.json");
        if (File.Exists(mapLinksPath))
        {
            try
            {
                var content = File.ReadAllText(mapLinksPath);
                foreach (var (dup, canonical) in redirects)
                    content = content.Replace($"\"{dup}\"", $"\"{canonical}\"");
                File.WriteAllText(mapLinksPath, content);
                Console.WriteLine($"Rewrote map-links.json");
            }
            catch { }
        }
    }

    private class PageInfo
    {
        public string FileName { get; set; } = "";
        public string Html { get; set; } = "";
        public int ItemCount { get; set; }
    }
}
