using System.Text.Json;
using SlothBox.Core;

namespace SlothBox.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_match_the_documented_first_run_configuration()
    {
        var settings = JsonSerializer.Deserialize<SlothBoxSettings>("{}")!;
        settings.OutputRoot.ShouldBe("");
        settings.Phone.ShouldBe(new PhoneSettings(true, 5081, "", 50));
        settings.Dashboard.ShouldBe(new DashboardSettings(5080));
        settings.Ollama.ShouldBe(new OllamaSettings(
            "http://127.0.0.1:11436", "gemma4:e4b", "embeddinggemma", "30m", 120, 30));
        settings.Database.ConnectionString.ShouldBe(
            "Host=127.0.0.1;Port=5433;Database=slothbox;Username=slothbox");
        settings.Pipeline.ShouldBe(new PipelineSettings(5, 3, 10, 80, 240, 1600, 6000, 20000, 2000, "INR"));
        settings.Laziness.ShouldBe(new LazinessSettings(30, 15, 15, 5));
    }

    [Fact]
    public void Json_round_trip_preserves_user_choices_and_never_contains_a_database_password()
    {
        var settings = new SlothBoxSettings
        {
            OutputRoot = @"D:\slothbox-test-output",
            Phone = new PhoneSettings(false, 5081, "synthetic-test-token", 12),
            Pipeline = new PipelineSettings(DefaultCurrency: "USD"),
            Laziness = new LazinessSettings(42, 20, 25, 8)
        };
        var json = JsonSerializer.Serialize(settings);
        JsonSerializer.Deserialize<SlothBoxSettings>(json).ShouldBe(settings);
        json.ShouldNotContain("Password", Case.Insensitive);
    }
}
