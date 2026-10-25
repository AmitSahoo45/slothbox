using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlothBox.Ai.Analyze;
using SlothBox.Ai.Embed;
using SlothBox.Core;
using SlothBox.Data;
using SlothBox.Server;
using SlothBox.Server.Api;
using SlothBox.Server.Endpoints;
using SlothBox.Server.Phone;
using SlothBox.Server.Settings;
using SlothBox.Server.Tray;
using SlothBox.Tests.Api;
using SlothBox.Tests.Tray;

namespace SlothBox.Tests;

public sealed class ServerHostTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    // Through the wrapper that follows the saved Ollama address (spec 7.4.8).
    [Fact]
    public void Files_are_read_by_Gemmas_analyzer() =>
        factory.Services.GetRequiredService<IFileAnalyzer>().ShouldBeOfType<LiveAnalyzer>().Current
            .ShouldBeOfType<FileAnalyzer>();

    [Fact]
    public void Search_vectors_come_from_the_EmbeddingGemma_embedder() =>
        factory.Services.GetRequiredService<IEmbedder>().ShouldBeOfType<LiveEmbedder>().Current
            .ShouldBeOfType<Embedder>();

    [Fact]
    public void The_Ollama_client_points_at_the_configured_address_and_outlasts_one_Gemma_attempt()
    {
        using var http = ServerHost.OllamaClient(new OllamaSettings(Url: "http://127.0.0.1:11436",
            AnalyzeTimeoutSeconds: 120));

        http.BaseAddress.ShouldBe(new Uri("http://127.0.0.1:11436"));
        http.Timeout.ShouldBeGreaterThan(TimeSpan.FromSeconds(120));
    }

    // Spec 4.7: the App hands its Windows pieces to the host; setup, settings and the phone block then use them.
    [Fact]
    public async Task The_apps_windows_pieces_reach_setup_settings_and_the_phone_block()
    {
        var autoStart = new FakeAutoStart();
        autoStart.SetEnabled(true);
        await using var app = ServerHost.Build(["--environment", "Testing"], services =>
        {
            // The real settings file, database and Ollama stay out of tests.
            services.AddSingleton<ISettingsStore>(new FixedSettings(new SlothBoxSettings()));
            services.AddSingleton<IDbContextFactory<SlothBoxDbContext>, NoDatabase>();
            services.AddSingleton<IOllamaStatus, FixedGemma>();
            services.AddSingleton<INativeDialogs>(new FakeDialogs { Answer = @"D:\slothbox-test\picked" });
            services.AddSingleton<IAutoStart>(autoStart);
            services.AddSingleton<IFirewallRules>(new FakeFirewall(PhoneLink.FirewallRuleName));
        });

        (await app.Services.GetRequiredService<SetupService>().PickFolderAsync(TestContext.Current.CancellationToken))
            .ShouldBe(new Outcome<PickFolderResultDto>.Done(new PickFolderResultDto(@"D:\slothbox-test\picked")));
        app.Services.GetRequiredService<SettingsService>().Get().StartAtLogin.ShouldBeTrue();
        app.Services.GetRequiredService<PhoneLink>().Get().FirewallRulePresent.ShouldBeTrue();
    }

    // Outside tests the host also runs the tray feed, and the feed reaches the App's tray.
    [Fact]
    public async Task Outside_tests_the_tray_feed_runs_and_updates_the_apps_tray()
    {
        var tray = new RecordingTray();
        await using var app = ServerHost.Build(["--environment", "Production"], services =>
        {
            // The real settings file and Ollama stay out of tests; the database is "offline".
            services.AddSingleton<ISettingsStore>(new FixedSettings(new SlothBoxSettings()));
            services.AddSingleton<IDbContextFactory<SlothBoxDbContext>, NoDatabase>();
            services.AddSingleton<IOllamaStatus, FixedGemma>();
            services.AddSingleton<ITrayNotifier>(tray);
        });
        var feed = app.Services.GetServices<IHostedService>().OfType<TrayFeed>().ShouldHaveSingleItem();
        var ct = TestContext.Current.CancellationToken;

        await feed.StartAsync(ct);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tray.LastStatus is null && DateTime.UtcNow < deadline)
            await Task.Delay(20, ct);
        tray.LastStatus.ShouldBe("Database offline");
        await feed.StopAsync(ct);
    }
}
