using Azure;
using Azure.Data.Tables;
using System.Runtime.Serialization;

namespace CFItems.DataPipeline.Models;

public class ItemRecord : ITableEntity
{
    public string PartitionKey { get; set; } = "CFItems";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string? Name { get; set; }
    public string? Level { get; set; }
    public int Worth { get; set; }
    public string? Type { get; set; }
    public string? Group { get; set; }
    public string? Damnoun { get; set; }
    public string? BaseDamnoun { get; set; }
    public string? Weight { get; set; }
    public string? Kg { get; set; }
    public string? Gram { get; set; }
    public string? Material { get; set; }
    public string? FullDataPiped { get; set; }
    public string? AffectsPiped { get; set; }
    public string? MagicAffectsPiped { get; set; }
    public string? Area { get; set; }
    public string? Avg { get; set; }
    public string? FlaggsPiped { get; set; }
    public string? ModifiersPiped { get; set; }
    public string? Hit { get; set; }
    public string? Dam { get; set; }
    public string? Hp { get; set; }
    public string? Mana { get; set; }
    public string? Moves { get; set; }
    public string? Str { get; set; }
    public string? Int { get; set; }
    public string? Wis { get; set; }
    public string? Dex { get; set; }
    public string? Con { get; set; }
    public string? Chr { get; set; }
    public string? Svs { get; set; }
    public string? Svp { get; set; }
    public string? Svb { get; set; }
    public string? Svm { get; set; }
    public string? Ac { get; set; }
    public string? Pierce { get; set; }
    public string? Bash { get; set; }
    public string? Slash { get; set; }
    public string? Magic { get; set; }
    public string? Element { get; set; }
    public string? Spell { get; set; }
    public string? SpellLevel { get; set; }
    public string? Age { get; set; }
    public string? Morale { get; set; }
    public string? ArmorLine { get; set; }
    public string? MobSource { get; set; }
    public string? ContainerSource { get; set; }
    public string? PathFromCrossroads { get; set; }

    public bool IsWeapon => Group == "weapon";
    public bool IsMagic => new[] { "pill", "potion", "wand", "talisman", "scroll" }.Contains(Group);
}

public class ItemLocation
{
    public string ItemName { get; set; } = "";
    public List<LocationEntry> Locations { get; set; } = new();
    public string? BestGuessArea { get; set; }
    public string? BestGuessRoom { get; set; }
    public string? BestGuessMob { get; set; }
    public string? BestGuessContainer { get; set; }
    public string? PathFromCrossroads { get; set; }
}

public class LocationEntry
{
    public string? RoomName { get; set; }
    public string? AreaName { get; set; }
    public string? MobName { get; set; }
    public string? ContainerName { get; set; }
    public string? SourceLog { get; set; }
    public int LineNumber { get; set; }
    public string Confidence { get; set; } = "low"; // high, medium, low
}

public class RoomNode
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Exits { get; set; } = new(); // direction -> room name
    public string? AreaType { get; set; } // civilized, wilderness, PROTECTED
    public string? AreaName { get; set; }
    public HashSet<string> MobsSeen { get; set; } = new();
    public int VisitCount { get; set; }
}

public class WorldMap
{
    public Dictionary<string, RoomNode> Rooms { get; set; } = new();
    public int TotalEdges { get; set; }
    public int TotalLogsProcessed { get; set; }
}

public class ForumPost
{
    public string? Author { get; set; }
    public string? Timestamp { get; set; }
    public string? Title { get; set; }
    public string Body { get; set; } = "";
}

public class ForumThread
{
    public string ThreadId { get; set; } = "";
    public string ForumId { get; set; } = "";
    public string ForumName { get; set; } = "";
    public string? Title { get; set; }
    public string Url { get; set; } = "";
    public List<ForumPost> Posts { get; set; } = new();
    public string? FirstPostDate { get; set; }
}

public class TrainingDocument
{
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Title { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class ScrapedPage
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Text { get; set; } = "";
    public string? RawHtml { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class AreaInfo
{
    public string Name { get; set; } = "";
    public string? LevelRange { get; set; }
    public string? Builder { get; set; }
    public string? Category { get; set; } // Town, Road, Water, Forest, etc.
    public List<string> KnownRooms { get; set; } = new();
    public List<string> KnownMobs { get; set; } = new();
    public List<string> KnownItems { get; set; } = new();
    public bool IsRestricted { get; set; } // "No Share" areas
}

public class SeedItemMapping
{
    public string ItemName { get; set; } = "";
    public string? MobName { get; set; }
    public string? AreaName { get; set; }
    public string? Slot { get; set; }
}
