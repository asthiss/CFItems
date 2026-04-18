using CFItems.Bot;
using CFItems.Bot.Services;

// Configuration (override via env vars)
var ollamaUrl    = Environment.GetEnvironmentVariable("CFBOT_OLLAMA")     ?? "http://localhost:11434";
var embedModel   = Environment.GetEnvironmentVariable("CFBOT_EMBED")      ?? "nomic-embed-text";
var chatModel    = Environment.GetEnvironmentVariable("CFBOT_MODEL")      ?? "llama3.2:3b";
var port         = int.TryParse(Environment.GetEnvironmentVariable("CFBOT_PORT"), out var p) ? p : 5005;

var repoRoot = FindRepoRoot();
var dataDir = Path.Combine(repoRoot, "data");
var indexPath = Path.Combine(dataDir, "bot", "index.json");
Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

var ollama = new OllamaClient(ollamaUrl);

var command = args.Length > 0 ? args[0] : "help";

switch (command)
{
    case "index":
        await new Indexer(ollama, embedModel, dataDir).BuildAsync(indexPath);
        break;

    case "serve":
        if (!File.Exists(indexPath))
        {
            Console.WriteLine($"Index not found at {indexPath}. Run 'dotnet run -- index' first.");
            return;
        }
        var retrieval = new RetrievalEngine();
        Console.WriteLine($"Loading index from {indexPath}...");
        retrieval.Load(indexPath);
        var server = new ChatServer(ollama, retrieval, embedModel, chatModel, port);
        await server.RunAsync();
        break;

    case "ask":
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: dotnet run -- ask \"your question\"");
            return;
        }
        if (!File.Exists(indexPath))
        {
            Console.WriteLine($"Index not found at {indexPath}. Run 'dotnet run -- index' first.");
            return;
        }
        var rEngine = new RetrievalEngine();
        rEngine.Load(indexPath);
        var oneShot = new ChatServer(ollama, rEngine, embedModel, chatModel);
        var resp = await oneShot.AnswerAsync(string.Join(' ', args.Skip(1)), 6);
        Console.WriteLine("\n=== Answer ===");
        Console.WriteLine(resp.Answer);
        Console.WriteLine("\n=== Sources ===");
        foreach (var s in resp.Sources) Console.WriteLine($"  [{s.Score:F2}] {s.Title} ({s.SourceType})");
        break;

    case "help":
    default:
        Console.WriteLine("CFItems.Bot — RAG bot over scraped Carrion Fields data");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  index            Build the embedding index from scraped data");
        Console.WriteLine("  serve            Start the HTTP server on port " + port);
        Console.WriteLine("  ask \"question\"   One-shot question on the command line");
        Console.WriteLine();
        Console.WriteLine("Environment variables:");
        Console.WriteLine($"  CFBOT_OLLAMA  - Ollama base URL (current: {ollamaUrl})");
        Console.WriteLine($"  CFBOT_EMBED   - Embedding model (current: {embedModel})");
        Console.WriteLine($"  CFBOT_MODEL   - Chat model (current: {chatModel})");
        Console.WriteLine($"  CFBOT_PORT    - HTTP server port (current: {port})");
        Console.WriteLine();
        Console.WriteLine("Setup:");
        Console.WriteLine("  1. Install Ollama from https://ollama.com");
        Console.WriteLine("  2. ollama pull nomic-embed-text");
        Console.WriteLine("  3. ollama pull llama3.2:3b");
        Console.WriteLine("  4. dotnet run -- index");
        Console.WriteLine("  5. dotnet run -- serve");
        break;
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
