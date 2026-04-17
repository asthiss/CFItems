using System.Text;
using System.Text.Json;
using CFItems.DataPipeline.Models;

namespace CFItems.DataPipeline.Extractors;

public class AreaPageGenerator
{
    private readonly Dictionary<string, JsonElement> _wikiPages;
    private readonly Dictionary<string, ItemLocation> _itemLocations;
    private readonly List<ItemRecord> _items;
    private readonly List<AreaInfo> _areas;

    // Map from display name -> wiki URL title for linking
    private readonly Dictionary<string, string> _nameToFile;

    public AreaPageGenerator(
        Dictionary<string, JsonElement> wikiPages,
        Dictionary<string, ItemLocation> itemLocations,
        List<ItemRecord> items,
        List<AreaInfo> areas)
    {
        _wikiPages = wikiPages;
        _itemLocations = itemLocations;
        _items = items;
        _areas = areas;

        _nameToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in wikiPages.Keys)
        {
            var fileName = MakeFileName(title);
            _nameToFile[title] = fileName;
            _nameToFile[title.Replace("_", " ")] = fileName;
            _nameToFile[title.Replace(" ", "")] = fileName;
        }
    }

    /// <summary>
    /// Generate HTML pages for all areas with wiki data.
    /// Returns count of pages generated.
    /// </summary>
    public int GenerateAllPages(string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var count = 0;
        foreach (var (title, data) in _wikiPages)
        {
            try
            {
                var html = GeneratePageHtml(title, data);
                if (html != null)
                {
                    var fileName = MakeFileName(title);
                    var path = Path.Combine(outputDir, fileName);
                    File.WriteAllText(path, html);
                    count++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating {title}: {ex.Message}");
            }
        }

        // Also generate an index page
        GenerateIndexPage(outputDir);
        return count;
    }

    private string? GeneratePageHtml(string title, JsonElement data)
    {
        string? asciiMap = null;
        var roomLegend = new Dictionary<string, string>();
        string? rawText = null;
        var linkedAreas = new List<string>();

        if (data.TryGetProperty("AsciiMap", out var mapEl) && mapEl.ValueKind == JsonValueKind.String)
            asciiMap = mapEl.GetString();
        if (data.TryGetProperty("RoomLegend", out var legendEl) && legendEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in legendEl.EnumerateObject())
                roomLegend[prop.Name] = prop.Value.GetString() ?? "";
        }
        if (data.TryGetProperty("RawText", out var rawEl) && rawEl.ValueKind == JsonValueKind.String)
            rawText = rawEl.GetString();
        if (data.TryGetProperty("LinkedAreas", out var linksEl) && linksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in linksEl.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                    linkedAreas.Add(el.GetString() ?? "");
            }
        }

        // Find items associated with this area
        var matchedItems = FindItemsForArea(title);

        if (string.IsNullOrEmpty(asciiMap) && !matchedItems.Any() && string.IsNullOrEmpty(rawText))
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>{EscapeHtml(title)} - Carrion Fields</title>");
        sb.AppendLine("<link rel=\"stylesheet\" type=\"text/css\" href=\"../cfitems.css\">");
        sb.AppendLine(@"<style>
        #mapContainer { overflow: auto; padding: 10px; background: #0a0a0a; border: 1px solid #333; margin: 10px 0; }
        #mapPre { font-family: 'Courier New', monospace; font-size: 13px; line-height: 1.3; color: #ccc; white-space: pre; }
        .legend-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 6px; margin: 10px 0; }
        .legend-entry { padding: 4px 8px; border-left: 2px solid #444; background: #111; font-size: 13px; }
        .legend-entry .num { color: #dfbe6f; font-weight: bold; margin-right: 8px; }
        .legend-entry .room-name { color: #fff; }
        .legend-entry .item-list { display: block; margin-top: 4px; color: #8a8; font-size: 12px; }
        .legend-entry.has-items { background: #1a1810; border-left-color: #dfbe6f; }
        .item-detail { padding: 8px; border-left: 2px solid #444; margin: 6px 0; background: #0f0f0f; font-size: 13px; }
        .item-detail b { color: #dfbe6f; }
        .item-detail .meta { color: #888; font-size: 12px; }
        .item-detail .mob { color: #c97; }
        .item-detail .room { color: #8ab; }
        .item-detail .path { color: #aaa; font-family: monospace; }
        h2 { color: #dfbe6f; }
        h3 { color: #c9a45c; margin-top: 20px; }
        .area-nav { color: #888; font-size: 13px; margin: 5px 0; }
        .area-nav a { color: #dfbe6f; }
        </style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<div id=\"topNav\">");
        sb.AppendLine("<a href=\"../index.html\">Items</a>");
        sb.AppendLine("<a href=\"../knowledge.html\">Knowledge</a>");
        sb.AppendLine("<a href=\"../map.html\">Map</a>");
        sb.AppendLine("<a href=\"../areas.html\">Areas</a>");
        sb.AppendLine("<a href=\"../documents.html\">Documents</a>");
        sb.AppendLine("</div>");
        sb.AppendLine("<hr/>");

        sb.AppendLine($"<h2>{EscapeHtml(title)}</h2>");

        // Area metadata
        var matchingArea = _areas.FirstOrDefault(a =>
            a.Name.Equals(title, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Replace(" ", "").Equals(title.Replace(" ", "").Replace("_", ""), StringComparison.OrdinalIgnoreCase));
        if (matchingArea != null)
        {
            sb.Append("<div class=\"area-nav\">");
            if (!string.IsNullOrEmpty(matchingArea.LevelRange))
                sb.Append($"Level: {EscapeHtml(matchingArea.LevelRange)} | ");
            if (!string.IsNullOrEmpty(matchingArea.Builder))
                sb.Append($"Builder: {EscapeHtml(matchingArea.Builder)} | ");
            if (!string.IsNullOrEmpty(matchingArea.Category))
                sb.Append($"Category: {EscapeHtml(matchingArea.Category)}");
            sb.AppendLine("</div>");
            if (matchingArea.IsRestricted)
                sb.AppendLine("<p style=\"color:#c44\">RESTRICTED area - map sharing not allowed.</p>");
        }

        // ASCII Map with clickable links
        if (!string.IsNullOrEmpty(asciiMap))
        {
            sb.AppendLine("<h3>Area Map</h3>");
            sb.AppendLine("<div id=\"mapContainer\"><pre id=\"mapPre\">");
            sb.Append(LinkifyMap(asciiMap));
            sb.AppendLine("</pre></div>");
        }

        // Room legend with items per room
        if (roomLegend.Count > 0)
        {
            var itemsByRoomNum = GroupItemsByRoomNumber(matchedItems, roomLegend);
            sb.AppendLine($"<h3>Rooms ({roomLegend.Count})</h3>");
            sb.AppendLine("<div class=\"legend-grid\">");
            foreach (var num in roomLegend.Keys.OrderBy(k => int.TryParse(k, out var n) ? n : 999))
            {
                var hasItems = itemsByRoomNum.TryGetValue(num, out var roomItems) && roomItems.Any();
                sb.Append($"<div class=\"legend-entry{(hasItems ? " has-items" : "")}\">");
                sb.Append($"<span class=\"num\">{num}</span>");
                sb.Append($"<span class=\"room-name\">{EscapeHtml(roomLegend[num])}</span>");
                if (hasItems)
                {
                    sb.Append("<span class=\"item-list\">");
                    sb.Append(string.Join(", ", roomItems!.Select(i => $"<a href=\"#item-{EscapeHtml(SlugifyName(i.name))}\" style=\"color:#8a8\">{EscapeHtml(i.name)}</a>")));
                    sb.Append("</span>");
                }
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        // Linked areas
        if (linkedAreas.Count > 0)
        {
            sb.AppendLine("<h3>Connected Areas</h3>");
            sb.AppendLine("<div class=\"area-nav\">");
            foreach (var link in linkedAreas.Distinct().Take(30))
            {
                var linkFile = _nameToFile.GetValueOrDefault(link, MakeFileName(link));
                sb.Append($"<a href=\"{linkFile}\">{EscapeHtml(link)}</a> &bull; ");
            }
            sb.AppendLine("</div>");
        }

        // All items in this area (with full identification data)
        if (matchedItems.Any())
        {
            sb.AppendLine($"<h3>Items Found Here ({matchedItems.Count})</h3>");
            foreach (var (itemName, item, loc) in matchedItems.OrderBy(i => i.name))
            {
                sb.Append($"<div class=\"item-detail\" id=\"item-{EscapeHtml(SlugifyName(itemName))}\">");
                sb.Append($"<b>{EscapeHtml(itemName)}</b>");
                if (item != null)
                {
                    sb.Append($" <span class=\"meta\">L{EscapeHtml(item.Level ?? "?")} {EscapeHtml(item.Group ?? "?")}");
                    if (!string.IsNullOrEmpty(item.Type)) sb.Append($" / {EscapeHtml(item.Type)}");
                    if (!string.IsNullOrEmpty(item.Material)) sb.Append($" / {EscapeHtml(item.Material)}");
                    sb.Append("</span>");
                }

                var room = loc?.BestGuessRoom ?? "";
                var mob = loc?.BestGuessMob ?? item?.MobSource ?? "";
                var container = loc?.BestGuessContainer ?? item?.ContainerSource ?? "";
                var path = loc?.PathFromCrossroads ?? item?.PathFromCrossroads ?? "";

                if (!string.IsNullOrEmpty(room))
                    sb.Append($"<br><span class=\"room\">Room:</span> {EscapeHtml(room)}");
                if (!string.IsNullOrEmpty(mob))
                    sb.Append($"<br><span class=\"mob\">Mob:</span> {EscapeHtml(mob)}");
                if (!string.IsNullOrEmpty(container))
                    sb.Append($"<br><span style=\"color:#8a8;\">Container:</span> {EscapeHtml(container)}");
                if (!string.IsNullOrEmpty(path))
                    sb.Append($"<br><span class=\"path\">Path from Crossroads:</span> <code>{EscapeHtml(path)}</code>");

                if (item != null)
                {
                    if (item.IsWeapon)
                        sb.Append($"<br><span class=\"meta\">Weapon:</span> {EscapeHtml(item.Damnoun ?? "?")} (avg {EscapeHtml(item.Avg ?? "?")})");
                    if (!string.IsNullOrEmpty(item.FlaggsPiped))
                        sb.Append($"<br><span class=\"meta\">Flags:</span> {EscapeHtml(item.FlaggsPiped.Replace("|", ", "))}");
                    if (!string.IsNullOrEmpty(item.ModifiersPiped))
                        sb.Append($"<br><span class=\"meta\">Modifiers:</span> {EscapeHtml(item.ModifiersPiped.Replace("|", ", "))}");
                    if (!string.IsNullOrEmpty(item.ArmorLine))
                        sb.Append($"<br><span class=\"meta\">Armor:</span> {EscapeHtml(item.ArmorLine)}");
                }

                sb.AppendLine("</div>");
            }
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private Dictionary<string, List<(string name, ItemRecord? item, ItemLocation? loc)>>
        GroupItemsByRoomNumber(
            List<(string name, ItemRecord? item, ItemLocation? loc)> items,
            Dictionary<string, string> roomLegend)
    {
        var result = new Dictionary<string, List<(string, ItemRecord?, ItemLocation?)>>();

        foreach (var entry in items)
        {
            var room = entry.loc?.BestGuessRoom ?? "";
            if (string.IsNullOrEmpty(room)) continue;

            // Match room to legend number
            foreach (var (num, legendName) in roomLegend)
            {
                if (room.Equals(legendName, StringComparison.OrdinalIgnoreCase) ||
                    room.Contains(legendName, StringComparison.OrdinalIgnoreCase) ||
                    legendName.Contains(room, StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.ContainsKey(num))
                        result[num] = new List<(string, ItemRecord?, ItemLocation?)>();
                    result[num].Add(entry);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Aggressively normalize an area name for loose matching:
    /// lowercase, strip spaces/underscores/apostrophes/hyphens/punctuation.
    /// So "Ar'atouldain", "Aratouldain", "Ar_atouldain" all collapse to "aratouldain".
    /// </summary>
    private static string NormalizeArea(string s) =>
        new string((s ?? "").ToLower().Where(char.IsLetterOrDigit).ToArray());

    private List<(string name, ItemRecord? item, ItemLocation? loc)> FindItemsForArea(string areaTitle)
    {
        var matches = new List<(string, ItemRecord?, ItemLocation?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Normalized forms of the title for matching. Also try stripping "The " prefix.
        var titleNorm = NormalizeArea(areaTitle);
        var titleNormNoThe = NormalizeArea(areaTitle.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
            ? areaTitle.Substring(4) : areaTitle);

        bool MatchesArea(string? candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            var c = NormalizeArea(candidate);
            if (c.Length == 0) return false;
            return c == titleNorm || c == titleNormNoThe ||
                   c.Contains(titleNorm) || titleNorm.Contains(c) ||
                   c.Contains(titleNormNoThe) || titleNormNoThe.Contains(c);
        }

        // Items from Azure Table with Area field
        foreach (var item in _items)
        {
            if (MatchesArea(item.Area))
            {
                if (seen.Add(item.Name ?? ""))
                {
                    _itemLocations.TryGetValue(item.Name ?? "", out var loc);
                    matches.Add((item.Name ?? "", item, loc));
                }
            }
        }

        // Items from itemLocations with BestGuessArea
        foreach (var (name, loc) in _itemLocations)
        {
            if (MatchesArea(loc.BestGuessArea))
            {
                if (seen.Add(name))
                {
                    var dbItem = _items.FirstOrDefault(i => i.Name == name);
                    matches.Add((name, dbItem, loc));
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Replace wiki links in the ASCII map (kept as text by our scraper) with
    /// clickable links to the corresponding area page.
    /// The scraper already stripped HTML, so the map is plain text. We need to
    /// detect area names that appear and make them clickable.
    /// </summary>
    private string LinkifyMap(string asciiMap)
    {
        // The ascii map is already plain text (stripped HTML during scrape).
        // HTML-escape it first, then try to linkify known area names.
        var escaped = EscapeHtml(asciiMap);

        // Sort by length descending to avoid partial matches
        var knownNames = _nameToFile.Keys
            .Where(n => n.Length >= 4)
            .OrderByDescending(n => n.Length)
            .ToList();

        // Only linkify first occurrence of each name to avoid message bloat
        var linkedOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in knownNames)
        {
            if (linkedOnce.Contains(name)) continue;
            var escName = EscapeHtml(name);

            // Match word-boundary-ish: surrounded by whitespace or punctuation
            var pattern = System.Text.RegularExpressions.Regex.Escape(escName);
            var regex = new System.Text.RegularExpressions.Regex($@"(?<![A-Za-z0-9_]){pattern}(?![A-Za-z0-9_])");
            var match = regex.Match(escaped);
            if (match.Success)
            {
                var file = _nameToFile[name];
                escaped = regex.Replace(escaped, $"<a href=\"{file}\" style=\"color:#dfbe6f\">{escName}</a>", 1);
                linkedOnce.Add(name);
            }
        }

        return escaped;
    }

    private void GenerateIndexPage(string outputDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine("<title>All Areas - Carrion Fields</title>");
        sb.AppendLine("<link rel=\"stylesheet\" type=\"text/css\" href=\"../cfitems.css\">");
        sb.AppendLine(@"<style>
        .area-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(250px, 1fr)); gap: 8px; }
        .area-entry { padding: 8px 12px; border-left: 3px solid #dfbe6f; background: #111; }
        .area-entry a { color: #dfbe6f; text-decoration: none; font-weight: bold; }
        .area-entry a:hover { text-decoration: underline; }
        .area-entry .meta { color: #888; font-size: 12px; }
        .search { padding: 8px 12px; width: 300px; background: #1a1a1a; color: white; border: 1px solid #dfbe6f; margin: 10px 0; }
        </style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<div id=\"topNav\">");
        sb.AppendLine("<a href=\"../index.html\">Items</a>");
        sb.AppendLine("<a href=\"../knowledge.html\">Knowledge</a>");
        sb.AppendLine("<a href=\"../map.html\">Map</a>");
        sb.AppendLine("<a href=\"../areas.html\">Areas</a>");
        sb.AppendLine("<a href=\"../documents.html\">Documents</a>");
        sb.AppendLine("</div>");
        sb.AppendLine("<hr/>");
        sb.AppendLine("<h2 style=\"color:#dfbe6f\">All Areas</h2>");
        sb.AppendLine($"<p style=\"color:#888\">{_wikiPages.Count} pages scraped from wiki.qhcf.net with items from game logs added.</p>");
        sb.AppendLine("<input type=\"text\" class=\"search\" id=\"search\" placeholder=\"Filter areas...\" oninput=\"filter()\">");
        sb.AppendLine("<div class=\"area-grid\" id=\"grid\">");

        foreach (var title in _wikiPages.Keys.OrderBy(t => t))
        {
            var fileName = MakeFileName(title);
            var items = FindItemsForArea(title);
            sb.Append($"<div class=\"area-entry\" data-name=\"{EscapeHtml(title.ToLower())}\">");
            sb.Append($"<a href=\"{fileName}\">{EscapeHtml(title)}</a>");
            if (items.Any())
                sb.Append($"<div class=\"meta\">{items.Count} items</div>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine(@"<script>
        function filter() {
            var q = document.getElementById('search').value.toLowerCase();
            document.querySelectorAll('.area-entry').forEach(el => {
                el.style.display = el.dataset.name.includes(q) ? '' : 'none';
            });
        }
        </script>");
        sb.AppendLine("</body></html>");

        File.WriteAllText(Path.Combine(outputDir, "index.html"), sb.ToString());
    }

    public static string MakeFileName(string title)
    {
        // Convert title to a safe filename
        var clean = title
            .Replace(" ", "_")
            .Replace("'", "")
            .Replace("\"", "")
            .Replace("/", "-")
            .Replace("\\", "-")
            .Replace(":", "")
            .Replace("*", "")
            .Replace("?", "")
            .Replace("<", "")
            .Replace(">", "")
            .Replace("|", "");
        // Remove any remaining non-safe chars
        var safe = new StringBuilder();
        foreach (var c in clean)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                safe.Append(c);
        }
        return safe.ToString() + ".html";
    }

    private static string SlugifyName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLower(c));
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static string EscapeHtml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
