using OllamaSharp;
using OllamaSharp.Models;
using SlothBox.Core;

namespace SlothBox.Ai.Embed;

// Spec 5.6: EmbeddingGemma on the CPU with the model card's prefixes; Ollama adds none itself.
// The HttpClient's base address is the Ollama URL.
public sealed class Embedder(HttpClient http, OllamaSettings settings, TimeSpan? attemptTimeout = null) : IEmbedder
{
    public const int Dimensions = 768;

    private readonly OllamaApiClient _ollama = new(http, settings.EmbeddingModel);
    private readonly TimeSpan _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(settings.EmbedTimeoutSeconds);

    public Task<float[]> EmbedDocumentAsync(string title, string body, CancellationToken ct) =>
        EmbedAsync($"title: {(string.IsNullOrWhiteSpace(title) ? "none" : title)} | text: {body}", ct);

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) =>
        EmbedAsync($"task: search result | query: {query}", ct);

    private async Task<float[]> EmbedAsync(string input, CancellationToken ct)
    {
        var request = new EmbedRequest
        {
            Model = settings.EmbeddingModel,
            Input = [input],
            KeepAlive = settings.KeepAlive,
            // num_gpu 0 keeps EmbeddingGemma on the CPU, so Gemma has the GPU to itself.
            Options = new RequestOptions { NumGpu = 0 }
        };
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_attemptTimeout);
            try
            {
                var response = await _ollama.EmbedAsync(request, timeout.Token);
                var vector = response.Embeddings?.FirstOrDefault();
                if (vector?.Length != Dimensions)
                    throw new InvalidOperationException(
                        $"EmbeddingGemma returned {vector?.Length ?? 0} numbers instead of {Dimensions}.");
                return vector;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // One retry after a timeout.
                if (attempt == 2)
                    throw new TimeoutException(
                        $"EmbeddingGemma did not answer within {_attemptTimeout.TotalSeconds:0.#} s, twice.");
            }
        }
    }
}
