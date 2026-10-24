using System.Text.Json;
using System.Text.Json.Nodes;
using SlothBox.Core;

namespace SlothBox.Ai.Status;

// Spec 4.3 step 7: asks Ollama which models are pulled (/api/tags) and which are loaded (/api/ps).
// The HttpClient's base address is the Ollama URL.
public sealed class OllamaStatus(HttpClient http, OllamaSettings settings, TimeSpan? timeout = null) : IOllamaStatus
{
    // A status check must answer quickly even when Ollama hangs: the dashboard waits for it.
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(3);

    public async Task<GemmaState> CheckAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        string message;
        try
        {
            var pulled = await ModelsAsync("api/tags", limit.Token);
            var loaded = await ModelsAsync("api/ps", limit.Token);
            return new GemmaState(true, null, State(settings.VisionModel, pulled, loaded),
                State(settings.EmbeddingModel, pulled, loaded));
        }
        catch (HttpRequestException e) when (e.StatusCode is { } status)
        {
            message = $"Ollama at {settings.Url} answered with an error ({(int)status}).";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException ||
                                  e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            message = $"Ollama is not answering at {settings.Url}.";
        }
        return new GemmaState(false, message, new ModelState(settings.VisionModel, false, false, false),
            new ModelState(settings.EmbeddingModel, false, false, false));
    }

    private async Task<IReadOnlyList<JsonObject>> ModelsAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is not JsonObject { } root)
            throw new JsonException("Ollama's answer is not a JSON object.");
        return root["models"] is JsonArray models ? models.OfType<JsonObject>().ToList() : [];
    }

    private static ModelState State(string name, IReadOnlyList<JsonObject> pulled, IReadOnlyList<JsonObject> loaded)
    {
        var running = loaded.FirstOrDefault(model => Matches(model, name));
        var size = Number(running?["size"]);
        // Wholly on the GPU: the whole model sits in VRAM (spec 5.7 day-one rule).
        var onGpu = running is not null && size > 0 && Number(running["size_vram"]) >= size;
        return new ModelState(name, pulled.Any(model => Matches(model, name)), running is not null, onGpu);
    }

    private static bool Matches(JsonObject model, string name) =>
        new[] { model["name"], model["model"] }.Any(node =>
            node is JsonValue value && value.TryGetValue(out string? listed) &&
            string.Equals(Tagged(listed), Tagged(name), StringComparison.OrdinalIgnoreCase));

    // Ollama lists "embeddinggemma" as "embeddinggemma:latest".
    private static string Tagged(string name) => name.Contains(':') ? name : name + ":latest";

    private static long Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out long number) ? number : 0;
}
