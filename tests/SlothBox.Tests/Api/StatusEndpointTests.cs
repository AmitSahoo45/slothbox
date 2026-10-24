using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Endpoints;
using SlothBox.Server.Worker;

namespace SlothBox.Tests.Api;

public sealed class StatusEndpointTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FixedGemma _gemma = new();
    private readonly CountingQueue _queue = new();
    private readonly FixedSettings _settings = new(new SlothBoxSettings { OutputRoot = @"D:\slothbox-test\out" });
    private readonly StatusServer _server;

    public StatusEndpointTests() => _server = new StatusServer(this);

    private sealed class CountingQueue : IJobQueue
    {
        public QueueCounts Counts { get; set; } = new(0, 0);
        public Exception? Failure { get; set; }

        public Task<QueueCounts> CountsAsync(CancellationToken ct) =>
            Failure is { } failure ? Task.FromException<QueueCounts>(failure) : Task.FromResult(Counts);

        public Task EnqueueAsync(IncomingFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> EnqueueUnlessFinishedAsync(IncomingFile file, Core.Folders.FileStamp stamp,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<Job?> ClaimNextAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> RequeueInterruptedAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StatusServer(StatusEndpointTests test) : WebApplicationFactory<Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Testing").ConfigureTestServices(services =>
            {
                services.AddSingleton<ISettingsStore>(test._settings);
                services.AddSingleton<IJobQueue>(test._queue);
                services.AddSingleton<IOllamaStatus>(test._gemma);
                services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SlothBox.Data.SlothBoxDbContext>,
                    NoDatabase>();
            });
    }

    private HttpClient Client()
    {
        var client = _server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5080")
        });
        client.DefaultRequestHeaders.Add("X-SlothBox", "1");
        return client;
    }

    private WorkerState Worker => _server.Services.GetRequiredService<WorkerState>();

    private async Task<StatusDto> StatusAsync()
    {
        using var client = Client();
        using var response = await client.GetAsync("/api/status", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadAsync<StatusDto>();
    }

    [Fact]
    public async Task Status_reports_Ollama_both_models_the_database_and_the_queue()
    {
        _queue.Counts = new QueueCounts(4, 1);

        var status = await StatusAsync();

        status.ShouldBe(new StatusDto(new ServiceStatusDto(true, null),
            new ModelStatusDto("gemma4:e4b", true, true, true), new ModelStatusDto("embeddinggemma", true, true, false),
            new ServiceStatusDto(true, null), new QueueChangedEvent(4, 1), false, true, null, null));
    }

    [Fact]
    public async Task Status_reports_Ollama_down_and_a_missing_model()
    {
        _gemma.State = new GemmaState(false, "Ollama is not answering at http://127.0.0.1:11436.",
            new ModelState("gemma4:e4b", false, false, false), new ModelState("embeddinggemma", false, false, false));

        var status = await StatusAsync();

        (status.Gemma, status.VisionModel.Present, status.EmbeddingModel.Present).ShouldBe((
            new ServiceStatusDto(false, "Ollama is not answering at http://127.0.0.1:11436."), false, false));
    }

    [Fact]
    public async Task Status_reports_a_database_that_does_not_answer()
    {
        _queue.Failure = new InvalidOperationException("Connection refused.");

        var status = await StatusAsync();

        (status.Database.Ready, status.Queue).ShouldBe((false, new QueueChangedEvent(0, 0)));
        status.Database.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null, "Set SLOTHBOX_DB_PASSWORD (see README setup).")]
    [InlineData("", "Set SLOTHBOX_DB_PASSWORD (see README setup).")]
    [InlineData("synthetic", "The database is not answering.")]
    public void A_missing_password_is_named_as_the_database_problem(string? password, string message) =>
        StatusService.DatabaseProblem(password).ShouldBe(message);

    [Fact]
    public async Task Setup_is_not_complete_until_the_output_folder_is_set()
    {
        _settings.Current = new SlothBoxSettings();

        (await StatusAsync()).SetupComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task Status_carries_the_workers_average_for_today()
    {
        _server.Services.GetRequiredService<WorkerStats>().Record(TimeSpan.FromSeconds(7.5));

        (await StatusAsync()).AverageSecondsPerFileToday.ShouldBe(7.5);
    }

    [Fact]
    public async Task Pause_and_resume_answer_with_the_new_status()
    {
        using var client = Client();

        using var paused = await client.PostAsync("/api/pause", null, Ct);
        paused.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await paused.ReadAsync<StatusDto>()).ShouldSatisfyAllConditions(
            status => status.Paused.ShouldBeTrue(), status => status.PauseReason.ShouldBe(PauseReason.User));
        Worker.Reason.ShouldBe(PauseReason.User);

        using var resumed = await client.PostAsync("/api/resume", null, Ct);
        (await resumed.ReadAsync<StatusDto>()).ShouldSatisfyAllConditions(
            status => status.Paused.ShouldBeFalse(), status => status.PauseReason.ShouldBeNull());
        Worker.Paused.ShouldBeFalse();
    }

    [Fact]
    public async Task Resume_lifts_a_pause_for_a_full_drive()
    {
        Worker.Pause(PauseReason.DriveFull);
        using var client = Client();

        using var response = await client.PostAsync("/api/resume", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadAsync<StatusDto>()).Paused.ShouldBeFalse();
        Worker.Paused.ShouldBeFalse();
    }

    [Fact]
    public async Task Resume_keeps_the_worker_paused_while_a_model_is_missing()
    {
        Worker.Pause(PauseReason.User);
        _gemma.State = FixedGemma.Ready() with { Vision = new ModelState("gemma4:e4b", false, false, false) };
        using var client = Client();

        using var response = await client.PostAsync("/api/resume", null, Ct);

        var status = await response.ReadAsync<StatusDto>();
        (status.Paused, status.PauseReason).ShouldBe((true, (PauseReason?)PauseReason.GemmaOffline));
        Worker.Reason.ShouldBe(PauseReason.GemmaOffline);
    }

    [Fact]
    public async Task Checking_the_status_lifts_a_pause_for_Gemma_once_Gemma_and_both_models_are_back()
    {
        Worker.Pause(PauseReason.GemmaOffline);

        var status = await StatusAsync();

        (status.Paused, status.PauseReason).ShouldBe((false, (PauseReason?)null));
    }

    [Theory]
    [InlineData(PauseReason.User)]
    [InlineData(PauseReason.DriveFull)]
    public async Task Checking_the_status_never_lifts_any_other_pause(PauseReason reason)
    {
        Worker.Pause(reason);

        var status = await StatusAsync();

        (status.Paused, status.PauseReason).ShouldBe((true, (PauseReason?)reason));
    }

    [Fact]
    public async Task A_pause_for_Gemma_stays_while_a_model_is_still_missing()
    {
        Worker.Pause(PauseReason.GemmaOffline);
        _gemma.State = FixedGemma.Ready() with { Embedding = new ModelState("embeddinggemma", false, false, false) };

        var status = await StatusAsync();

        (status.Paused, status.PauseReason).ShouldBe((true, (PauseReason?)PauseReason.GemmaOffline));
    }

    public void Dispose() => _server.Dispose();
}
