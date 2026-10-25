using Microsoft.Extensions.DependencyInjection;
using SlothBox.Ai.Analyze;
using SlothBox.Ai.Embed;
using SlothBox.Core;
using SlothBox.Server;
using SlothBox.Server.Settings;
using SlothBox.Tests.Api;

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
}
