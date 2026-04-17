using System.Text.RegularExpressions;
using CFItems.DataPipeline.Models;

namespace CFItems.DataPipeline.Extractors;

public class WorldMapBuilder
{
    private static readonly Regex PromptRegex = new(@"^(PROTECTED )?(civilized|wilderness)\s+\d+/\d+\|hp", RegexOptions.Compiled);
    private static readonly Regex ExitsRegex = new(@"\[Exits:\s*(.*?)\]", RegexOptions.Compiled);
    private static readonly Regex DirectionOnPrompt = new(@"\s(n|s|e|w|north|south|east|west|up|down|ne|nw|se|sw|northeast|northwest|southeast|southwest)\s*$", RegexOptions.Compiled);
    private static readonly Regex MobLineRegex = new(@"(?:is here|stands here|is sleeping|is resting|is hovering|floats here|sits here|lies here)", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> DirNormalize = new(StringComparer.OrdinalIgnoreCase)
    {
        {"n","north"}, {"s","south"}, {"e","east"}, {"w","west"},
        {"ne","northeast"}, {"nw","northwest"}, {"se","southeast"}, {"sw","southwest"},
        {"north","north"}, {"south","south"}, {"east","east"}, {"west","west"},
        {"up","up"}, {"down","down"},
        {"northeast","northeast"}, {"northwest","northwest"}, {"southeast","southeast"}, {"southwest","southwest"}
    };

    private static readonly Dictionary<string, string> DirShort = new()
    {
        {"north","n"}, {"south","s"}, {"east","e"}, {"west","w"},
        {"up","u"}, {"down","d"},
        {"northeast","ne"}, {"northwest","nw"}, {"southeast","se"}, {"southwest","sw"}
    };

    /// <summary>
    /// Build a world map by tracking movement across all log files.
    /// </summary>
    public WorldMap BuildFromLogs(string logDirectory)
    {
        var map = new WorldMap();

        var logFiles = Directory.GetFiles(logDirectory, "*.log")
            .Concat(Directory.GetFiles(logDirectory, "*.txt"))
            .Concat(Directory.GetFiles(logDirectory, "*.TXT"))
            .Distinct()
            .ToArray();

        Console.WriteLine($"Building world map from {logFiles.Length} log files...");

        var processed = 0;
        foreach (var logFile in logFiles)
        {
            try
            {
                ProcessLogForMap(logFile, map);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in {Path.GetFileName(logFile)}: {ex.Message}");
            }

            processed++;
            if (processed % 100 == 0)
                Console.WriteLine($"Processed {processed}/{logFiles.Length} files, {map.Rooms.Count} rooms found...");
        }

        map.TotalLogsProcessed = processed;
        map.TotalEdges = map.Rooms.Values.Sum(r => r.Exits.Count);

        Console.WriteLine($"World map: {map.Rooms.Count} rooms, {map.TotalEdges} edges from {processed} logs");
        return map;
    }

    private void ProcessLogForMap(string filePath, WorldMap map)
    {
        var lines = File.ReadAllLines(filePath);

        string? currentRoom = null;
        string? currentAreaType = null;
        string? pendingDirection = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Detect prompt lines with movement commands
            var promptMatch = PromptRegex.Match(trimmed);
            if (promptMatch.Success)
            {
                currentAreaType = promptMatch.Groups[2].Value;

                // Check for direction command at end of prompt line
                var dirMatch = DirectionOnPrompt.Match(trimmed);
                if (dirMatch.Success && DirNormalize.TryGetValue(dirMatch.Groups[1].Value, out var normDir))
                {
                    pendingDirection = normDir;
                }
                continue;
            }

            // Detect [Exits:] line - this means we're in a room description
            var exitsMatch = ExitsRegex.Match(trimmed);
            if (exitsMatch.Success)
            {
                // Find the room name (walk backward to title)
                var roomName = FindRoomName(lines, i);
                if (roomName == null) continue;

                // Parse available exits
                var exitsStr = exitsMatch.Groups[1].Value;
                var availableExits = ParseExits(exitsStr);

                // Get or create room node
                if (!map.Rooms.TryGetValue(roomName, out var roomNode))
                {
                    roomNode = new RoomNode { Name = roomName, AreaType = currentAreaType };
                    map.Rooms[roomName] = roomNode;
                }
                roomNode.VisitCount++;
                if (currentAreaType != null)
                    roomNode.AreaType = currentAreaType;

                // Record mobs in the room (lines after [Exits:] until next prompt or blank section)
                for (var j = i + 1; j < Math.Min(i + 20, lines.Length); j++)
                {
                    var mobLine = lines[j].Trim();
                    if (string.IsNullOrEmpty(mobLine) || PromptRegex.IsMatch(mobLine))
                        break;
                    if (MobLineRegex.IsMatch(mobLine))
                    {
                        // Clean mob line
                        var mob = mobLine.Replace("(White Aura) ", "").Replace("(Black Aura) ", "")
                            .Replace("(Translucent) ", "").Replace("(Glowing) ", "").Replace("(Humming) ", "").Trim();
                        if (mob.Length > 3 && mob.Length < 100)
                            roomNode.MobsSeen.Add(mob);
                    }
                }

                // If we moved here from a previous room, record the edge
                if (pendingDirection != null && currentRoom != null && currentRoom != roomName)
                {
                    if (map.Rooms.TryGetValue(currentRoom, out var prevRoom))
                    {
                        prevRoom.Exits.TryAdd(pendingDirection, roomName);
                    }

                    // Also record reverse direction
                    var reverse = GetReverseDirection(pendingDirection);
                    if (reverse != null)
                        roomNode.Exits.TryAdd(reverse, currentRoom);
                }

                currentRoom = roomName;
                pendingDirection = null;
            }
        }
    }

    private string? FindRoomName(string[] lines, int exitsLineIndex)
    {
        for (var i = exitsLineIndex - 1; i >= Math.Max(0, exitsLineIndex - 15); i--)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (PromptRegex.IsMatch(line)) continue;
            if (lines[i].StartsWith("  ") || lines[i].StartsWith("\t")) continue;
            if (line.StartsWith("(") || MobLineRegex.IsMatch(line)) continue;
            if (line.StartsWith("You ") || line.StartsWith("Your ")) continue;
            if (line.Contains("|")) continue;

            if (line.Length > 2 && line.Length < 80)
            {
                if (char.IsUpper(line[0]) || line[0] == '\'' || line[0] == '"')
                    return line;
            }
        }
        return null;
    }

    private List<string> ParseExits(string exitsStr)
    {
        // Remove brackets around individual exits like [south]
        exitsStr = exitsStr.Replace("[", "").Replace("]", "");
        return exitsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => DirNormalize.ContainsKey(e))
            .Select(e => DirNormalize[e])
            .ToList();
    }

    private string? GetReverseDirection(string dir) => dir switch
    {
        "north" => "south", "south" => "north",
        "east" => "west", "west" => "east",
        "up" => "down", "down" => "up",
        "northeast" => "southwest", "southwest" => "northeast",
        "northwest" => "southeast", "southeast" => "northwest",
        _ => null
    };

    /// <summary>
    /// Compact direction shorthand.
    /// </summary>
    public static string ShortDir(string dir) =>
        DirShort.TryGetValue(dir, out var s) ? s : dir;
}
