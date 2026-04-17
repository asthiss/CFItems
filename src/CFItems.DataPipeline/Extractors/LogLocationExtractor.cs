using System.Text.RegularExpressions;
using CFItems.DataPipeline.Models;

namespace CFItems.DataPipeline.Extractors;

public class LogLocationExtractor
{
    private static readonly string ItemDelimiter = "----------------------------------------";
    private static readonly string ItemDelimiterLineTwo = "can be referred to as";
    private static readonly Regex ExitsRegex = new(@"\[Exits:.*\]", RegexOptions.Compiled);
    private static readonly Regex WhereRegex = new(@"^\(PK\)\s+\w+\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex PromptRegex = new(@"^(civilized|wilderness|PROTECTED)\s+\d+/\d+\|hp", RegexOptions.Compiled);
    private static readonly Regex GetFromCorpseRegex = new(@"You get (.+) from the corpse of (.+)\.", RegexOptions.Compiled);
    private static readonly Regex GetFromContainerRegex = new(@"You get (.+) from (.+)\.", RegexOptions.Compiled);
    private static readonly Regex MobDeadRegex = new(@"^(.+) is DEAD!!", RegexOptions.Compiled);

    private readonly HashSet<string> _knownAreaNames;
    private readonly Dictionary<string, string> _roomToAreaMap;

    public LogLocationExtractor(List<AreaInfo> areas, List<SeedItemMapping> seedMappings)
    {
        _knownAreaNames = new HashSet<string>(
            areas.Select(a => a.Name),
            StringComparer.OrdinalIgnoreCase);

        _roomToAreaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Process all log files in a directory (both .log and .txt).
    /// </summary>
    public Dictionary<string, ItemLocation> ProcessAllLogs(string logDirectory)
    {
        var allLocations = new Dictionary<string, ItemLocation>(StringComparer.OrdinalIgnoreCase);

        var logFiles = Directory.GetFiles(logDirectory, "*.log")
            .Concat(Directory.GetFiles(logDirectory, "*.txt"))
            .Concat(Directory.GetFiles(logDirectory, "*.TXT"))
            .Distinct()
            .ToArray();

        Console.WriteLine($"Processing {logFiles.Length} log files for item locations...");

        var processed = 0;
        foreach (var logFile in logFiles)
        {
            try
            {
                var locations = ProcessLogFile(logFile);
                MergeLocations(allLocations, locations);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {Path.GetFileName(logFile)}: {ex.Message}");
            }

            processed++;
            if (processed % 100 == 0)
                Console.WriteLine($"Processed {processed}/{logFiles.Length} log files...");
        }

        foreach (var item in allLocations.Values)
            DetermineBestGuess(item);

        Console.WriteLine($"Found locations for {allLocations.Count} unique items");
        return allLocations;
    }

    public List<(string itemName, LocationEntry location)> ProcessLogFile(string filePath)
    {
        var results = new List<(string, LocationEntry)>();
        var lines = File.ReadAllLines(filePath);
        var fileName = Path.GetFileName(filePath);

        for (var i = 1; i < lines.Length - 1; i++)
        {
            if (lines[i].StartsWith(ItemDelimiter) &&
                i + 1 < lines.Length &&
                lines[i + 1].Contains(ItemDelimiterLineTwo) &&
                (i == 0 || !lines[i - 1].Contains("lore")))
            {
                var itemName = ExtractItemName(lines[i + 1]);
                if (string.IsNullOrEmpty(itemName))
                    continue;

                var location = FindRoomContext(lines, i, fileName);
                if (location == null)
                    location = new LocationEntry { SourceLog = fileName, LineNumber = i };

                // Extract mob/container source
                FindItemSource(lines, i, itemName, location);

                if (location.RoomName != null || location.AreaName != null ||
                    location.MobName != null || location.ContainerName != null)
                {
                    results.Add((itemName, location));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Search near an identify block for "You get X from Y" to find mob/container source.
    /// </summary>
    private void FindItemSource(string[] lines, int identifyLineIndex, string itemName, LocationEntry location)
    {
        // Normalize item name for matching
        var itemLower = itemName.ToLower();
        // Also try partial match: first few significant words
        var itemWords = itemLower.Split(' ').Where(w => w.Length > 2).Take(3).ToArray();

        // Search backward (player looted, then identified)
        var searchStart = Math.Max(0, identifyLineIndex - 150);
        for (var i = identifyLineIndex - 1; i >= searchStart; i--)
        {
            var line = lines[i].Trim();
            // Strip prompt prefix if present
            if (PromptRegex.IsMatch(line))
            {
                var promptEnd = line.IndexOf("You get");
                if (promptEnd >= 0)
                    line = line.Substring(promptEnd);
            }

            if (!line.StartsWith("You get "))
                continue;

            // Check if this "You get" line references our item
            if (!LineMatchesItem(line, itemLower, itemWords))
                continue;

            // Match "You get X from the corpse of Y"
            var corpseMatch = GetFromCorpseRegex.Match(line);
            if (corpseMatch.Success)
            {
                location.MobName = corpseMatch.Groups[2].Value.Trim();
                location.Confidence = location.Confidence == "low" ? "medium" : location.Confidence;
                return;
            }

            // Match "You get X from Y" (container)
            var containerMatch = GetFromContainerRegex.Match(line);
            if (containerMatch.Success)
            {
                var container = containerMatch.Groups[2].Value.Trim();
                // Filter out inventory containers (player bags)
                if (!IsPlayerContainer(container))
                {
                    location.ContainerName = container;
                    return;
                }
            }

            // Plain "You get X." - look nearby for "is DEAD!!" to find mob
            FindNearbyMobKill(lines, i, location);
            return;
        }

        // Search forward too (player identified, then looted)
        var searchEnd = Math.Min(lines.Length, identifyLineIndex + 50);
        for (var i = identifyLineIndex + 1; i < searchEnd; i++)
        {
            var line = lines[i].Trim();
            if (PromptRegex.IsMatch(line))
            {
                var promptEnd = line.IndexOf("You get");
                if (promptEnd >= 0) line = line.Substring(promptEnd);
            }

            if (!line.StartsWith("You get ")) continue;
            if (!LineMatchesItem(line, itemLower, itemWords)) continue;

            var corpseMatch = GetFromCorpseRegex.Match(line);
            if (corpseMatch.Success)
            {
                location.MobName = corpseMatch.Groups[2].Value.Trim();
                return;
            }

            var containerMatch = GetFromContainerRegex.Match(line);
            if (containerMatch.Success)
            {
                var container = containerMatch.Groups[2].Value.Trim();
                if (!IsPlayerContainer(container))
                {
                    location.ContainerName = container;
                    return;
                }
            }

            FindNearbyMobKill(lines, i, location);
            return;
        }

        // Last resort: look for any "is DEAD!!" within 100 lines before identify
        for (var i = identifyLineIndex - 1; i >= searchStart; i--)
        {
            var deadMatch = MobDeadRegex.Match(lines[i].Trim());
            if (deadMatch.Success)
            {
                location.MobName = deadMatch.Groups[1].Value.Trim();
                return;
            }
        }
    }

    private bool LineMatchesItem(string line, string itemLower, string[] itemWords)
    {
        var lineLower = line.ToLower();
        // Exact name match
        if (lineLower.Contains(itemLower)) return true;
        // Partial match: at least 2 significant words from item name
        if (itemWords.Length >= 2)
            return itemWords.Count(w => lineLower.Contains(w)) >= Math.Min(2, itemWords.Length);
        return itemWords.Length == 1 && lineLower.Contains(itemWords[0]);
    }

    private void FindNearbyMobKill(string[] lines, int getLineIndex, LocationEntry location)
    {
        // Search backward from "You get" for "is DEAD!!"
        for (var j = getLineIndex - 1; j >= Math.Max(0, getLineIndex - 30); j--)
        {
            var deadMatch = MobDeadRegex.Match(lines[j].Trim());
            if (deadMatch.Success)
            {
                location.MobName = deadMatch.Groups[1].Value.Trim();
                return;
            }
        }
    }

    private bool IsPlayerContainer(string container)
    {
        var lower = container.ToLower();
        return lower.Contains("girdle of endless") || lower.Contains("bag") ||
               lower.Contains("sack") || lower.Contains("backpack") ||
               lower.Contains("quiver") || lower.Contains("pouch");
    }

    private string? ExtractItemName(string line)
    {
        var parts = line.Split(" can be referred to as ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) return null;
        var name = parts[0].Trim();
        if (name.Contains(", "))
            name = name.Split(',')[1].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private LocationEntry? FindRoomContext(string[] lines, int identifyLineIndex, string fileName)
    {
        string? roomName = null;
        string? areaName = null;
        var confidence = "low";

        var searchStart = Math.Max(0, identifyLineIndex - 80);

        // Strategy 1: Look for [Exits:] pattern
        for (var i = identifyLineIndex - 1; i >= searchStart; i--)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || PromptRegex.IsMatch(line))
                continue;

            if (ExitsRegex.IsMatch(line))
            {
                roomName = FindRoomName(lines, i);
                if (roomName != null)
                {
                    confidence = "medium";
                    break;
                }
            }
        }

        // Strategy 2: 'where' command
        for (var i = identifyLineIndex - 1; i >= searchStart; i--)
        {
            var line = lines[i].Trim();
            var whereMatch = WhereRegex.Match(line);
            if (whereMatch.Success)
            {
                roomName = whereMatch.Groups[1].Value.Trim();
                confidence = "high";
                break;
            }

            if (line == "People near you:")
            {
                for (var j = i + 1; j < Math.Min(i + 20, lines.Length); j++)
                {
                    var wm = WhereRegex.Match(lines[j].Trim());
                    if (wm.Success)
                    {
                        roomName = wm.Groups[1].Value.Trim();
                        confidence = "high";
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(lines[j]) || PromptRegex.IsMatch(lines[j]))
                        break;
                }
                if (roomName != null) break;
            }
        }

        // Strategy 3: Infer area from room name
        if (roomName != null)
            areaName = InferAreaFromRoom(roomName);

        // Strategy 4: Area from context
        if (areaName == null)
            areaName = FindAreaFromContext(lines, identifyLineIndex);

        if (roomName == null && areaName == null)
            return null;

        return new LocationEntry
        {
            RoomName = roomName,
            AreaName = areaName,
            SourceLog = fileName,
            LineNumber = identifyLineIndex,
            Confidence = confidence
        };
    }

    private string? FindRoomName(string[] lines, int exitsLineIndex)
    {
        for (var i = exitsLineIndex - 1; i >= Math.Max(0, exitsLineIndex - 15); i--)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (PromptRegex.IsMatch(line)) continue;
            if (lines[i].StartsWith("  ") || lines[i].StartsWith("\t")) continue;
            if (line.StartsWith("(") || (line.StartsWith("A ") && line.Contains(" is here")) ||
                line.Contains(" lies here") || line.Contains(" stands here") ||
                line.Contains(" is sleeping here") || line.Contains(" is resting here") ||
                line.Contains(" flows from") || line.Contains(" sits here") ||
                line.Contains(" is hovering here") || line.Contains(" floats here"))
                continue;
            if (line.StartsWith("You ") || line.StartsWith("Your "))
                continue;

            if (line.Length > 2 && line.Length < 80 && !line.Contains("|"))
            {
                if (char.IsUpper(line[0]) || line[0] == '\'' || line[0] == '"')
                    return line;
            }
        }
        return null;
    }

    private string? InferAreaFromRoom(string roomName)
    {
        foreach (var area in _knownAreaNames)
        {
            if (roomName.Contains(area, StringComparison.OrdinalIgnoreCase))
                return area;
        }

        var areaKeywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Galadon", "Galadon" }, { "Seantryn", "Seantryn Modan" },
            { "Udgaard", "Udgaard" }, { "Balator", "Balator" },
            { "Hamsah", "The Seaport of Hamsah Mu'tazz" }, { "Darsylon", "Darsylon" },
            { "Arkham", "Arkham" }, { "Tir-Talath", "Tir-Talath" },
            { "Voralian", "Voralian City" }, { "Mortorn", "Mortorn" },
            { "Hillcrest", "Hillcrest" }, { "Akan", "Akan" },
            { "Aturi", "Aturi" }, { "Barovia", "The Village of Barovia" },
            { "Basilica", "The Basilica" }, { "Coral", "Coral Palace" },
            { "Underdark", "Underdark" }, { "Feanwyyn", "Feanwyyn Weald" },
            { "Dragon Tower", "Dragon Tower" }, { "Kiadana", "Mount Kiadana-Rah" },
            { "Emerald", "Ancient Emerald Forest" }, { "Eastern Road", "Eastern Road" },
            { "Crossroads", "Eastern Road" }, { "Ostalagiah", "The Citadel of Ostalagiah" },
            { "Silverwood", "Silverwood" }, { "Cragstone", "Upper Cragstone" },
            { "Consortium", "The Consortium" }, { "Evermoon", "Evermoon Hollow" },
            { "Blackclaw", "Blackclaw Village" }, { "Frigid", "The Frigid Wasteland" },
            { "Jade Mountain", "The Jade Mountains" }, { "Loch Terradian", "Loch Terradian" },
            { "Spiderhaunt", "Spiderhaunt Woods" }, { "Azreth", "Azreth Wood" },
            { "Enpolad", "Enpolad's Game Garden" }, { "Dranettie", "The Dranettie Wood" },
            { "Talshidar", "The Talshidar Caves" }, { "Whistlewood", "Whistlewood Swamp" },
            { "Blackwater", "Blackwater Swamp" }, { "Shadow Grove", "Shadow Grove" },
            { "Elemental Temple", "Elemental Temple" }, { "Dagdan", "Dagdan" },
            { "Grinning Skull", "Grinning Skull Village" }, { "Goblin Village", "Goblin Village" },
            { "Desert of Araile", "Desert of Araile" }, { "Sands of Sorrow", "Sands of Sorrow" },
            { "Pyramid", "The Pyramid of Azhan" }, { "Oryx", "The Oryx Steppes" },
            { "Crystal Island", "Crystal Island" }, { "Slave Mine", "The Slave Mines of Sitran" },
            { "Dark Wood", "The Dark Wood" }, { "Halfling", "The Halfling Lands" },
            { "Battlefield", "The Battlefield" }, { "High Lord", "High Lord's Keep" },
            { "Teth Azeleth", "Teth Azeleth" }, { "Kobold", "The Kobold Warrens" },
            { "Mausoleum", "Mausoleum" }, { "Prosimy", "Forest of Prosimy" },
        };

        foreach (var (keyword, area) in areaKeywords)
        {
            if (roomName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return area;
        }

        return _roomToAreaMap.TryGetValue(roomName, out var mapped) ? mapped : null;
    }

    private string? FindAreaFromContext(string[] lines, int contextIndex)
    {
        var searchStart = Math.Max(0, contextIndex - 100);
        for (var i = contextIndex; i >= searchStart; i--)
        {
            var line = lines[i].Trim();
            if (line.Contains("You have entered "))
            {
                var entered = line.Replace("You have entered ", "").TrimEnd('.');
                if (_knownAreaNames.Contains(entered))
                    return entered;
            }
        }
        return null;
    }

    private void MergeLocations(Dictionary<string, ItemLocation> all, List<(string itemName, LocationEntry location)> newEntries)
    {
        foreach (var (itemName, location) in newEntries)
        {
            if (!all.TryGetValue(itemName, out var existing))
            {
                existing = new ItemLocation { ItemName = itemName };
                all[itemName] = existing;
            }

            var isDuplicate = existing.Locations.Any(l =>
                l.RoomName == location.RoomName &&
                l.AreaName == location.AreaName &&
                l.MobName == location.MobName);

            if (!isDuplicate)
                existing.Locations.Add(location);
        }
    }

    private void DetermineBestGuess(ItemLocation item)
    {
        if (!item.Locations.Any()) return;

        var best = item.Locations
            .OrderByDescending(l => l.Confidence switch { "high" => 3, "medium" => 2, _ => 1 })
            .ThenByDescending(l => !string.IsNullOrEmpty(l.AreaName) ? 1 : 0)
            .First();

        item.BestGuessArea = best.AreaName;
        item.BestGuessRoom = best.RoomName;

        if (string.IsNullOrEmpty(item.BestGuessArea))
        {
            var withArea = item.Locations.FirstOrDefault(l => !string.IsNullOrEmpty(l.AreaName));
            if (withArea != null) item.BestGuessArea = withArea.AreaName;
        }

        // Best guess mob - prefer entries with both mob and area
        var withMob = item.Locations
            .Where(l => !string.IsNullOrEmpty(l.MobName))
            .OrderByDescending(l => !string.IsNullOrEmpty(l.AreaName) ? 1 : 0)
            .FirstOrDefault();
        item.BestGuessMob = withMob?.MobName;

        // Best guess container
        var withContainer = item.Locations
            .Where(l => !string.IsNullOrEmpty(l.ContainerName))
            .FirstOrDefault();
        item.BestGuessContainer = withContainer?.ContainerName;
    }
}
