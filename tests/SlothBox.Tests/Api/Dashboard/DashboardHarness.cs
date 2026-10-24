using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using SlothBox.Core;
using SlothBox.Core.Pipeline;
using SlothBox.Data;
using SlothBox.Tests.Data;
using SlothBox.Tests.Moving;
using SlothBox.Tests.Pipeline;
using SlothBox.Tests.Reading;

namespace SlothBox.Tests.Api.Dashboard;

// The real server over a fresh database and temp folders, with Ollama, the embedder and other programs faked.
// Files are filed through the real pipeline, so the dashboard reads what SlothBox itself writes.
internal sealed class DashboardHarness : IAsyncDisposable
{
    private readonly DashboardServer _server;
    private int? _folderId;
    private int _photos;

    private DashboardHarness(PipelineHarness pipeline)
    {
        Pipeline = pipeline;
        _server = new DashboardServer(this);
    }

    public static async Task<DashboardHarness> CreateAsync(PostgresFixture postgres) =>
        new(await PipelineHarness.CreateAsync(postgres));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PipelineHarness Pipeline { get; }
    public MoveHarness Moves => Pipeline.Moves;
    public FixedGemma Gemma { get; } = new();
    public RecordingLauncher Launcher { get; } = new();

    // The commands the server sends to the database (the test's own setup is not recorded).
    public CommandLog Commands { get; } = new();

    public T Service<T>() where T : notnull => _server.Services.GetRequiredService<T>();

    // Files a synthetic photo through the real pipeline with the answer Gemma gives for it. Every photo has its own
    // size, so no two have the same content.
    public async Task<FileRecord> FileAsync(string name, Analysis analysis, int? width = null, int height = 48)
    {
        var path = TestFiles.Image(Pipeline.WatchedPath(name), width ?? 64 + ++_photos, height,
            SKEncodedImageFormat.Jpeg);
        return await FilePathAsync(path, analysis);
    }

    public Task<FileRecord> FileTextAsync(string name, string text, Analysis analysis) =>
        FilePathAsync(Pipeline.Watched(name, text), analysis);

    // Moves a file's filing back in time, as if SlothBox had filed it then.
    public async Task FiledAtAsync(Guid fileId, DateTimeOffset at)
    {
        await using var db = Moves.Db.CreateDbContext();
        var utc = at.ToUniversalTime();
        await db.Actions.Where(a => a.FileId == fileId).ExecuteUpdateAsync(set => set
            .SetProperty(a => a.CreatedAt, utc).SetProperty(a => a.CompletedAt, utc), Ct);
    }

    public async Task<FileRecord> FilePathAsync(string path, Analysis analysis)
    {
        var folderId = _folderId ??= await Pipeline.AddFolderAsync();
        Pipeline.Analyzer.Answer = _ => new AnalysisResult.Success(analysis);
        var result = await Pipeline.Pipeline().RunAsync(PipelineHarness.JobFor(path, folderId: folderId), Ct);
        return result.ShouldBeOfType<PipelineResult.Filed>().File;
    }

    public HttpClient Client()
    {
        var client = _server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5080"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-SlothBox", "1");
        return client;
    }

    public async Task<T> GetAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<T>();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: ApiJson.Options);
        return await client.SendAsync(request, Ct);
    }

    public async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string json)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        return await client.SendAsync(request, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await Pipeline.DisposeAsync();
    }

    private sealed class DashboardServer(DashboardHarness h) : WebApplicationFactory<Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Testing").ConfigureTestServices(services =>
            {
                services.AddSingleton<ISettingsStore>(h.Pipeline.Settings);
                services.AddSingleton(h.Moves.Db.With(h.Commands));
                services.AddSingleton<IOllamaStatus>(h.Gemma);
                services.AddSingleton<IFileLauncher>(h.Launcher);
                services.AddSingleton<IEmbedder>(h.Pipeline.Embedder);
            });
    }
}

// What Gemma answers for the synthetic files.
internal static class Analyses
{
    public static readonly ReceiptData Jio = new("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi);

    public static readonly Analysis JioReceipt = new(FileKind.Receipt, "Bills and recharges", "Jio recharge",
        "UPI payment of 599 INR to Jio", "Payment successful Jio 599.00", Jio);

    public static readonly Analysis Chat = new(FileKind.Screenshot, "Chats", "dinner plans chat",
        "A chat about dinner.", "See you at 8", null);

    public static readonly Analysis Notes = new(FileKind.Document, "Learning", "dotnet 10 notes",
        "Notes on .NET 10.", "", null);

    public static Analysis NoDate => JioReceipt with { Receipt = Jio with { Date = null } };

    public static Analysis Unknown => JioReceipt with { Type = FileKind.Unknown, Receipt = null };
}
