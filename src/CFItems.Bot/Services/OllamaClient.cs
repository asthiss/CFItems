using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CFItems.Bot.Services;

/// <summary>
/// Tiny client for the local Ollama HTTP API (default http://localhost:11434).
/// We only use /api/embeddings and /api/chat.
/// </summary>
public class OllamaClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OllamaClient(string baseUrl = "http://localhost:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<float[]> EmbedAsync(string model, string text)
    {
        var body = new { model, prompt = text };
        var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/embeddings", body);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<EmbedResponse>();
        return json?.Embedding ?? Array.Empty<float>();
    }

    public async Task<string> ChatAsync(string model, string systemPrompt, string userMessage)
    {
        var body = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            stream = false,
            options = new { temperature = 0.3 }
        };
        var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/chat", body);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<ChatApiResponse>();
        return json?.Message?.Content ?? "";
    }

    private class EmbedResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    private class ChatApiResponse
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }

    private class ChatMessage
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }
}
