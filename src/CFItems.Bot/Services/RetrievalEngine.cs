using System.Text.Json;

namespace CFItems.Bot.Services;

/// <summary>Loads the index from disk and serves cosine-similarity search.</summary>
public class RetrievalEngine
{
    private IndexFile _index = new();

    public string EmbeddingModel => _index.EmbeddingModel;
    public int Dimensions => _index.Dimensions;
    public int ChunkCount => _index.Chunks.Count;

    public void Load(string path)
    {
        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<IndexFile>(json);
        _index = loaded ?? new IndexFile();
    }

    /// <summary>Find top-K most similar chunks to the question embedding.</summary>
    public List<(IndexChunk chunk, double score)> Search(float[] questionEmbedding, int topK)
    {
        var scored = new List<(IndexChunk, double)>(_index.Chunks.Count);
        foreach (var c in _index.Chunks)
        {
            if (c.Embedding.Length != questionEmbedding.Length) continue;
            scored.Add((c, CosineSimilarity(c.Embedding, questionEmbedding)));
        }
        return scored
            .OrderByDescending(x => x.Item2)
            .Take(topK)
            .ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}
