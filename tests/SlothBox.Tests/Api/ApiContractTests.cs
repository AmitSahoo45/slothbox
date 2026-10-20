using System.Text.Json.Nodes;

namespace SlothBox.Tests.Api;

public sealed class ApiContractTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task Served_openapi_equals_the_checked_in_contract_for_either_allowed_host()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SlothBox.slnx")))
            directory = directory.Parent;
        directory.ShouldNotBeNull();
        var contractPath = Path.Combine(directory.FullName, "contracts", "openapi.json");
        File.Exists(contractPath).ShouldBeTrue("The API contract must be included in the diff.");
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(contractPath, TestContext.Current.CancellationToken));
        foreach (var host in new[] { "127.0.0.1:5080", "localhost:5080" })
        {
            using var client = factory.DashboardClient();
            client.DefaultRequestHeaders.Host = host;
            var actual = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json",
                TestContext.Current.CancellationToken));
            JsonNode.DeepEquals(expected, actual).ShouldBeTrue($"Contract drift for {host}.");
        }
    }
}
