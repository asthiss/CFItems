using System.Net;
using System.Text;
using System.Text.Json;

namespace CFItems.Bot.Services;

/// <summary>
/// Tiny HttpListener-based server that receives chat requests, does retrieval,
/// and asks Ollama to answer using only the retrieved context.
/// </summary>
public class ChatServer
{
    private readonly OllamaClient _ollama;
    private readonly RetrievalEngine _retrieval;
    private readonly string _embeddingModel;
    private readonly string _chatModel;
    private readonly int _port;

    private const string SystemPrompt =
        "You are a Carrion Fields MUD expert. Carrion Fields is a roleplay-intensive playerkilling MUD running since 1994.\n" +
        "Answer the user's question using ONLY the information in the provided context below.\n" +
        "Cite the source title in brackets when you use a fact, e.g. [Wiki: Galadon] or [Item: a bloodstone ring].\n" +
        "If the context does not contain the answer, say so honestly. Do not make up facts.\n" +
        "Keep answers concise and accurate. If item locations conflict between sources, note it.";

    public ChatServer(OllamaClient ollama, RetrievalEngine retrieval, string embeddingModel, string chatModel, int port = 5005)
    {
        _ollama = ollama;
        _retrieval = retrieval;
        _embeddingModel = embeddingModel;
        _chatModel = chatModel;
        _port = port;
    }

    public async Task RunAsync()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{_port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        listener.Start();

        Console.WriteLine($"\n=== CF Bot listening on http://localhost:{_port} ===");
        Console.WriteLine($"Embedding model: {_embeddingModel}");
        Console.WriteLine($"Chat model: {_chatModel}");
        Console.WriteLine($"Index: {_retrieval.ChunkCount} chunks ({_retrieval.Dimensions}D)");
        Console.WriteLine("\nEndpoints:");
        Console.WriteLine($"  POST /chat       - body: {{\"question\": \"...\", \"topK\": 6}}");
        Console.WriteLine($"  GET  /health     - basic status");
        Console.WriteLine("\nPress Ctrl+C to stop.\n");

        while (listener.IsListening)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                _ = HandleAsync(ctx); // fire and forget
            }
            catch (HttpListenerException) { break; }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        // CORS - allow browser access from any origin (file:// or web)
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        resp.Headers["Access-Control-Allow-Headers"] = "Content-Type";

        try
        {
            if (req.HttpMethod == "OPTIONS") { resp.StatusCode = 204; resp.Close(); return; }

            var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');

            if (path == "/health" || path == "")
            {
                await WriteJson(resp, new { ok = true, chunks = _retrieval.ChunkCount, model = _chatModel });
                return;
            }

            if (path == "/chat" && req.HttpMethod == "POST")
            {
                using var sr = new StreamReader(req.InputStream);
                var bodyJson = await sr.ReadToEndAsync();
                var chat = JsonSerializer.Deserialize<ChatRequest>(bodyJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (chat == null || string.IsNullOrWhiteSpace(chat.Question))
                {
                    resp.StatusCode = 400;
                    await WriteJson(resp, new { error = "missing 'question' in body" });
                    return;
                }

                Console.WriteLine($"\n[?] {chat.Question}");

                var answer = await AnswerAsync(chat.Question, chat.TopK > 0 ? chat.TopK : 6);
                await WriteJson(resp, answer);
                return;
            }

            resp.StatusCode = 404;
            await WriteJson(resp, new { error = "not found" });
        }
        catch (Exception ex)
        {
            resp.StatusCode = 500;
            try { await WriteJson(resp, new { error = ex.Message }); } catch { }
            Console.WriteLine($"  Error handling request: {ex.Message}");
        }
    }

    public async Task<ChatResponse> AnswerAsync(string question, int topK)
    {
        // 1. Embed the question
        var qVec = await _ollama.EmbedAsync(_embeddingModel, question);

        // 2. Retrieve top-K matching chunks
        var hits = _retrieval.Search(qVec, topK);

        // 3. Build context from retrieved chunks
        var ctxBuilder = new StringBuilder();
        foreach (var (chunk, score) in hits)
        {
            ctxBuilder.AppendLine($"--- {chunk.Title ?? chunk.Source} (score: {score:F3}) ---");
            ctxBuilder.AppendLine(chunk.Text);
            ctxBuilder.AppendLine();
        }

        var prompt = $"## Context from CF knowledge base:\n\n{ctxBuilder}\n## Question:\n{question}\n\n## Answer:";

        // 4. Ask Ollama for the answer
        var answer = await _ollama.ChatAsync(_chatModel, SystemPrompt, prompt);

        Console.WriteLine($"  Top hits: {string.Join(", ", hits.Take(3).Select(h => $"{h.chunk.Title}({h.score:F2})"))}");
        Console.WriteLine($"[!] {answer.Substring(0, Math.Min(200, answer.Length))}...");

        return new ChatResponse
        {
            Answer = answer,
            Sources = hits.Select(h => new Source
            {
                Title = h.chunk.Title ?? "(untitled)",
                SourceType = h.chunk.Source,
                Url = h.chunk.Url,
                Score = h.score,
                Snippet = h.chunk.Text.Length > 200 ? h.chunk.Text.Substring(0, 200) + "..." : h.chunk.Text
            }).ToList()
        };
    }

    private static async Task WriteJson(HttpListenerResponse resp, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        resp.Close();
    }
}
