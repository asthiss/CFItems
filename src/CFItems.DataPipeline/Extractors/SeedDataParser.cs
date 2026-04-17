using System.Text.RegularExpressions;
using CFItems.DataPipeline.Models;

namespace CFItems.DataPipeline.Extractors;

public class SeedDataParser
{
    /// <summary>
    /// Parse docs/areas.txt into structured area info.
    /// Format: | 40 - 51 | Dhuuston - Subterranean Spire |
    /// </summary>
    public List<AreaInfo> ParseAreasFile(string filePath)
    {
        var areas = new List<AreaInfo>();
        var lines = File.ReadAllLines(filePath);
        var regex = new Regex(@"\|\s*(.+?)\s*\|\s*(.+?)\s*-\s*(.+?)\s*\|?$");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("areas") || line.StartsWith("["))
                continue;

            var match = regex.Match(line);
            if (match.Success)
            {
                var levelRange = match.Groups[1].Value.Trim();
                var builder = match.Groups[2].Value.Trim();
                var areaName = match.Groups[3].Value.Trim();

                areas.Add(new AreaInfo
                {
                    Name = areaName,
                    LevelRange = levelRange,
                    Builder = builder
                });
            }
        }

        Console.WriteLine($"Parsed {areas.Count} areas from {filePath}");
        return areas;
    }

    /// <summary>
    /// Parse the Links HTML file for area categorization and "no share" list.
    /// </summary>
    public (Dictionary<string, string> areaCategories, List<string> restrictedAreas) ParseLinksFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var areaCategories = new Dictionary<string, string>();
        var restrictedAreas = new List<string>();

        // Extract category sections
        var boldRegex = new Regex(@"<b>(.+?)</b>");
        var linkRegex = new Regex(@"title=""(.+?)""");
        var liTextRegex = new Regex(@"<li>\s*(.+?)\s*</li>");

        var currentCategory = "";
        var inNoShare = false;

        foreach (var line in content.Split('\n'))
        {
            var boldMatch = boldRegex.Match(line);
            if (boldMatch.Success)
            {
                currentCategory = boldMatch.Groups[1].Value;
                inNoShare = currentCategory.Contains("No No") || currentCategory.Contains("not alowed");
            }

            if (inNoShare)
            {
                var liMatch = liTextRegex.Match(line);
                if (liMatch.Success)
                {
                    var areaName = liMatch.Groups[1].Value.Trim();
                    // Strip any remaining HTML
                    areaName = Regex.Replace(areaName, @"<[^>]+>", "").Trim();
                    if (!string.IsNullOrEmpty(areaName))
                        restrictedAreas.Add(areaName);
                }
                continue;
            }

            var linkMatch = linkRegex.Match(line);
            if (linkMatch.Success && !string.IsNullOrEmpty(currentCategory))
            {
                var areaName = linkMatch.Groups[1].Value;
                // Clean up area names
                areaName = areaName.Replace("Of", " Of ").Replace("  ", " ");
                if (!areaCategories.ContainsKey(areaName))
                    areaCategories[areaName] = currentCategory;
            }
        }

        Console.WriteLine($"Parsed {areaCategories.Count} area categories and {restrictedAreas.Count} restricted areas");
        return (areaCategories, restrictedAreas);
    }

    /// <summary>
    /// Extract all wiki page URL titles from the Links file.
    /// Returns list of (displayName, urlTitle, category) tuples.
    /// urlTitle is suitable for URL construction: wiki.qhcf.net/index.php?title={urlTitle}
    /// </summary>
    public List<(string DisplayName, string UrlTitle, string Category)> ParseLinksForUrls(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var results = new List<(string, string, string)>();
        var seen = new HashSet<string>();

        var boldRegex = new Regex(@"<b>(.+?)</b>");
        // Match: <a href="...?title=URLTITLE" title="URLTITLE">DisplayName</a>
        var anchorRegex = new Regex(@"<a\s+href=""[^""]*title=([^""&]+)[^""]*""\s+title=""[^""]+""[^>]*>([^<]+)</a>");

        var currentCategory = "";
        var inNoShare = false;

        foreach (var line in content.Split('\n'))
        {
            var boldMatch = boldRegex.Match(line);
            if (boldMatch.Success)
            {
                currentCategory = boldMatch.Groups[1].Value;
                inNoShare = currentCategory.Contains("No No") || currentCategory.Contains("not alowed");
            }
            if (inNoShare) continue;

            var matches = anchorRegex.Matches(line);
            foreach (Match m in matches)
            {
                var urlTitle = m.Groups[1].Value.Trim();
                var displayName = m.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(urlTitle)) continue;
                if (!seen.Add(urlTitle)) continue;
                results.Add((displayName, urlTitle, currentCategory));
            }
        }

        Console.WriteLine($"Extracted {results.Count} wiki page URLs from Links file");
        return results;
    }

    /// <summary>
    /// Parse EqToLookForGoodieWarrior.txt for item-to-area seed mappings.
    /// Lines like: "a bloodstone ring  L52, 4 5 6 4, dam 3, ..., Green Dragon, Feanwyyn Weald"
    /// </summary>
    public List<SeedItemMapping> ParseEquipmentFile(string filePath)
    {
        var mappings = new List<SeedItemMapping>();
        var lines = File.ReadAllLines(filePath);
        var currentSlot = "";

        // Known area names to match against (from areas.txt)
        var knownAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Feanwyyn Weald", "Coral Palace", "Dragon Tower", "Slave Mines of Sitran",
            "Mount Kiadana-Rah", "Seantryn Modan", "Seantryn Palace", "Pyramids of Azhan",
            "Bramblefield", "Dranettie", "Dranettie Wood", "Basilica", "Frigid Wastelands",
            "Frigid Wasteland", "Kuo-Toa Lair", "Kiadana-rah", "Battle Field", "Battlefield",
            "Tower of Trothon", "Mortorn", "Lost Elven Vaults", "Arboria", "Manor",
            "Ruins of Tcar", "Enpolad's Game Garden", "Blingdenstone", "Teth Azeleth",
            "ElementalTemple", "Elemental Temple", "Chessmaster Tower",
            "Village of Barovia", "Keep of Barovia", "Whitecloaks"
        };

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---"))
                continue;

            // Detect slot headers
            if (line.TrimStart().StartsWith("Finger:") || line.TrimStart().StartsWith("Neck:") ||
                line.TrimStart().StartsWith("Body:") || line.TrimStart().StartsWith("Head:") ||
                line.TrimStart().StartsWith("Face:") || line.TrimStart().StartsWith("Legs:") ||
                line.TrimStart().StartsWith("Feet:") || line.TrimStart().StartsWith("Hands:") ||
                line.TrimStart().StartsWith("Arms:") || line.TrimStart().StartsWith("About:") ||
                line.TrimStart().StartsWith("Waist:") || line.TrimStart().StartsWith("Wrist:") ||
                line.TrimStart().StartsWith("Shield:") || line.TrimStart().StartsWith("Wield:") ||
                line.TrimStart().StartsWith("Hold:"))
            {
                currentSlot = line.TrimStart().Split(':')[0].Trim();
            }

            // Extract item name (starts with 'a ', 'an ', 'the ', or specific patterns)
            var trimmed = line.Trim();
            if (trimmed.StartsWith(currentSlot + ":"))
                trimmed = trimmed.Substring(currentSlot.Length + 1).Trim();

            // Look for tab-separated parts
            var parts = trimmed.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) continue;

            var itemName = parts[0].Trim();
            if (!itemName.StartsWith("a ") && !itemName.StartsWith("an ") &&
                !itemName.StartsWith("the ") && !itemName.StartsWith("some ") &&
                !itemName.StartsWith("boots") && !itemName.StartsWith("gauntlets") &&
                !itemName.StartsWith("gloves") && !itemName.StartsWith("vambraces"))
                continue;

            // Look for area references in the rest of the line
            var restOfLine = string.Join(" ", parts.Skip(1));
            if (string.IsNullOrEmpty(restOfLine)) continue;

            string? foundArea = null;
            string? foundMob = null;

            // Check for known area names in the line
            foreach (var area in knownAreas)
            {
                if (restOfLine.Contains(area, StringComparison.OrdinalIgnoreCase))
                {
                    foundArea = area;
                    break;
                }
            }

            // Also check for [[AreaName]] wiki-style references
            var wikiRef = Regex.Match(restOfLine, @"\[\[(.+?)\]\]");
            if (wikiRef.Success)
                foundArea = wikiRef.Groups[1].Value;

            // Try to extract mob name (comes before area, separated by comma)
            if (foundArea != null)
            {
                var areaIdx = restOfLine.IndexOf(foundArea, StringComparison.OrdinalIgnoreCase);
                var beforeArea = restOfLine.Substring(0, areaIdx).TrimEnd(',', ' ');
                // Mob name is usually the last comma-separated segment before area
                var segments = beforeArea.Split(',');
                var lastSegment = segments.Last().Trim();
                // Filter out stats-like segments
                if (lastSegment.Length > 3 && !lastSegment.StartsWith("w") &&
                    !Regex.IsMatch(lastSegment, @"^\d") && !lastSegment.Contains("special"))
                {
                    foundMob = lastSegment;
                }
            }

            if (foundArea != null)
            {
                mappings.Add(new SeedItemMapping
                {
                    ItemName = itemName,
                    AreaName = foundArea,
                    MobName = foundMob,
                    Slot = currentSlot
                });
            }
        }

        Console.WriteLine($"Parsed {mappings.Count} item-to-area seed mappings from {filePath}");
        return mappings;
    }
}
