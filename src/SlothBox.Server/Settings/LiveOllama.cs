using SlothBox.Ai.Analyze;
using SlothBox.Ai.Embed;
using SlothBox.Ai.Status;
using SlothBox.Core;

namespace SlothBox.Server.Settings;

// Settings > AI can change the Ollama address while SlothBox runs (spec 7.4.8): the next call goes to the saved one.
// Holds a client built for the current Ollama settings and builds a new one once they change.
public sealed class OllamaFollower<T>(ISettingsStore settings, Func<SlothBoxSettings, T> create) where T : class
{
    private readonly Lock _gate = new();
    private (OllamaSettings Ollama, T Client)? _built;

    public T Current
    {
        get
        {
            var current = settings.Current;
            lock (_gate)
            {
                if (_built is not { } built || built.Ollama != current.Ollama)
                    _built = built = (current.Ollama, create(current));
                return built.Client;
            }
        }
    }
}

public sealed class LiveOllamaStatus(ISettingsStore settings) : IOllamaStatus
{
    private readonly OllamaFollower<IOllamaStatus> _ollama =
        new(settings, current => new OllamaStatus(ServerHost.OllamaClient(current.Ollama), current.Ollama));

    // The status check for the saved address.
    public IOllamaStatus Current => _ollama.Current;

    public Task<GemmaState> CheckAsync(CancellationToken ct) => Current.CheckAsync(ct);
}

public sealed class LiveEmbedder(ISettingsStore settings) : IEmbedder
{
    private readonly OllamaFollower<IEmbedder> _ollama =
        new(settings, current => new Embedder(ServerHost.OllamaClient(current.Ollama), current.Ollama));

    // The embedder for the saved address.
    public IEmbedder Current => _ollama.Current;

    public Task<float[]> EmbedDocumentAsync(string title, string body, CancellationToken ct) =>
        Current.EmbedDocumentAsync(title, body, ct);

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) => Current.EmbedQueryAsync(query, ct);
}

public sealed class LiveAnalyzer(ISettingsStore settings) : IFileAnalyzer
{
    private readonly OllamaFollower<IFileAnalyzer> _ollama = new(settings, current =>
        new FileAnalyzer(ServerHost.OllamaClient(current.Ollama), current.Ollama, current.Pipeline));

    // The analyzer for the saved address.
    public IFileAnalyzer Current => _ollama.Current;

    public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken ct) =>
        Current.AnalyzeAsync(request, ct);
}
