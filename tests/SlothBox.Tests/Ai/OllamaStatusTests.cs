using System.Net;
using System.Text;
using SlothBox.Ai.Status;
using SlothBox.Core;

namespace SlothBox.Tests.Ai;

public sealed class OllamaStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BothPulled = """
        {"models":[{"name":"gemma4:e4b","model":"gemma4:e4b","size":6600000000},
                   {"name":"embeddinggemma:latest","model":"embeddinggemma:latest","size":621000000}]}
        """;

    private const string NoneLoaded = """{"models":[]}""";

    // Stands in for Ollama's GET /api/tags and /api/ps.
    private sealed class FakeOllama(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public static FakeOllama Answering(string tags, string ps) =>
            new(path => Json(path == "/api/tags" ? tags : ps));

        public static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request.RequestUri.AbsolutePath));
        }
    }

    private static OllamaStatus Status(HttpMessageHandler ollama) =>
        new(new HttpClient(ollama) { BaseAddress = new Uri("http://127.0.0.1:11436") }, new OllamaSettings(),
            TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Both_models_pulled_means_ready()
    {
        var ollama = FakeOllama.Answering(BothPulled, NoneLoaded);

        var state = await Status(ollama).CheckAsync(Ct);

        state.ShouldBe(new GemmaState(true, null, new ModelState("gemma4:e4b", true, false, false),
            new ModelState("embeddinggemma", true, false, false)));
        state.Ready.ShouldBeTrue();
        ollama.Paths.ShouldBe(["/api/tags", "/api/ps"]);
    }

    [Fact]
    public async Task A_model_is_on_the_GPU_only_when_all_of_it_sits_in_VRAM()
    {
        const string loaded = """
            {"models":[{"name":"gemma4:e4b","model":"gemma4:e4b","size":7000000000,"size_vram":7000000000},
                       {"name":"embeddinggemma:latest","model":"embeddinggemma:latest","size":700000000,"size_vram":0}]}
            """;

        var state = await Status(FakeOllama.Answering(BothPulled, loaded)).CheckAsync(Ct);

        state.Vision.ShouldBe(new ModelState("gemma4:e4b", true, true, true));
        state.Embedding.ShouldBe(new ModelState("embeddinggemma", true, true, false));
    }

    [Fact]
    public async Task A_model_partly_on_the_CPU_is_not_on_the_GPU()
    {
        const string split = """{"models":[{"name":"gemma4:e4b","size":7000000000,"size_vram":5000000000}]}""";

        var state = await Status(FakeOllama.Answering(BothPulled, split)).CheckAsync(Ct);

        state.Vision.ShouldBe(new ModelState("gemma4:e4b", true, true, false));
    }

    [Fact]
    public async Task A_missing_model_is_reported_and_Gemma_is_not_ready()
    {
        const string onlyGemma = """{"models":[{"name":"gemma4:e4b","model":"gemma4:e4b"}]}""";

        var state = await Status(FakeOllama.Answering(onlyGemma, NoneLoaded)).CheckAsync(Ct);

        (state.Reachable, state.Vision.Present, state.Embedding.Present, state.Ready)
            .ShouldBe((true, true, false, false));
    }

    [Fact]
    public async Task Names_match_without_regard_to_case_and_a_missing_tag_means_latest()
    {
        const string tags = """{"models":[{"name":"Gemma4:E4B"},{"name":"EmbeddingGemma:latest"}]}""";

        var state = await Status(FakeOllama.Answering(tags, NoneLoaded)).CheckAsync(Ct);

        state.Ready.ShouldBeTrue();
    }

    [Fact]
    public async Task Ollama_not_answering_is_reported_with_its_address()
    {
        var down = new FakeOllama(_ => throw new HttpRequestException(HttpRequestError.ConnectionError,
            "No connection could be made because the target machine actively refused it."));

        var state = await Status(down).CheckAsync(Ct);

        state.ShouldBe(new GemmaState(false, "Ollama is not answering at http://127.0.0.1:11436.",
            new ModelState("gemma4:e4b", false, false, false), new ModelState("embeddinggemma", false, false, false)));
    }

    [Fact]
    public async Task An_error_status_from_Ollama_is_not_ready()
    {
        var failing = new FakeOllama(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var state = await Status(failing).CheckAsync(Ct);

        state.Reachable.ShouldBeFalse();
        state.Message.ShouldBe("Ollama at http://127.0.0.1:11436 answered with an error (500).");
    }

    // Accepts the connection and never answers.
    private sealed class HungOllama : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    [Fact]
    public async Task An_Ollama_that_does_not_answer_in_time_is_not_ready()
    {
        var status = new OllamaStatus(new HttpClient(new HungOllama()) { BaseAddress = new Uri("http://127.0.0.1:11436") },
            new OllamaSettings(), TimeSpan.FromMilliseconds(50));

        var state = await status.CheckAsync(Ct);

        state.Reachable.ShouldBeFalse();
        state.Message.ShouldBe("Ollama is not answering at http://127.0.0.1:11436.");
    }

    [Fact]
    public async Task A_reply_that_is_not_json_is_not_ready()
    {
        var odd = new FakeOllama(_ => FakeOllama.Json("<html>proxy error</html>"));

        (await Status(odd).CheckAsync(Ct)).Reachable.ShouldBeFalse();
    }
}
