using SlothBox.Ai.Analyze;
using SlothBox.Core;

namespace SlothBox.Tests.Ai;

// The one real Gemma call of plan E7. It runs only when SLOTHBOX_OLLAMA_URL points at an Ollama with the model
// already pulled, so CI skips it. The receipt is synthetic.
public sealed class FileAnalyzerLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_real_call_returns_output_that_matches_the_schema()
    {
        var url = Environment.GetEnvironmentVariable("SLOTHBOX_OLLAMA_URL");
        Assert.SkipUnless(!string.IsNullOrEmpty(url), "Set SLOTHBOX_OLLAMA_URL to call a running Ollama.");
        using var http = new HttpClient { BaseAddress = new Uri(url!), Timeout = TimeSpan.FromMinutes(5) };
        var analyzer = new FileAnalyzer(http, new OllamaSettings(), new PipelineSettings());
        var request = new AnalysisRequest("payment.txt", "Downloads",
            new FileContent.Text("PAYMENT RECEIPT\nPaid to: City Grocers\nDate: 18 Oct 2026\nTotal: Rs. 1,250.00\n"),
            [
                new(1, FileKind.Receipt, "Groceries", "Supermarket and grocery bills", "Groceries", false),
                new(2, FileKind.Receipt, "Other", "", "Other", true),
                new(3, FileKind.Screenshot, "Other", "", "Other", true),
                new(4, FileKind.Document, "Other", "", "Other", true)
            ]);

        var result = await analyzer.AnalyzeAsync(request, TestContext.Current.CancellationToken);

        output.WriteLine(result.ToString());
        result.ShouldBeOfType<AnalysisResult.Success>();
    }
}
