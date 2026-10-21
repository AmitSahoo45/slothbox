using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SlothBox.Ai.Embed;
using SlothBox.Core;

namespace SlothBox.Tests.Ai;

public sealed class EmbedderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly float[] Vector = Enumerable.Range(0, 768).Select(i => i / 768f).ToArray();

    // Stands in for Ollama's POST /api/embed and records what SlothBox sent.
    private sealed class FakeOllama(Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(string Path, JsonNode Body)> Requests { get; } = [];

        public static FakeOllama Answering(float[] vector) => new((_, _) => Task.FromResult(Embeddings(vector)));

        public static HttpResponseMessage Embeddings(float[] vector) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["model"] = "embeddinggemma",
                    ["embeddings"] = new JsonArray(new JsonArray([.. vector.Select(v => JsonValue.Create(v))]))
                }.ToJsonString(), Encoding.UTF8, "application/json")
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath,
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!));
            return await respond(Requests.Count, ct);
        }
    }

    private static Embedder Embedder(FakeOllama ollama, TimeSpan? attemptTimeout = null) =>
        new(new HttpClient(ollama) { BaseAddress = new Uri("http://127.0.0.1:11436") }, new OllamaSettings(),
            attemptTimeout);

    [Fact]
    public async Task A_document_is_embedded_with_its_title_prefix_and_gives_768_numbers()
    {
        var ollama = FakeOllama.Answering(Vector);

        var vector = await Embedder(ollama).EmbedDocumentAsync("Jio recharge",
            "Bills and recharges. UPI payment of 599 INR to Jio", Ct);

        vector.ShouldBe(Vector);
        var (path, body) = ollama.Requests.ShouldHaveSingleItem();
        path.ShouldBe("/api/embed");
        body["model"]!.GetValue<string>().ShouldBe("embeddinggemma");
        body["input"]!.AsArray().Select(i => i!.GetValue<string>())
            .ShouldBe(["title: Jio recharge | text: Bills and recharges. UPI payment of 599 INR to Jio"]);
    }

    [Fact]
    public async Task A_document_without_a_title_uses_the_word_none()
    {
        var ollama = FakeOllama.Answering(Vector);

        await Embedder(ollama).EmbedDocumentAsync("", "Other. A photo of a receipt", Ct);

        ollama.Requests.Single().Body["input"]![0]!.GetValue<string>()
            .ShouldBe("title: none | text: Other. A photo of a receipt");
    }

    [Fact]
    public async Task A_query_uses_the_search_result_prefix()
    {
        var ollama = FakeOllama.Answering(Vector);

        await Embedder(ollama).EmbedQueryAsync("that jio bill", Ct);

        ollama.Requests.Single().Body["input"]![0]!.GetValue<string>()
            .ShouldBe("task: search result | query: that jio bill");
    }

    [Fact]
    public async Task Embeddings_run_on_the_cpu_and_keep_the_model_loaded_for_30_minutes()
    {
        var ollama = FakeOllama.Answering(Vector);

        await Embedder(ollama).EmbedQueryAsync("electricity October", Ct);

        var body = ollama.Requests.Single().Body;
        body["options"]!["num_gpu"]!.GetValue<int>().ShouldBe(0);
        body["keep_alive"]!.GetValue<string>().ShouldBe("30m");
    }

    [Fact]
    public async Task A_vector_that_is_not_768_numbers_long_is_refused()
    {
        var ollama = FakeOllama.Answering([0.1f, 0.2f, 0.3f]);

        await Should.ThrowAsync<InvalidOperationException>(() => Embedder(ollama).EmbedQueryAsync("jio", Ct));
    }

    [Fact]
    public async Task A_call_that_times_out_is_tried_once_more()
    {
        var ollama = new FakeOllama(async (attempt, ct) =>
        {
            if (attempt == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return FakeOllama.Embeddings(Vector);
        });

        var vector = await Embedder(ollama, TimeSpan.FromMilliseconds(200)).EmbedQueryAsync("jio", Ct);

        vector.ShouldBe(Vector);
        ollama.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_second_timeout_gives_up()
    {
        var ollama = new FakeOllama(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return FakeOllama.Embeddings(Vector);
        });

        await Should.ThrowAsync<TimeoutException>(() =>
            Embedder(ollama, TimeSpan.FromMilliseconds(200)).EmbedQueryAsync("jio", Ct));
        ollama.Requests.Count.ShouldBe(2);
    }
}
