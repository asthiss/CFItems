namespace CFItems.Bot;

/// <summary>One indexed chunk: text content + its embedding vector.</summary>
public class IndexChunk
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";   // wiki, item, area, forum, helpfile, qhcf-site
    public string? Title { get; set; }         // page/thread/item title for citation
    public string? Url { get; set; }           // source URL or anchor for follow-up
    public float[] Embedding { get; set; } = Array.Empty<float>();
}

/// <summary>Container for the whole index, saved to JSON.</summary>
public class IndexFile
{
    public string EmbeddingModel { get; set; } = "";
    public int Dimensions { get; set; }
    public DateTime BuiltAt { get; set; } = DateTime.UtcNow;
    public List<IndexChunk> Chunks { get; set; } = new();
}

public class ChatRequest
{
    public string Question { get; set; } = "";
    public int TopK { get; set; } = 6;
}

public class ChatResponse
{
    public string Answer { get; set; } = "";
    public List<Source> Sources { get; set; } = new();
}

public class Source
{
    public string Title { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string? Url { get; set; }
    public double Score { get; set; }
    public string Snippet { get; set; } = "";
}
