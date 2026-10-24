using Microsoft.Extensions.DependencyInjection;
using SlothBox.Ai.Analyze;
using SlothBox.Core;
using SlothBox.Server;
using SlothBox.Tests.Api;

namespace SlothBox.Tests;

public sealed class ServerHostTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public void Files_are_read_by_Gemmas_analyzer() =>
        factory.Services.GetRequiredService<IFileAnalyzer>().ShouldBeOfType<FileAnalyzer>();

    [Fact]
    public void The_Ollama_client_points_at_the_configured_address_and_outlasts_one_Gemma_attempt()
    {
        using var http = ServerHost.OllamaClient(new OllamaSettings(Url: "http://127.0.0.1:11436",
            AnalyzeTimeoutSeconds: 120));

        http.BaseAddress.ShouldBe(new Uri("http://127.0.0.1:11436"));
        http.Timeout.ShouldBeGreaterThan(TimeSpan.FromSeconds(120));
    }
}
