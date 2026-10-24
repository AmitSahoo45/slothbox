using OllamaSharp.Models.Exceptions;
using SlothBox.Core;
using SlothBox.Core.Pipeline;

namespace SlothBox.Tests.Pipeline;

// Spec 5.6: a file whose embedding fails stays filed without a vector, whatever the embedder throws; only shutdown
// stops it. "Rebuild missing search data" fills the vector in later.
public sealed class FileIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ollama")]
    [InlineData("response")]
    [InlineData("connection")]
    [InlineData("timeout")]
    [InlineData("client timeout")]
    [InlineData("anything else")]
    public async Task An_embedder_error_leaves_the_file_without_a_vector(string error)
    {
        var embedder = new FakeEmbedder { Failure = Error(error) };
        var files = new RecordingFiles();

        var indexed = await FileIndex.EmbedAsync(TestRecords.File("abc"), "Bills", embedder, files,
            new PipelineSettings(), Ct);

        indexed.ShouldBeFalse();
        files.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shutdown_still_stops_the_embedding()
    {
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var embedder = new FakeEmbedder { Failure = new OperationCanceledException(shutdown.Token) };

        await Should.ThrowAsync<OperationCanceledException>(() => FileIndex.EmbedAsync(TestRecords.File("abc"),
            "Bills", embedder, new RecordingFiles(), new PipelineSettings(), shutdown.Token));
    }

    [Fact]
    public async Task The_vector_is_saved_when_the_embedder_answers()
    {
        var file = TestRecords.File("abc");
        var files = new RecordingFiles();

        var indexed = await FileIndex.EmbedAsync(file, "Bills", new FakeEmbedder(), files, new PipelineSettings(), Ct);

        indexed.ShouldBeTrue();
        files.Saved.ShouldBe([file.Id]);
    }

    private static Exception Error(string kind) => kind switch
    {
        "ollama" => new OllamaException("model \"embeddinggemma\" not found"),
        "response" => new ResponseError("the input is too long"),
        "connection" => new HttpRequestException("No connection could be made."),
        "timeout" => new TimeoutException(),
        // An HTTP client timeout surfaces as a cancellation that nobody asked for.
        "client timeout" => new TaskCanceledException("The request was canceled due to the HttpClient.Timeout."),
        _ => new FormatException("Unexpected answer.")
    };

    private sealed class RecordingFiles : IFileRepository
    {
        public List<Guid> Saved { get; } = [];

        public Task SaveEmbeddingAsync(Guid id, float[] embedding, CancellationToken ct)
        {
            Saved.Add(id);
            return Task.CompletedTask;
        }

        public Task<FileRecord?> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<FileRecord?> FindFiledByHashAsync(string sha256, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<FileRecord?> FindUnfinishedAsync(string sha256, string originalPath, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task SaveAsync(FileRecord file, CancellationToken ct) => throw new NotSupportedException();
    }
}
