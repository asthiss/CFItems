using CFItems.DataPipeline.Models;

namespace CFItems.DataPipeline.Extractors;

public class PathFinder
{
    private readonly WorldMap _map;

    public PathFinder(WorldMap map)
    {
        _map = map;
    }

    /// <summary>
    /// Find the best starting room on the Eastern Road crossroads.
    /// </summary>
    public string? FindCrossroadsRoom()
    {
        // Priority: rooms with "Crossroads" in name on Eastern Road
        var candidates = _map.Rooms.Keys
            .Where(r => r.Contains("Crossroads", StringComparison.OrdinalIgnoreCase) ||
                        r.Contains("Eastern Road", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Prefer the room with most exits (most connected = best crossroads)
        var best = candidates
            .Where(r => _map.Rooms.ContainsKey(r))
            .OrderByDescending(r => _map.Rooms[r].Exits.Count)
            .ThenByDescending(r => r.Contains("Crossroads", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

        if (best != null)
            Console.WriteLine($"Crossroads room: '{best}' with {_map.Rooms[best].Exits.Count} exits");

        return best;
    }

    /// <summary>
    /// BFS from a starting room to find shortest paths to all reachable rooms.
    /// Returns dictionary of room -> (path directions, distance)
    /// </summary>
    public Dictionary<string, (List<string> directions, int distance)> BfsFromRoom(string startRoom, int maxDistance = 200)
    {
        var result = new Dictionary<string, (List<string>, int)>();
        var visited = new HashSet<string>();
        var queue = new Queue<(string room, List<string> path)>();

        queue.Enqueue((startRoom, new List<string>()));
        visited.Add(startRoom);
        result[startRoom] = (new List<string>(), 0);

        while (queue.Count > 0)
        {
            var (room, path) = queue.Dequeue();

            if (path.Count >= maxDistance)
                continue;

            if (!_map.Rooms.TryGetValue(room, out var node))
                continue;

            foreach (var (direction, nextRoom) in node.Exits)
            {
                if (visited.Contains(nextRoom))
                    continue;

                visited.Add(nextRoom);
                var newPath = new List<string>(path) { direction };
                result[nextRoom] = (newPath, newPath.Count);
                queue.Enqueue((nextRoom, newPath));
            }
        }

        Console.WriteLine($"BFS from '{startRoom}': reached {result.Count} rooms (max distance: {result.Values.MaxBy(v => v.Item2).Item2})");
        return result;
    }

    /// <summary>
    /// Compress a path into shorthand: n;n;n;w;w -> 3n;2w
    /// </summary>
    public static string CompressPath(List<string> directions)
    {
        if (directions.Count == 0) return "";

        var compressed = new List<string>();
        var currentDir = WorldMapBuilder.ShortDir(directions[0]);
        var count = 1;

        for (var i = 1; i < directions.Count; i++)
        {
            var dir = WorldMapBuilder.ShortDir(directions[i]);
            if (dir == currentDir)
            {
                count++;
            }
            else
            {
                compressed.Add(count > 1 ? $"{count}{currentDir}" : currentDir);
                currentDir = dir;
                count = 1;
            }
        }
        compressed.Add(count > 1 ? $"{count}{currentDir}" : currentDir);

        return string.Join(";", compressed);
    }

    /// <summary>
    /// Calculate PathFromCrossroads for all items that have a known room.
    /// </summary>
    public void CalculateItemPaths(
        Dictionary<string, ItemLocation> itemLocations,
        Dictionary<string, (List<string> directions, int distance)> bfsResults)
    {
        var found = 0;
        var notFound = 0;

        foreach (var item in itemLocations.Values)
        {
            var room = item.BestGuessRoom;
            if (string.IsNullOrEmpty(room))
            {
                notFound++;
                continue;
            }

            if (bfsResults.TryGetValue(room, out var pathInfo))
            {
                item.PathFromCrossroads = CompressPath(pathInfo.directions);
                found++;
            }
            else
            {
                notFound++;
            }
        }

        Console.WriteLine($"Paths calculated: {found} items with paths, {notFound} without (room not reachable or unknown)");
    }
}
