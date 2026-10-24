using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Core;
using SlothBox.Data;

namespace SlothBox.Tests.Api;

public sealed class ServerFactory : WebApplicationFactory<Server.Program>
{
    // The real settings file and database are never touched by API tests.
    private sealed class DefaultSettings : ISettingsStore
    {
        public SlothBoxSettings Current { get; } = new();
    }

    private sealed class NoQueue : IJobQueue
    {
        public Task EnqueueAsync(IncomingFile file, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> EnqueueUnlessFinishedAsync(IncomingFile file, Core.Folders.FileStamp stamp,
            CancellationToken ct) => Task.FromResult(false);
        public Task<Job?> ClaimNextAsync(CancellationToken ct) => Task.FromResult<Job?>(null);
        public Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct) => Task.CompletedTask;
        public Task<int> RequeueInterruptedAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<QueueCounts> CountsAsync(CancellationToken ct) => Task.FromResult(new QueueCounts(0, 0));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing").ConfigureTestServices(services =>
        {
            services.AddSingleton<ISettingsStore, DefaultSettings>();
            services.AddSingleton<IJobQueue, NoQueue>();
            // The real Ollama, the real database and programs on this machine stay out of API tests.
            services.AddSingleton<IOllamaStatus, FixedGemma>();
            services.AddSingleton<IFileLauncher, RecordingLauncher>();
            services.AddSingleton<IDbContextFactory<SlothBoxDbContext>, NoDatabase>();
        });

    public HttpClient DashboardClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5080"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-SlothBox", "1");
        return client;
    }
}
