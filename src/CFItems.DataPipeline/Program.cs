using System.Text.Json;
using CFItems.DataPipeline.Extractors;
using CFItems.DataPipeline.Models;
using CFItems.DataPipeline.Services;

// Azure Storage configuration
const string sasToken = "sv=2025-11-05&ss=bfqt&srt=sco&sp=rwdlacupiytfx&se=2030-04-15T16:21:07Z&st=2026-04-16T08:06:07Z&spr=https&sig=REDACTED_SAS_SIG%3D";
const string tableEndpoint = "https://cfitems.table.core.windows.net/";
const string blobEndpoint = "https://cfitems.blob.core.windows.net/";

// Paths
var repoRoot = FindRepoRoot();
var dataDir = Path.Combine(repoRoot, "data");
var logsDir = Path.Combine(dataDir, "logs");
var itemsExportPath = Path.Combine(dataDir, "items-export.json");
var locationsOutputPath = Path.Combine(dataDir, "item-locations.json");
var areasOutputPath = Path.Combine(dataDir, "knowledge", "areas.json");
var seedMappingsPath = Path.Combine(dataDir, "seed-mappings.json");

Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "knowledge"));

var storageService = new AzureStorageService(sasToken, tableEndpoint, blobEndpoint);
var seedParser = new SeedDataParser();

var worldMapPath = Path.Combine(dataDir, "world-map.json");

var command = args.Length > 0 ? args[0] : "all";

switch (command)
{
    case "export":
        await RunExport();
        break;
    case "download-logs":
        await RunDownloadLogs();
        break;
    case "parse-seeds":
        RunParseSeeds();
        break;
    case "extract-locations":
        RunExtractLocations();
        break;
    case "build-map":
        RunBuildMap();
        break;
    case "find-paths":
        RunFindPaths();
        break;
    case "backfill":
        await RunBackfill();
        break;
    case "reset-locations":
        await RunResetLocations();
        break;
    case "clear-paths":
        await RunClearPaths();
        break;
    case "all":
        await RunAll();
        break;
    case "scrape-wiki":
        await RunScrapeWiki();
        break;
    case "scrape-qhcf-areas":
        await RunScrapeQhcfAreas();
        break;
    case "scrape-qhcf-all":
        await RunScrapeQhcfAll();
        break;
    case "scrape-phorum":
        await RunScrapePhorum();
        break;
    case "scrape-dcboard":
        await RunScrapeDcBoard();
        break;
    case "scrape-qhcf-site":
        await RunScrapeQhcfSite();
        break;
    case "scrape-all-sites":
        await RunScrapeAllSites();
        break;
    case "build-training-data":
        RunBuildTrainingData();
        break;
    case "generate-area-pages":
        RunGenerateAreaPages();
        break;
    case "consolidate-area-pages":
        RunConsolidateAreaPages();
        break;
    case "generate-map-links":
        RunGenerateMapLinks();
        break;
    case "generate-areas-index":
        RunGenerateAreasIndex();
        break;
    case "scrape-worldmap":
        await RunScrapeWorldMap();
        break;
    case "upload-knowledge":
        await RunUploadKnowledge();
        break;
    case "stats":
        RunStats();
        break;
    default:
        Console.WriteLine("Commands: export|download-logs|parse-seeds|extract-locations|build-map|find-paths|backfill|scrape-wiki|upload-knowledge|all|stats");
        break;
}

async Task RunAll()
{
    Console.WriteLine("=== CFItems Data Pipeline - Full Run ===\n");

    // Step 1: Export items
    Console.WriteLine("--- Step 1: Export Items ---");
    await RunExport();

    // Step 2: Download logs
    Console.WriteLine("\n--- Step 2: Download Log Files ---");
    await RunDownloadLogs();

    // Step 3: Parse seed data
    Console.WriteLine("\n--- Step 3: Parse Seed Data ---");
    var (areas, seedMappings) = RunParseSeeds();

    // Step 4: Extract locations from logs
    Console.WriteLine("\n--- Step 4: Extract Item Locations ---");
    RunExtractLocations(areas, seedMappings);

    // Step 5: Build world map
    Console.WriteLine("\n--- Step 5: Build World Map ---");
    RunBuildMap();

    // Step 6: Find paths from crossroads
    Console.WriteLine("\n--- Step 6: Calculate Paths from Crossroads ---");
    RunFindPaths();

    // Step 7: Backfill Azure
    Console.WriteLine("\n--- Step 7: Backfill Azure Table ---");
    await RunBackfill();

    Console.WriteLine("\n=== Pipeline Complete ===");
}

async Task RunExport()
{
    var items = await storageService.ExportAllItemsAsync(itemsExportPath);
    var withArea = items.Count(i => !string.IsNullOrEmpty(i.Area));
    Console.WriteLine($"Items with Area: {withArea}/{items.Count} ({100.0 * withArea / items.Count:F1}%)");
}

async Task RunDownloadLogs()
{
    await storageService.DownloadLogFilesAsync(logsDir);
}

(List<AreaInfo> areas, List<SeedItemMapping> seedMappings) RunParseSeeds()
{
    // Parse areas.txt
    var areasFile = Path.Combine(repoRoot, "docs", "areas.txt");
    var areas = File.Exists(areasFile)
        ? seedParser.ParseAreasFile(areasFile)
        : new List<AreaInfo>();

    // Parse Links file for categories
    var linksFile = Path.Combine(repoRoot, "docs", "Links to get data from.txt");
    if (File.Exists(linksFile))
    {
        var (categories, restricted) = seedParser.ParseLinksFile(linksFile);
        foreach (var area in areas)
        {
            if (categories.TryGetValue(area.Name, out var cat))
                area.Category = cat;
            if (restricted.Contains(area.Name))
                area.IsRestricted = true;
        }

        // Add restricted areas that aren't in areas.txt
        foreach (var restrictedName in restricted)
        {
            if (!areas.Any(a => a.Name.Equals(restrictedName, StringComparison.OrdinalIgnoreCase)))
            {
                areas.Add(new AreaInfo { Name = restrictedName, IsRestricted = true });
            }
        }
    }

    // Parse equipment file for seed mappings
    var eqFile = Path.Combine(repoRoot, "src", "documents", "EqToLookForGoodieWarrior.txt");
    var seedMappings = File.Exists(eqFile)
        ? seedParser.ParseEquipmentFile(eqFile)
        : new List<SeedItemMapping>();

    // Save parsed data
    var json = JsonSerializer.Serialize(areas, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(areasOutputPath, json);

    json = JsonSerializer.Serialize(seedMappings, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(seedMappingsPath, json);

    Console.WriteLine($"Saved {areas.Count} areas to {areasOutputPath}");
    Console.WriteLine($"Saved {seedMappings.Count} seed mappings to {seedMappingsPath}");

    return (areas, seedMappings);
}

void RunExtractLocations(List<AreaInfo>? areas = null, List<SeedItemMapping>? seedMappings = null)
{
    // Load seed data if not provided
    if (areas == null && File.Exists(areasOutputPath))
        areas = JsonSerializer.Deserialize<List<AreaInfo>>(File.ReadAllText(areasOutputPath)) ?? new();
    if (seedMappings == null && File.Exists(seedMappingsPath))
        seedMappings = JsonSerializer.Deserialize<List<SeedItemMapping>>(File.ReadAllText(seedMappingsPath)) ?? new();

    areas ??= new List<AreaInfo>();
    seedMappings ??= new List<SeedItemMapping>();

    if (!Directory.Exists(logsDir))
    {
        Console.WriteLine($"No logs directory found at {logsDir}. Run 'download-logs' first.");
        return;
    }

    var extractor = new LogLocationExtractor(areas, seedMappings);
    var locations = extractor.ProcessAllLogs(logsDir);

    // Merge seed mappings as high-confidence entries
    foreach (var seed in seedMappings)
    {
        if (!locations.TryGetValue(seed.ItemName, out var existing))
        {
            existing = new ItemLocation { ItemName = seed.ItemName };
            locations[seed.ItemName] = existing;
        }

        var entry = new LocationEntry
        {
            AreaName = seed.AreaName,
            MobName = seed.MobName,
            Confidence = "high",
            SourceLog = "EqToLookForGoodieWarrior.txt"
        };

        if (!existing.Locations.Any(l => l.AreaName == entry.AreaName && l.MobName == entry.MobName))
        {
            existing.Locations.Add(entry);
        }

        // Update best guess if seed data is available
        if (string.IsNullOrEmpty(existing.BestGuessArea) && !string.IsNullOrEmpty(seed.AreaName))
        {
            existing.BestGuessArea = seed.AreaName;
        }
    }

    var json = JsonSerializer.Serialize(locations, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(locationsOutputPath, json);

    var withArea = locations.Values.Count(l => !string.IsNullOrEmpty(l.BestGuessArea));
    Console.WriteLine($"\nResults: {locations.Count} items with location data, {withArea} with area name");
    Console.WriteLine($"Saved to {locationsOutputPath}");
}

async Task RunClearPaths()
{
    // 1. Clear paths in local item-locations.json
    if (File.Exists(locationsOutputPath))
    {
        var locations = JsonSerializer.Deserialize<Dictionary<string, ItemLocation>>(
            File.ReadAllText(locationsOutputPath)) ?? new();
        var cleared = 0;
        foreach (var loc in locations.Values)
        {
            if (!string.IsNullOrEmpty(loc.PathFromCrossroads))
            {
                loc.PathFromCrossroads = null;
                cleared++;
            }
        }
        var json = JsonSerializer.Serialize(locations, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(locationsOutputPath, json);
        Console.WriteLine($"Cleared PathFromCrossroads from {cleared} items in {locationsOutputPath}");
    }

    // 2. Clear PathFromCrossroads for every item in Azure Table
    Console.WriteLine("Clearing PathFromCrossroads in Azure Table...");
    var uri = new Uri($"{tableEndpoint}cfitems?{sasToken}");
    var tableClient = new Azure.Data.Tables.TableClient(uri);

    var allItems = new List<ItemRecord>();
    await foreach (var item in tableClient.QueryAsync<ItemRecord>())
        allItems.Add(item);

    var updated = 0;
    var failed = 0;
    foreach (var item in allItems)
    {
        if (string.IsNullOrEmpty(item.PathFromCrossroads)) continue;
        try
        {
            item.PathFromCrossroads = null;
            await tableClient.UpsertEntityAsync(item);
            updated++;
            if (updated % 50 == 0) Console.WriteLine($"  Cleared {updated} items...");
        }
        catch (Exception ex)
        {
            failed++;
            if (failed <= 3) Console.WriteLine($"  Failed on '{item.RowKey}': {ex.Message}");
        }
    }
    Console.WriteLine($"Done. Cleared paths on {updated} items in Azure Table ({failed} failures)");
}

async Task RunResetLocations()
{
    if (!File.Exists(locationsOutputPath))
    {
        Console.WriteLine("No item-locations.json found. Run 'extract-locations' first.");
        return;
    }

    var locations = JsonSerializer.Deserialize<Dictionary<string, ItemLocation>>(
        File.ReadAllText(locationsOutputPath)) ?? new();

    // Build dict of items to update with fresh data (only from the cleaner extraction)
    var updates = locations
        .Where(kv => kv.Value.Locations.Any(l => l.Confidence is "high" or "medium"))
        .Where(kv => !string.IsNullOrEmpty(kv.Value.BestGuessArea) ||
                     !string.IsNullOrEmpty(kv.Value.BestGuessMob) ||
                     !string.IsNullOrEmpty(kv.Value.BestGuessContainer) ||
                     !string.IsNullOrEmpty(kv.Value.PathFromCrossroads))
        .ToDictionary(
            kv => kv.Key,
            kv => ((string?)kv.Value.BestGuessArea, (string?)kv.Value.BestGuessMob,
                   (string?)kv.Value.BestGuessContainer, (string?)kv.Value.PathFromCrossroads));

    Console.WriteLine($"Will update {updates.Count} items with fresh data and clear the rest.");
    await storageService.ResetAndUpdateAllLocationsAsync(updates);
}

async Task RunBackfill()
{
    if (!File.Exists(locationsOutputPath))
    {
        Console.WriteLine("No item-locations.json found. Run 'extract-locations' first.");
        return;
    }

    var locations = JsonSerializer.Deserialize<Dictionary<string, ItemLocation>>(
        File.ReadAllText(locationsOutputPath)) ?? new();

    // Build update records with all new fields
    var updates = locations
        .Where(kv => kv.Value.Locations.Any(l => l.Confidence is "high" or "medium"))
        .Where(kv => !string.IsNullOrEmpty(kv.Value.BestGuessArea) ||
                     !string.IsNullOrEmpty(kv.Value.BestGuessMob) ||
                     !string.IsNullOrEmpty(kv.Value.PathFromCrossroads))
        .Select(kv => new {
            Name = kv.Key,
            Area = kv.Value.BestGuessArea,
            Mob = kv.Value.BestGuessMob,
            Container = kv.Value.BestGuessContainer,
            Path = kv.Value.PathFromCrossroads
        })
        .ToList();

    Console.WriteLine($"Backfilling {updates.Count} items...");
    await storageService.BatchUpdateAllFieldsAsync(updates.ToDictionary(
        u => u.Name,
        u => (u.Area, u.Mob, u.Container, u.Path)));
}

async Task RunScrapeWiki()
{
    var knowledgeDir = Path.Combine(dataDir, "knowledge");
    var scraper = new WikiScraper(knowledgeDir);

    Console.WriteLine("Scraping game info from carrionfields.net...");
    var gameInfo = await scraper.ScrapeGameInfoAsync();

    var json = JsonSerializer.Serialize(gameInfo, new JsonSerializerOptions { WriteIndented = true });
    var outputPath = Path.Combine(knowledgeDir, "game-info.json");
    await File.WriteAllTextAsync(outputPath, json);
    Console.WriteLine($"Saved game info to {outputPath}");

    // Try Wayback Machine for area data
    Console.WriteLine("\nTrying Wayback Machine for area data...");
    var areas = File.Exists(areasOutputPath)
        ? JsonSerializer.Deserialize<List<AreaInfo>>(File.ReadAllText(areasOutputPath)) ?? new()
        : new List<AreaInfo>();

    // Only try non-restricted areas
    var areasToScrape = areas
        .Where(a => !a.IsRestricted)
        .Select(a => a.Name)
        .Take(20) // Start with first 20 to test
        .ToList();

    var areaData = await scraper.ScrapeWaybackAreasAsync(areasToScrape);

    if (areaData.Any())
    {
        json = JsonSerializer.Serialize(areaData, new JsonSerializerOptions { WriteIndented = true });
        outputPath = Path.Combine(knowledgeDir, "area-wiki-data.json");
        await File.WriteAllTextAsync(outputPath, json);
        Console.WriteLine($"Saved {areaData.Count} area wiki entries to {outputPath}");
    }

    // Try item list from Wayback
    Console.WriteLine("\nTrying Wayback Machine for item list...");
    await scraper.ScrapeWaybackItemListAsync();
}

async Task RunScrapeWorldMap()
{
    // Fetch the WorldMap page which already has embedded <a> tags
    var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "CFItems-KnowledgeBuilder/1.0");
    var html = await http.GetStringAsync("http://wiki.qhcf.net/index.php?title=WorldMap");

    // Find the content div
    var contentMatch = System.Text.RegularExpressions.Regex.Match(html,
        @"<div id=""mw-content-text""[^>]*>(.*?)(?=<div class=""printfooter""|<!-- /bodyContent -->)",
        System.Text.RegularExpressions.RegexOptions.Singleline);
    if (!contentMatch.Success)
    {
        Console.WriteLine("Could not find content div on WorldMap page");
        return;
    }

    var content = contentMatch.Groups[1].Value;

    // Extract the first <pre> block - that's the map
    var preMatch = System.Text.RegularExpressions.Regex.Match(content, @"<pre>(.*?)</pre>",
        System.Text.RegularExpressions.RegexOptions.Singleline);
    if (!preMatch.Success)
    {
        Console.WriteLine("Could not find <pre> map block");
        return;
    }

    var mapHtml = preMatch.Groups[1].Value;

    // Rewrite all wiki links to point to our area/ directory
    // Pattern: <a href="http://wiki.qhcf.net/index.php?title=TITLE" ...>TEXT</a>
    var linkRegex = new System.Text.RegularExpressions.Regex(
        @"<a\s+href=""http://wiki\.qhcf\.net/index\.php\?title=([^""]+)""\s+title=""[^""]*""(?:\s+class=""[^""]*"")?\s*>([^<]+)</a>");

    var areaDir = Path.Combine(FindRepoRoot(), "src", "area");

    var rewritten = linkRegex.Replace(mapHtml, m =>
    {
        var rawTitle = Uri.UnescapeDataString(m.Groups[1].Value);
        var text = m.Groups[2].Value;

        // Build the area page filename using the same logic as AreaPageGenerator
        var fileName = AreaPageGenerator.MakeFileName(rawTitle);
        var filePath = Path.Combine(areaDir, fileName);

        // Only create a link if the area page exists
        if (File.Exists(filePath))
        {
            return $"<a href=\"area/{fileName}\" class=\"area-link\" title=\"{System.Net.WebUtility.HtmlEncode(rawTitle)}\">{text}</a>";
        }
        // Otherwise just the plain text
        return text;
    });

    // Colorize water (~ characters) outside of HTML tags
    var colorized = ColorizeWater(rewritten);

    // Save the fragment
    var outputPath = Path.Combine(FindRepoRoot(), "src", "worldmap-fragment.html");
    File.WriteAllText(outputPath, colorized);
    Console.WriteLine($"Saved linkified world map to {outputPath}");

    // Also build the full map.html
    BuildMapHtmlFromFragment(colorized);
}

/// <summary>
/// Wraps every `~` character outside of HTML tags in a blue-colored span
/// (matching the water legend color). Tracks tag state to avoid touching
/// `~` inside attribute values.
/// </summary>
string ColorizeWater(string html)
{
    var sb = new System.Text.StringBuilder(html.Length + 1000);
    var inTag = false;
    foreach (var c in html)
    {
        if (c == '<') inTag = true;
        else if (c == '>') { inTag = false; sb.Append(c); continue; }

        if (!inTag && c == '~')
        {
            sb.Append("<span style=\"color:#68a\">~</span>");
        }
        else
        {
            sb.Append(c);
        }
    }
    return sb.ToString();
}

void BuildMapHtmlFromFragment(string mapFragment)
{
    var output = $@"<!DOCTYPE html>
<html lang=""en"" xmlns=""http://www.w3.org/1999/xhtml"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Map of Thera - Carrion Fields</title>
    <link rel=""stylesheet"" type=""text/css"" href=""cfitems.css"">
    <style>
        #mapContainer {{ overflow: auto; padding: 10px; background: #0a0a0a; border: 1px solid #333; }}
        #mapPre {{ font-family: 'Courier New', monospace; font-size: 13px; line-height: 1.2; color: #ccc; white-space: pre; }}
        /* Override cfitems.css margin on anchors which breaks ASCII alignment */
        #mapPre a, #mapPre .area-link {{ color: #dfbe6f; text-decoration: none; margin: 0; padding: 0; }}
        #mapPre a:hover, #mapPre .area-link:hover {{ color: #fff; background: #333; text-decoration: underline; margin: 0; }}
        #mapSearch {{ padding: 8px 12px; width: 300px; background: #1a1a1a; color: white; border: 1px solid #dfbe6f; margin: 10px 0; }}
        .legend {{ padding: 10px; background: #111; border: 1px solid #333; margin: 10px 0; font-size: 14px; }}
        .legend span {{ margin-right: 15px; }}
        .controls {{ margin: 10px 0; }}
        .controls button {{ padding: 5px 12px; background: #222; color: #dfbe6f; border: 1px solid #dfbe6f; cursor: pointer; margin-right: 5px; }}
        .controls button:hover {{ background: #333; }}
        .highlight {{ background: #553300 !important; color: #fff !important; }}
    </style>
</head>
<body>
    <div id=""topNav"">
        <a href=""index.html"">Items</a>
        <a href=""knowledge.html"">Knowledge</a>
        <a href=""map.html"">Map</a>
        <a href=""areas.html"">Areas</a>
        <a href=""documents.html"">Documents</a>
    </div>
    <hr/>

    <h2 style=""color:#dfbe6f"">Map of Thera</h2>
    <p style=""color:#888;font-size:13px;"">Map from wiki.qhcf.net with links to local area pages (gold = clickable).</p>

    <div class=""controls"">
        <input type=""text"" id=""mapSearch"" placeholder=""Search for an area on the map..."">
        <button onclick=""zoomIn()"">Zoom +</button>
        <button onclick=""zoomOut()"">Zoom -</button>
        <button onclick=""resetZoom()"">Reset</button>
    </div>

    <div class=""legend"">
        <span style=""color:#dfbe6f"">Gold = Clickable Area</span>
        <span style=""color:#7a7"">** = Major City</span>
        <span style=""color:#aaa"">--- = Road/Path</span>
        <span style=""color:#68a"">~~~ = Water</span>
    </div>

    <div id=""mapContainer"">
        <pre id=""mapPre"">{mapFragment}</pre>
    </div>

    <script>
    var currentFontSize = 13;

    document.getElementById('mapSearch').addEventListener('input', (e) => {{
        const query = e.target.value.toLowerCase();
        document.querySelectorAll('#mapPre .area-link').forEach(el => {{
            el.classList.remove('highlight');
            if (query && el.textContent.toLowerCase().includes(query)) {{
                el.classList.add('highlight');
                el.scrollIntoView({{ behavior: 'smooth', block: 'center', inline: 'center' }});
            }}
        }});
    }});

    function zoomIn() {{ currentFontSize = Math.min(24, currentFontSize + 2); document.getElementById('mapPre').style.fontSize = currentFontSize + 'px'; }}
    function zoomOut() {{ currentFontSize = Math.max(8, currentFontSize - 2); document.getElementById('mapPre').style.fontSize = currentFontSize + 'px'; }}
    function resetZoom() {{ currentFontSize = 13; document.getElementById('mapPre').style.fontSize = '13px'; }}
    </script>
</body>
</html>
";

    var mapPath = Path.Combine(FindRepoRoot(), "src", "map.html");
    File.WriteAllText(mapPath, output);
    Console.WriteLine($"Rebuilt map.html at {mapPath}");
}

void RunGenerateAreasIndex()
{
    var knowledgeDir = Path.Combine(dataDir, "knowledge");
    var wikiPath = Path.Combine(knowledgeDir, "qhcf-wiki-all.json");
    if (!File.Exists(wikiPath))
    {
        Console.WriteLine("qhcf-wiki-all.json not found.");
        return;
    }

    // Load wiki titles
    var doc = JsonDocument.Parse(File.ReadAllText(wikiPath));
    var allTitles = new List<string>();
    foreach (var prop in doc.RootElement.EnumerateObject())
        allTitles.Add(prop.Name);

    // Load areas metadata for grouping
    var areas = File.Exists(areasOutputPath)
        ? JsonSerializer.Deserialize<List<AreaInfo>>(File.ReadAllText(areasOutputPath)) ?? new()
        : new List<AreaInfo>();

    // Load items for counts
    var items = File.Exists(itemsExportPath)
        ? JsonSerializer.Deserialize<List<ItemRecord>>(File.ReadAllText(itemsExportPath)) ?? new()
        : new List<ItemRecord>();

    // Load item locations for richer counting (BestGuessArea from log extraction)
    var itemLocations = File.Exists(locationsOutputPath)
        ? JsonSerializer.Deserialize<Dictionary<string, ItemLocation>>(File.ReadAllText(locationsOutputPath)) ?? new()
        : new Dictionary<string, ItemLocation>();

    // Known area pages (filter out non-area pages like skills/faqs/etc)
    var areaPageDir = Path.Combine(FindRepoRoot(), "src", "area");

    // Build HTML
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("<!DOCTYPE html>");
    sb.AppendLine("<html lang=\"en\"><head>");
    sb.AppendLine("<meta charset=\"utf-8\" />");
    sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
    sb.AppendLine("<title>All Areas - Carrion Fields</title>");
    sb.AppendLine("<link rel=\"stylesheet\" type=\"text/css\" href=\"cfitems.css\">");
    sb.AppendLine(@"<style>
        .areas-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 6px; margin: 10px 0; }
        .area-entry { padding: 8px 12px; border-left: 3px solid #444; background: #111; position: relative; padding-right: 32px; }
        .area-entry a { color: #aaa; text-decoration: none; font-weight: bold; }
        .area-entry a:hover { text-decoration: underline; color: #fff; }
        .area-entry .meta { color: #888; font-size: 12px; margin-top: 2px; }
        /* Highlight areas with known items - same style as knowledge.html */
        .area-entry.has-items { border-left-color: #dfbe6f; background: #1a1610; }
        .area-entry.has-items a { color: #dfbe6f; }
        .area-entry.has-items::after {
            content: ""\2605""; /* star */
            position: absolute; top: 6px; right: 10px;
            color: #dfbe6f; font-size: 18px;
        }
        .search { padding: 10px 14px; width: 100%; max-width: 400px; background: #1a1a1a; color: white; border: 1px solid #dfbe6f; font-size: 15px; margin: 10px 0; box-sizing: border-box; }
        .letter-nav { display: flex; flex-wrap: wrap; gap: 4px; margin: 15px 0; padding: 8px; background: #111; border: 1px solid #333; }
        .letter-nav a { color: #dfbe6f; padding: 4px 8px; text-decoration: none; border: 1px solid #333; border-radius: 3px; }
        .letter-nav a:hover { background: #333; }
        .stats { color: #888; font-size: 13px; margin: 5px 0; }
        .letter-section { margin-top: 20px; }
        .letter-heading { color: #dfbe6f; border-bottom: 1px solid #333; padding-bottom: 4px; margin-top: 20px; scroll-margin-top: 10px; }
        .type-tabs { display: flex; gap: 0; margin: 10px 0; border-bottom: 2px solid #dfbe6f; }
        .type-tab { padding: 6px 16px; cursor: pointer; color: #aaa; border: 1px solid transparent; border-bottom: none; }
        .type-tab.active { color: #dfbe6f; background: #1a1a1a; border-color: #dfbe6f; border-radius: 4px 4px 0 0; }
    </style>");
    sb.AppendLine("</head><body>");
    sb.AppendLine("<div id=\"topNav\">");
    sb.AppendLine("<a href=\"index.html\">Items</a>");
    sb.AppendLine("<a href=\"knowledge.html\">Knowledge</a>");
    sb.AppendLine("<a href=\"map.html\">Map</a>");
    sb.AppendLine("<a href=\"areas.html\">Areas</a>");
    sb.AppendLine("<a href=\"documents.html\">Documents</a>");
    sb.AppendLine("</div>");
    sb.AppendLine("<hr/>");
    sb.AppendLine("<h2 style=\"color:#dfbe6f\">All Areas</h2>");

    // Stats
    var withItems = 0;
    foreach (var t in allTitles)
    {
        if (items.Any(i => i.Area != null && i.Area.Contains(t, StringComparison.OrdinalIgnoreCase))) withItems++;
    }
    sb.AppendLine($"<p class=\"stats\">{allTitles.Count} pages scraped from wiki.qhcf.net.</p>");

    sb.AppendLine("<input type=\"text\" class=\"search\" id=\"search\" placeholder=\"Search areas...\" oninput=\"filterAreas()\">");

    // Filter tabs
    sb.AppendLine("<div class=\"type-tabs\">");
    sb.AppendLine("<div class=\"type-tab active\" onclick=\"setFilter('all', this)\">All</div>");
    sb.AppendLine("<div class=\"type-tab\" onclick=\"setFilter('with-items', this)\">With Items</div>");
    sb.AppendLine("<div class=\"type-tab\" onclick=\"setFilter('areas-only', this)\">Areas Only (No FAQs/Skills)</div>");
    sb.AppendLine("</div>");

    // Only include titles whose area page actually exists
    var validTitles = allTitles
        .Where(t => File.Exists(Path.Combine(areaPageDir, AreaPageGenerator.MakeFileName(t))))
        .ToList();

    // Letter navigation - only letters that have at least one page
    sb.AppendLine("<div class=\"letter-nav\">");
    var letters = validTitles.Select(t => char.ToUpper(t[0])).Distinct().OrderBy(c => c).ToList();
    foreach (var letter in letters)
    {
        sb.AppendLine($"<a href=\"#letter-{letter}\">{letter}</a>");
    }
    sb.AppendLine("</div>");

    // Group by first letter
    var grouped = validTitles
        .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
        .GroupBy(t => char.ToUpper(t[0]))
        .OrderBy(g => g.Key);

    foreach (var group in grouped)
    {
        sb.AppendLine($"<div class=\"letter-section\" data-letter=\"{group.Key}\">");
        sb.AppendLine($"<h3 class=\"letter-heading\" id=\"letter-{group.Key}\">{group.Key}</h3>");
        sb.AppendLine("<div class=\"areas-grid\">");
        foreach (var title in group)
        {
            var fileName = AreaPageGenerator.MakeFileName(title);
            var hasPage = File.Exists(Path.Combine(areaPageDir, fileName));
            if (!hasPage) continue;

            // Try to find matching area info
            var areaInfo = areas.FirstOrDefault(a =>
                a.Name.Equals(title, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Replace(" ", "").Equals(title.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

            // Count items for this area - same matching logic as AreaPageGenerator
            // (aggressive normalization: lowercase + strip non-alphanumeric).
            string Norm(string s) => new string((s ?? "").ToLower().Where(char.IsLetterOrDigit).ToArray());
            var titleNorm = Norm(title);
            var titleNormNoThe = Norm(title.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
                ? title.Substring(4) : title);

            bool MatchArea(string? candidate)
            {
                if (string.IsNullOrEmpty(candidate)) return false;
                var c = Norm(candidate);
                if (c.Length == 0) return false;
                return c == titleNorm || c == titleNormNoThe ||
                       c.Contains(titleNorm) || titleNorm.Contains(c) ||
                       c.Contains(titleNormNoThe) || titleNormNoThe.Contains(c);
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in items)
                if (i.Name != null && MatchArea(i.Area)) matched.Add(i.Name);
            foreach (var (name, loc) in itemLocations)
                if (MatchArea(loc.BestGuessArea)) matched.Add(name);
            var itemCount = matched.Count;

            // Classify: is this an "area" or a helpfile/FAQ/skill page?
            var isArea = !IsHelpfilePage(title);

            sb.Append($"<div class=\"area-entry{(itemCount > 0 ? " has-items" : "")}\"");
            sb.Append($" data-name=\"{title.ToLower()}\"");
            sb.Append($" data-has-items=\"{(itemCount > 0 ? "1" : "0")}\"");
            sb.Append($" data-is-area=\"{(isArea ? "1" : "0")}\"");
            sb.Append(">");
            sb.Append($"<a href=\"area/{fileName}\">{EscapeHtml(title)}</a>");

            var metaParts = new List<string>();
            if (areaInfo != null)
            {
                if (!string.IsNullOrEmpty(areaInfo.LevelRange)) metaParts.Add($"L{areaInfo.LevelRange}");
                if (!string.IsNullOrEmpty(areaInfo.Category)) metaParts.Add(areaInfo.Category);
            }
            if (itemCount > 0) metaParts.Add($"{itemCount} items");

            if (metaParts.Count > 0)
                sb.Append($"<div class=\"meta\">{string.Join(" • ", metaParts)}</div>");

            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    sb.AppendLine(@"<script>
    let currentFilter = 'all';
    function filterAreas() {
        const q = document.getElementById('search').value.toLowerCase();
        document.querySelectorAll('.area-entry').forEach(el => {
            const matchesSearch = !q || el.dataset.name.includes(q);
            const matchesFilter = currentFilter === 'all' ||
                (currentFilter === 'with-items' && el.dataset.hasItems === '1') ||
                (currentFilter === 'areas-only' && el.dataset.isArea === '1');
            el.style.display = (matchesSearch && matchesFilter) ? '' : 'none';
        });
        // Hide empty letter sections
        document.querySelectorAll('.letter-section').forEach(sec => {
            const visible = [...sec.querySelectorAll('.area-entry')].some(el => el.style.display !== 'none');
            sec.style.display = visible ? '' : 'none';
        });
    }
    function setFilter(f, el) {
        currentFilter = f;
        document.querySelectorAll('.type-tab').forEach(t => t.classList.remove('active'));
        el.classList.add('active');
        filterAreas();
    }
    </script>");
    sb.AppendLine("</body></html>");

    var outputPath = Path.Combine(FindRepoRoot(), "src", "areas.html");
    File.WriteAllText(outputPath, sb.ToString());
    Console.WriteLine($"Generated areas.html with {allTitles.Count} area entries");
    Console.WriteLine($"Saved to {outputPath}");
}

static bool IsHelpfilePage(string title)
{
    var lower = title.ToLower();
    // Helpfile/FAQ/skill patterns
    if (lower.EndsWith("edgehelpfiles") || lower.EndsWith("edgesummaries") ||
        lower.EndsWith("helpfiles") || lower.EndsWith("skills") || lower.EndsWith("spells") ||
        lower.EndsWith("faq") || lower.Contains("edge list") || lower.Contains("edgelist"))
        return true;
    if (lower == "about" || lower == "allareas" || lower == "arealist" || lower == "arealist2" ||
        lower == "areaexplore" || lower == "armor types" || lower == "arms" || lower == "axe" ||
        lower == "anothertest" || lower == "broland" || lower == "about" || lower == "cf" ||
        lower == "cf-n" || lower == "pk" || lower == "sword" || lower == "shield" ||
        lower == "wand" || lower == "weapon" || lower == "head" || lower == "legs" ||
        lower == "main page" || lower == "main_page" || lower == "recentchanges" ||
        lower == "rankinglist" || lower == "rankingequipmentgood" || lower == "rankingequipmentevil" ||
        lower == "rangerranking" || lower == "worldmap" || lower == "newbiemapper")
        return true;
    if (lower.StartsWith("ship ") || lower.StartsWith("ranking") || lower.StartsWith("item list") ||
        lower.StartsWith("items list") || lower.Contains("getting started") || lower.Contains("newbie") ||
        lower.StartsWith("class ") || lower.Contains("guide"))
        return true;
    return false;
}

static string EscapeHtml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

void RunGenerateMapLinks()
{
    // Load all wiki titles
    var titlesCache = Path.Combine(dataDir, "knowledge", "qhcf-all-titles.json");
    if (!File.Exists(titlesCache))
    {
        Console.WriteLine("qhcf-all-titles.json not found. Run 'scrape-qhcf-all' first.");
        return;
    }
    var titles = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(titlesCache)) ?? new();

    // Load map text
    var mapPath = Path.Combine(FindRepoRoot(), "docs", "MapOfThera.txt");
    if (!File.Exists(mapPath))
    {
        Console.WriteLine($"MapOfThera.txt not found at {mapPath}");
        return;
    }
    var mapText = File.ReadAllText(mapPath);

    // Build mapping: text in map -> area page filename
    var mapping = new Dictionary<string, string>(StringComparer.Ordinal);

    // Pre-build normalized lookup for titles
    var titleByNormalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var t in titles)
    {
        titleByNormalized[t] = t;
        var noSp = t.Replace(" ", "").Replace("-", "").Replace("'", "");
        if (!titleByNormalized.ContainsKey(noSp)) titleByNormalized[noSp] = t;
    }

    // Look for title matches in map text (multi-word phrases first)
    foreach (var title in titles.OrderByDescending(t => t.Length))
    {
        if (title.Length < 4) continue;
        // Try exact phrase match
        if (mapText.Contains(title, StringComparison.Ordinal))
        {
            var fileName = AreaPageGenerator.MakeFileName(title);
            var areaPagePath = Path.Combine(FindRepoRoot(), "src", "area", fileName);
            if (File.Exists(areaPagePath))
                mapping[title] = fileName;
        }
    }

    // Also scan for individual capitalized words in the map that match titles
    var wordMatches = System.Text.RegularExpressions.Regex.Matches(mapText, @"[A-Z][a-zA-Z][a-zA-Z0-9']{3,}");
    foreach (System.Text.RegularExpressions.Match m in wordMatches)
    {
        var word = m.Value;
        if (mapping.ContainsKey(word)) continue;
        if (titleByNormalized.TryGetValue(word, out var title))
        {
            var fileName = AreaPageGenerator.MakeFileName(title);
            var areaPagePath = Path.Combine(FindRepoRoot(), "src", "area", fileName);
            if (File.Exists(areaPagePath))
                mapping[word] = fileName;
        }
    }

    // Also handle common variations:
    // - Uppercase shortcuts like "GALADON", "HAMSAH", "MU'TAZZ", "SEANTRYN", "MODAN", "VORALIAN"
    // - Hyphenated like "Delar-Tol"
    var variants = new Dictionary<string, string>
    {
        ["GALADON"] = "Galadon",
        ["HAMSAH"] = "Hamsah Mu'tazz",
        ["MU'TAZZ"] = "Hamsah Mu'tazz",
        ["SEANTRYN"] = "Seantryn Modan",
        ["MODAN"] = "Seantryn Modan",
        ["VORALIAN"] = "Voralian City",
        ["THERA"] = "World Map",
        ["STONE"] = "Stones Embrace",
        ["UNDERGROUND"] = "Underdark",
        ["Delar-Tol"] = "DelarTol",
        ["Mu'tazz"] = "Hamsah Mu'tazz",
        ["Eastern Road"] = "EasternRoad",
        ["Western"] = "WesternAryth",
        ["LochGry"] = "Loch Grynmear",
        ["Kiadana"] = "Mount Kiadana-Rah",
        ["Kiadana Rah"] = "Mount Kiadana-Rah",
        ["Mount Kiadana Rah"] = "Mount Kiadana-Rah",
        ["Ashes"] = "The Ashes of NoWhere",
        ["Arendyl"] = "PlainsOfArendyl",
        ["Despair"] = "SeaOfDespair",
        ["Sea of Despair"] = "SeaOfDespair",
        ["Deep"] = "Waters of the Deep",
        ["Ruins of the Deep"] = "Waters of the Deep",
        ["WotDeep"] = "Waters of the Deep",
        ["Pine Forest"] = "PineForest",
        ["Aryth"] = "ArythOcean",
        ["Aryth Ocean"] = "ArythOcean",
        ["River"] = "The River Nanthor",
        ["Island"] = "The Forgotten Island",
        ["Forgotten"] = "The Forgotten Island",
        ["Shadow"] = "Shadow Grove",
        ["Shadow Grove"] = "Shadow Grove",
        ["Grove"] = "TheGrove",
        ["Swamp"] = "WhistlewoodSwamp",
        ["Spiderhaunt"] = "SpiderHauntwoods",
        ["Virgin"] = "A Virgin Forest",
        ["Wasteland"] = "FrigidWastelands",
        ["Frigid"] = "FrigidWastelands",
        ["Zakiim"] = "Mines of Zakiim",
        ["Spawning"] = "Glauruk Spawning Ground",
        ["Dranettie"] = "DranetyWoods",
        ["Maltrakis"] = "Village of Maltrakis",
        ["Village of Maltrakis"] = "Village of Maltrakis",
        ["Whistlewood"] = "WhistlewoodSwamp",
        ["Emerald"] = "Ancient Emerald Forest",
        ["Ancient Emerald Forest"] = "Ancient Emerald Forest",
        ["Blackwater"] = "Blackwater Swamp",
        ["Halfling"] = "The Halfling Lands",
        ["Halfling Lands"] = "The Halfling Lands",
        ["Sanctum"] = "Sanctum",
        ["Eil Shaeria"] = "Eil Shaeria",
        ["Tower of High Sorcery"] = "Tower of High Sorcery",
        ["Outpost"] = "Goblin Outpost",
        ["Goblin Outpost"] = "Goblin Outpost",
        ["Northern Trail"] = "A Northern Trail",
        ["Udgaardian Plains"] = "PlainsOfUdgaard",
        ["Plains of Udgaard"] = "PlainsOfUdgaard",
        ["Plains of Arendyl"] = "PlainsOfArendyl",
        ["Vale of Arendyl"] = "TheValeofArendyl",
        ["Abandoned Mines"] = "AbandonedMines",
        ["Abandoned Siege Camp"] = "Abandoned Siege Encampment",
        ["Siege Camp"] = "Abandoned Siege Encampment",
        ["Sewers"] = "Sewers",
        ["SM"] = "SeantrynSewers",
        ["Warrens"] = "Kobold Warrens",
        ["Kobold"] = "Kobold Warrens",
        ["Slave Mines of Sitran"] = "SlaveMinesOfSitran",
        ["Sitran"] = "SlaveMinesOfSitran",
        ["Redhorn"] = "The Redhorn Mountains",
        ["Trinil"] = "Ayr'Trinil",
        ["Ayr'Trinil"] = "Ayr'Trinil",
        ["Desert"] = "DesertOfAraile",
        ["Desert of Araile"] = "DesertOfAraile",
        ["Araile"] = "DesertOfAraile",
        ["Sorrow"] = "SandsOfSorrow",
        ["Sands of Sorrow"] = "SandsOfSorrow",
        ["Tahril"] = "Tahril",
        ["Pyramid"] = "Pyramid of Azhan",
        ["Azhan"] = "Pyramid of Azhan",
        ["Steppes"] = "The Oryx Steppes",
        ["Oryx"] = "The Oryx Steppes",
        ["Crystal"] = "CrystalIsland",
        ["Crystal Is"] = "CrystalIsland",
        ["Coral"] = "CoralHead",
        ["Coral Head"] = "CoralHead",
        ["CoralPalace"] = "CoralPalace",
        ["ShipGraveyard"] = "ShipGraveyard",
        ["Felar"] = "Blackclaw",
        ["Encampment"] = "Felar Encampment",
        ["Felar Encampment"] = "Felar Encampment",
        ["Harbor"] = "SeantrynHarbor",
        ["Seantryn Harbor"] = "SeantrynHarbor",
        ["Seantryn"] = "SeantrynModan",
        ["Wastes of Nonviel"] = "WastesOfNonviel",
        ["Nonviel"] = "WastesOfNonviel",
        ["Xvart Lair"] = "XvartLair",
        ["Xvart"] = "XvartLair",
        ["Jade Mountains"] = "JadeMountains",
        ["Coastal Plains"] = "The Coastal Plains",
        ["Coastal"] = "The Coastal Plains",
        ["Lallenyha"] = "VillageOfLallenyha",
        ["Village of Lallenyha"] = "VillageOfLallenyha",
        ["Lumberyard"] = "Lumberyard",
        ["Rocky"] = "The Rocky Paths",
        ["Loch Terradian"] = "Loch Terradian",
        ["Terradian"] = "Loch Terradian",
        ["VioletWoodland"] = "VioletWoodland",
        ["Violet Woodland"] = "VioletWoodland",
        ["Aldevari"] = "Aldevari",
        ["Wetlands"] = "Coastal Wetlands",
        ["Coastal Wetlands"] = "Coastal Wetlands",
        ["Caravans"] = "Caravans",
        ["Aturi Timberlands"] = "Aturi Timberlands",
        ["Timberlands"] = "Aturi Timberlands",
        ["Keep of Ceawlin"] = "CeawlinKeep",
        ["Ceawlin"] = "CeawlinKeep",
        ["Basilica"] = "Basilica",
        ["Dra'Melkhur"] = "Dra'Melkhur",
        ["Dark Wood"] = "DarkWoods",
        ["Dark"] = "DarkWoods",
        ["Wood"] = "DarkWoods",
        ["Balator"] = "Balator",
        ["FieldsOfBalator"] = "FieldsOfBalator",
        ["Fields of Balator"] = "FieldsOfBalator",
        ["Bramblefield"] = "Bramblefield",
        ["Road"] = "",
        ["Eastern"] = "EasternRoad",
        ["Outskirts"] = "GaladonOutskirts",
        ["Onyx"] = "OnyxTower",
        ["Silverwood"] = "Silverwood",
        ["KoR"] = "",
        ["Grinning"] = "Grinning Skull Village",
        ["Skull"] = "Grinning Skull Village",
        ["Grinning Skull"] = "Grinning Skull Village",
        ["Lab"] = "Kteng's Laboratory",
        ["Kteng's"] = "Kteng's Laboratory",
        ["Valley of Veran"] = "Valley of Veran",
        ["Veran"] = "Valley of Veran",
        ["Mansion of Twilight"] = "Mansion of Twilight",
        ["Twilight"] = "Mansion of Twilight",
        ["V'dramir's Cove"] = "V'Dramir's Cove",
        ["Cove"] = "V'Dramir's Cove",
        ["Barovia"] = "CastleofBarovia",
        ["Keep of Barovia"] = "CastleofBarovia",
        ["Village of Barovia"] = "OutlyingVillages",
        ["Evergrove Outpost"] = "Evergrove Outpost",
        ["Evergrove"] = "Evergrove Outpost",
        ["Outlying Villages"] = "OutlyingVillages",
        ["Outlying"] = "OutlyingVillages",
        ["Villages"] = "OutlyingVillages",
        ["Arkham"] = "Arkham",
        ["Dragon Tower"] = "DragonTower",
        ["Dragon"] = "DragonTower",
        ["Port"] = "SeantrynHarbor",
        ["Elemental"] = "ElementalTemple",
        ["Elemental Temple"] = "ElementalTemple",
        ["Temple"] = "ElementalTemple",
        ["Glauruk"] = "Glauruk Spawning Ground",
        ["Fean"] = "Feanwyyn",
        ["Feanwyyn"] = "Feanwyyn",
        ["Weald"] = "Feanwyyn Weald",
        ["Feanwyyn Weald"] = "Feanwyyn Weald",
        ["High Sorcery"] = "Tower of High Sorcery",
        ["Tower of"] = "Tower of High Sorcery",
        ["NoWhere"] = "The Ashes of NoWhere",
        ["Ashes of NoWhere"] = "The Ashes of NoWhere",
        ["Dagdan"] = "Dagdan",
        ["Sanctum"] = "",
        ["Akan"] = "Akan",
        ["Castle"] = "CastleAkan",
        ["Castle of Akan"] = "CastleAkan",
        ["Mortorn"] = "Mortorn",
        ["Voralia's"] = "Voralia'sTears",
        ["Tears"] = "Voralia'sTears",
        ["Embrace"] = "StonesEmbrace",
        ["Stones Embrace"] = "StonesEmbrace",
        ["Stones"] = "StonesEmbrace",
        ["Imperial"] = "ImperialLands",
        ["Imperial Lands"] = "ImperialLands",
        ["Prosimy"] = "Forest of Prosimy",
        ["Forest of Prosimy"] = "Forest of Prosimy",
        ["Kteng's Lab"] = "Kteng's Laboratory",
        ["Teth"] = "TethAzeleth",
        ["TethAzeleth"] = "TethAzeleth",
        ["Cragstone"] = "Cragstone",
        ["Underdark"] = "Underdark",
        ["UDS"] = "UnderdarkSea",
        ["Ruins of Maethien"] = "RuinsOfMaethien",
        ["Maethien"] = "Maethien",
        ["Darsylon"] = "Darsylon",
        ["InTheAir"] = "InTheAir",
        ["Mount Calandaryl"] = "MountCalandaryl",
        ["Calandaryl"] = "MountCalandaryl",
        ["Hillcrest"] = "Hillcrest",
        ["Wastes"] = "WastesOfNonviel",
        ["High"] = "HighLords",
        ["Gamepath"] = "A Narrow Gamepath",
        ["TrogCav"] = "Troglodyte Caverns",
        ["Bandit"] = "The Bandit Stronghold",
        ["Bandit Stronghold"] = "The Bandit Stronghold",
        ["Stronghold"] = "The Bandit Stronghold",
        ["Aratouldain"] = "Aratouldain",
        ["Velkyn"] = "Velkyn Oloth",
        ["Velkyn Oloth"] = "Velkyn Oloth",
        ["Oloth"] = "Velkyn Oloth",
        ["Yhorian"] = "Yhorian",
        ["RobertDunn"] = "RobertDunn",
        ["Zendrac"] = "Zendrac",
        ["DurNominator"] = "DurNominator",
        ["Mausoleum"] = "Mausoleum",
        ["Mauso"] = "Mausoleum",
        ["-leum"] = "Mausoleum",
        ["Azreth"] = "AzrethForest",
        ["Azreth Forest"] = "AzrethForest",
        ["Nanthor"] = "The River Nanthor",
        ["Shepherds"] = "Shepherds",
        ["Pass"] = "The Pass",
        ["High Pass"] = "The High Pass",
        ["Shaeria"] = "Eil Shaeria",
        ["Dhumlar"] = "Dhumlar",
        ["Qhabiszan"] = "Qhabiszan",
        ["Ko"] = "",
        ["Hamsah"] = "Hamsah Mu'tazz",
        ["Ysigrath"] = "Ysigrath",
        ["Enpolad's"] = "Enpolad's Game Garden",
        ["Game"] = "Enpolad's Game Garden",
        ["Enpolad's Game Garden"] = "Enpolad's Game Garden",
        ["Delar"] = "DelarTol",
        ["Tol"] = "DelarTol",
        ["Delar-Tol"] = "DelarTol",
        ["Aturi"] = "Aturi",
        ["Citadel"] = "The Citadel of Ostalagiah",
        ["Ostalagiah"] = "The Citadel of Ostalagiah",
        ["Past"] = "",
        ["Past Grove"] = "Shadow Grove",
        ["Present Grove"] = "Shadow Grove",
        ["Present"] = "Shadow Grove",
        ["Galadonian"] = "Galadonian Settlement",
        ["Settlement"] = "Galadonian Settlement",
        ["Wagon"] = "A Wagon-Marked Road",
        ["Hidden"] = "Hidden Forest",
        ["Hidden Forest"] = "Hidden Forest",
        ["Forest of"] = "Forest of NoWhere",
        ["NoWhere"] = "ForestOfNoWhere",
        ["Battlefield"] = "Battlefield",
        ["The Battlefield"] = "Battlefield",
        ["Saurian"] = "Saurian Village",
        ["Shadow Grove"] = "Shadow Grove",
    };

    foreach (var (word, title) in variants)
    {
        if (string.IsNullOrEmpty(title)) continue;
        if (mapping.ContainsKey(word)) continue;
        if (!mapText.Contains(word)) continue;

        var fileName = AreaPageGenerator.MakeFileName(title);
        var areaPagePath = Path.Combine(FindRepoRoot(), "src", "area", fileName);
        if (File.Exists(areaPagePath))
            mapping[word] = fileName;
    }

    // Also pick up "InTheAir" which is a single word in map
    // and Kteng's etc

    var outputPath = Path.Combine(FindRepoRoot(), "src", "map-links.json");
    var json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(outputPath, json);

    Console.WriteLine($"Generated {mapping.Count} map text -> area page links");
    Console.WriteLine($"Saved to {outputPath}");
}

void RunConsolidateAreaPages()
{
    var srcDir = Path.Combine(FindRepoRoot(), "src");
    var consolidator = new AreaPageConsolidator(srcDir);
    consolidator.Consolidate();
}

void RunGenerateAreaPages()
{
    var knowledgeDir = Path.Combine(dataDir, "knowledge");
    var wikiPath = Path.Combine(knowledgeDir, "qhcf-wiki-all.json");
    if (!File.Exists(wikiPath))
    {
        Console.WriteLine("qhcf-wiki-all.json not found. Run 'scrape-qhcf-all' first.");
        return;
    }

    // Load wiki pages
    var doc = JsonDocument.Parse(File.ReadAllText(wikiPath));
    var wikiPages = new Dictionary<string, JsonElement>();
    foreach (var prop in doc.RootElement.EnumerateObject())
        wikiPages[prop.Name] = prop.Value.Clone();

    // Load item locations
    var itemLocations = File.Exists(locationsOutputPath)
        ? JsonSerializer.Deserialize<Dictionary<string, ItemLocation>>(File.ReadAllText(locationsOutputPath)) ?? new()
        : new Dictionary<string, ItemLocation>();

    // Load items
    var items = File.Exists(itemsExportPath)
        ? JsonSerializer.Deserialize<List<ItemRecord>>(File.ReadAllText(itemsExportPath)) ?? new()
        : new List<ItemRecord>();

    // Load areas
    var areas = File.Exists(areasOutputPath)
        ? JsonSerializer.Deserialize<List<AreaInfo>>(File.ReadAllText(areasOutputPath)) ?? new()
        : new List<AreaInfo>();

    Console.WriteLine($"Generating pages from {wikiPages.Count} wiki pages, {items.Count} items, {itemLocations.Count} locations...");

    var generator = new AreaPageGenerator(wikiPages, itemLocations, items, areas);
    var outputDir = Path.Combine(FindRepoRoot(), "src", "area");
    var count = generator.GenerateAllPages(outputDir);

    Console.WriteLine($"Generated {count} area pages in {outputDir}");
}

async Task RunScrapePhorum()
{
    var forumsDir = Path.Combine(dataDir, "forums");
    var scraper = new PhorumScraper(forumsDir);
    await scraper.ScrapeAllBoardsAsync();
}

async Task RunScrapeDcBoard()
{
    var forumsDir = Path.Combine(dataDir, "forums");
    var scraper = new DcBoardScraper(forumsDir);
    await scraper.ScrapeAllForumsAsync();
}

async Task RunScrapeQhcfSite()
{
    var knowledgeDir = Path.Combine(dataDir, "knowledge");
    var scraper = new QhcfSiteScraper(knowledgeDir);
    await scraper.ScrapeAllAsync();
}

async Task RunScrapeAllSites()
{
    Console.WriteLine("\n=== Step 1: qhcf.net premium pages ===");
    await RunScrapeQhcfSite();

    Console.WriteLine("\n=== Step 2: qhcf.net phorum ===");
    await RunScrapePhorum();

    Console.WriteLine("\n=== Step 3: forums.carrionfields.com ===");
    await RunScrapeDcBoard();

    Console.WriteLine("\n=== All scraping complete ===");
}

void RunBuildTrainingData()
{
    var formatter = new TrainingFormatter(dataDir);
    formatter.Build();
}

async Task RunScrapeQhcfAll()
{
    var knowledgeDir = Path.Combine(dataDir, "knowledge");
    Directory.CreateDirectory(knowledgeDir);
    var scraper = new WikiScraper(knowledgeDir);

    var titlesCache = Path.Combine(knowledgeDir, "qhcf-all-titles.json");
    List<string> titles;

    if (File.Exists(titlesCache))
    {
        titles = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(titlesCache)) ?? new();
        Console.WriteLine($"Loaded {titles.Count} titles from cache: {titlesCache}");
    }
    else
    {
        Console.WriteLine("Fetching list of all wiki pages...");
        titles = await scraper.GetAllQhcfPageTitlesAsync();
        File.WriteAllText(titlesCache, JsonSerializer.Serialize(titles, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved title list to {titlesCache}");
    }

    // Resume support: load existing results if present
    var outputPath = Path.Combine(knowledgeDir, "qhcf-wiki-all.json");
    var existing = new Dictionary<string, object>();
    if (File.Exists(outputPath))
    {
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
                existing[prop.Name] = prop.Value.Clone();
            Console.WriteLine($"Resuming: {existing.Count} pages already scraped");
        }
        catch { }
    }

    var toScrape = titles.Where(t => !existing.ContainsKey(t)).ToList();
    Console.WriteLine($"Scraping {toScrape.Count} remaining pages (of {titles.Count} total)...");

    var lastSave = DateTime.UtcNow;
    var newResults = await scraper.ScrapeAllQhcfPagesAsync(toScrape, (n, total, status) =>
    {
        if (n % 20 == 0 || n == total)
            Console.WriteLine($"  [{n}/{total}] {status}");

        // Periodic save every 2 minutes so we don't lose progress
        if ((DateTime.UtcNow - lastSave).TotalMinutes > 2)
        {
            // Can't save here without capturing more state, leave for end
            lastSave = DateTime.UtcNow;
        }
    });

    // Merge results
    foreach (var (k, v) in newResults)
        existing[k] = v;

    var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(outputPath, json);

    Console.WriteLine($"\nTotal: {existing.Count}/{titles.Count} pages saved to {outputPath}");
}

async Task RunScrapeQhcfAreas()
{
    var linksFile = Path.Combine(repoRoot, "docs", "Links to get data from.txt");
    if (!File.Exists(linksFile))
    {
        Console.WriteLine($"Links file not found at {linksFile}");
        return;
    }

    var pages = seedParser.ParseLinksForUrls(linksFile);
    Console.WriteLine($"Starting scrape of {pages.Count} wiki pages from wiki.qhcf.net...");

    var knowledgeDir = Path.Combine(dataDir, "knowledge");
    Directory.CreateDirectory(knowledgeDir);
    var scraper = new WikiScraper(knowledgeDir);

    var results = await scraper.ScrapeQhcfAreasAsync(pages);

    var outputPath = Path.Combine(knowledgeDir, "qhcf-wiki-areas.json");
    var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(outputPath, json);

    Console.WriteLine($"\nScraped {results.Count}/{pages.Count} pages successfully");
    Console.WriteLine($"Saved to {outputPath}");
}

async Task RunUploadKnowledge()
{
    var knowledgeDir = Path.Combine(dataDir, "knowledge");
    var containerUri = new Uri($"{blobEndpoint}cfknowledge?{sasToken}");
    var containerClient = new Azure.Storage.Blobs.BlobContainerClient(containerUri);
    await containerClient.CreateIfNotExistsAsync();

    // Upload knowledge files
    var filesToUpload = new[] { "areas.json", "item-locations.json", "game-info.json", "area-wiki-data.json", "world-map.json", "qhcf-wiki-areas.json", "qhcf-wiki-all.json", "qhcf-all-titles.json" };

    // Also upload the map
    var mapFile = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "docs", "MapOfThera.txt");
    if (!File.Exists(mapFile))
        mapFile = Path.Combine(FindRepoRoot(), "docs", "MapOfThera.txt");

    if (File.Exists(mapFile))
    {
        var blob = containerClient.GetBlobClient("MapOfThera.txt");
        await blob.UploadAsync(mapFile, overwrite: true);
        Console.WriteLine("Uploaded MapOfThera.txt");
    }

    foreach (var fileName in filesToUpload)
    {
        var filePath = Path.Combine(knowledgeDir, fileName);
        if (!File.Exists(filePath))
        {
            // Also check if it's in the data root (item-locations.json)
            filePath = Path.Combine(dataDir, fileName);
        }

        if (File.Exists(filePath))
        {
            var blob = containerClient.GetBlobClient(fileName);
            await blob.UploadAsync(filePath, overwrite: true);
            Console.WriteLine($"Uploaded {fileName}");
        }
        else
        {
            Console.WriteLine($"Skipped {fileName} (not found)");
        }
    }

    Console.WriteLine("Knowledge files uploaded to cfknowledge container");
}

void RunBuildMap()
{
    if (!Directory.Exists(logsDir))
    {
        Console.WriteLine($"No logs directory found at {logsDir}. Run 'download-logs' first.");
        return;
    }

    var builder = new WorldMapBuilder();
    var map = builder.BuildFromLogs(logsDir);

    var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(worldMapPath, json);
    Console.WriteLine($"Saved world map to {worldMapPath}");
}

void RunFindPaths()
{
    if (!File.Exists(worldMapPath))
    {
        Console.WriteLine("No world-map.json found. Run 'build-map' first.");
        return;
    }
    if (!File.Exists(locationsOutputPath))
    {
        Console.WriteLine("No item-locations.json found. Run 'extract-locations' first.");
        return;
    }

    var map = JsonSerializer.Deserialize<WorldMap>(File.ReadAllText(worldMapPath))!;
    var locations = JsonSerializer.Deserialize<Dictionary<string, ItemLocation>>(
        File.ReadAllText(locationsOutputPath))!;

    var pathFinder = new PathFinder(map);
    var crossroads = pathFinder.FindCrossroadsRoom();

    if (crossroads == null)
    {
        Console.WriteLine("Could not find crossroads room in world map.");
        return;
    }

    var bfsResults = pathFinder.BfsFromRoom(crossroads);
    pathFinder.CalculateItemPaths(locations, bfsResults);

    // Save updated locations with paths
    var json = JsonSerializer.Serialize(locations, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(locationsOutputPath, json);
    Console.WriteLine($"Updated {locationsOutputPath} with paths");
}

void RunStats()
{
    if (File.Exists(itemsExportPath))
    {
        var items = JsonSerializer.Deserialize<List<ItemRecord>>(File.ReadAllText(itemsExportPath)) ?? new();
        var withArea = items.Count(i => !string.IsNullOrEmpty(i.Area));
        Console.WriteLine($"Items: {items.Count}, With Area: {withArea} ({100.0 * withArea / items.Count:F1}%)");
        Console.WriteLine($"Item types: {string.Join(", ", items.GroupBy(i => i.Group).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}({g.Count()})"))}");
    }

    if (File.Exists(locationsOutputPath))
    {
        var locations = JsonSerializer.Deserialize<Dictionary<string, ItemLocation>>(File.ReadAllText(locationsOutputPath)) ?? new();
        var withArea = locations.Values.Count(l => !string.IsNullOrEmpty(l.BestGuessArea));
        var withMob = locations.Values.Count(l => !string.IsNullOrEmpty(l.BestGuessMob));
        var withContainer = locations.Values.Count(l => !string.IsNullOrEmpty(l.BestGuessContainer));
        var withPath = locations.Values.Count(l => !string.IsNullOrEmpty(l.PathFromCrossroads));
        var byConfidence = locations.Values
            .SelectMany(l => l.Locations)
            .GroupBy(l => l.Confidence)
            .Select(g => $"{g.Key}:{g.Count()}");
        Console.WriteLine($"Locations: {locations.Count} items, {withArea} with area, {withMob} with mob, {withContainer} with container, {withPath} with path");
        Console.WriteLine($"Confidence: {string.Join(", ", byConfidence)}");
    }

    if (File.Exists(worldMapPath))
    {
        var map = JsonSerializer.Deserialize<WorldMap>(File.ReadAllText(worldMapPath));
        if (map != null)
            Console.WriteLine($"World map: {map.Rooms.Count} rooms, {map.TotalEdges} edges");
    }

    if (Directory.Exists(logsDir))
    {
        var logCount = Directory.GetFiles(logsDir, "*.log").Length +
                       Directory.GetFiles(logsDir, "*.txt").Length +
                       Directory.GetFiles(logsDir, "*.TXT").Length;
        Console.WriteLine($"Log files: {logCount}");
    }
}

string FindRepoRoot()
{
    var dir = Directory.GetCurrentDirectory();
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir, ".git")))
            return dir;
        dir = Directory.GetParent(dir)?.FullName;
    }
    return Directory.GetCurrentDirectory();
}
