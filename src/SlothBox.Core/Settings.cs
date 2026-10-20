namespace SlothBox.Core;

public sealed record SlothBoxSettings
{
    public string OutputRoot { get; init; } = "";
    public PhoneSettings Phone { get; init; } = new();
    public DashboardSettings Dashboard { get; init; } = new();
    public OllamaSettings Ollama { get; init; } = new();
    public DatabaseSettings Database { get; init; } = new();
    public PipelineSettings Pipeline { get; init; } = new();
    public LazinessSettings Laziness { get; init; } = new();
}

// Token creation belongs to first-run persistence, not to the configuration contract.
public sealed record PhoneSettings(bool Enabled = true, int Port = 5081, string Token = "", int MaxUploadMegabytes = 50);
public sealed record DashboardSettings(int Port = 5080);
public sealed record OllamaSettings(string Url = "http://127.0.0.1:11436", string VisionModel = "gemma4:e4b",
    string EmbeddingModel = "embeddinggemma", string KeepAlive = "30m", int AnalyzeTimeoutSeconds = 120,
    int EmbedTimeoutSeconds = 30);
public sealed record DatabaseSettings(
    string ConnectionString = "Host=127.0.0.1;Port=5433;Database=slothbox;Username=slothbox");
public sealed record PipelineSettings(int SweepMinutes = 5, int StableSeconds = 3, int LockRetryMinutes = 10,
    int MaxFileNameLength = 80, int MaxPathLength = 240, int ImageMaxEdgePx = 1600, int GemmaTextChars = 6000,
    int StoredTextChars = 20000, int EmbedTextChars = 2000, string DefaultCurrency = "INR");
public sealed record LazinessSettings(int ReceiptSeconds = 30, int ScreenshotSeconds = 15, int DocumentSeconds = 15,
    int OtherSeconds = 5);
