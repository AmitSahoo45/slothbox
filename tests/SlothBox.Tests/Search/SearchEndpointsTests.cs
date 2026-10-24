using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Core;
using SlothBox.Data;
using SlothBox.Tests.Api;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Search;

[Collection(PostgresCollection.Name)]
public sealed class SearchEndpointsTests(PostgresFixture postgres, ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The test server over a fresh database, with EmbeddingGemma faked.
    private WebApplicationFactory<Server.Program> ServerOver(TestDatabase db) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDbContextFactory<SlothBoxDbContext>>(db);
            services.AddSingleton<IEmbedder>(new SearchServiceTests.FakeEmbedder());
        }));

    private static HttpClient DashboardClient(WebApplicationFactory<Server.Program> server)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5080"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-SlothBox", "1");
        return client;
    }

    [Fact]
    public async Task Search_returns_each_hit_with_how_it_matched()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var both = await SearchServiceTests.SeedAsync(db, "Jio recharge", SearchServiceTests.QueryVector,
            caption: "UPI payment to Jio");
        var meaning = await SearchServiceTests.SeedAsync(db, "Mobile top-up",
            SearchServiceTests.Vector((0, 1), (1, 0.5f)));
        await using var server = ServerOver(db);
        using var client = DashboardClient(server);

        using var response = await client.GetAsync("/api/search?q=jio%20recharge&type=document", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        var first = items[0].GetProperty("file");
        first.GetProperty("file").GetProperty("fileId").GetGuid().ShouldBe(both);
        first.GetProperty("file").GetProperty("title").GetString().ShouldBe("Jio recharge");
        first.GetProperty("caption").GetString().ShouldBe("UPI payment to Jio");
        first.GetProperty("history").GetArrayLength().ShouldBe(1);
        items[0].GetProperty("matchedWords").GetBoolean().ShouldBeTrue();
        items[0].GetProperty("matchedMeaning").GetBoolean().ShouldBeTrue();
        items[1].GetProperty("file").GetProperty("file").GetProperty("fileId").GetGuid().ShouldBe(meaning);
        items[1].GetProperty("matchedWords").GetBoolean().ShouldBeFalse();
        items[1].GetProperty("matchedMeaning").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_type_is_a_bad_request()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        await using var server = ServerOver(db);
        using var client = DashboardClient(server);

        using var response = await client.GetAsync("/api/search?q=jio&type=invoice", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Rebuild_reports_the_files_it_queued_and_fills_them_in()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        await SearchServiceTests.SeedAsync(db, "Jio recharge", null);
        await using var server = ServerOver(db);
        using var client = DashboardClient(server);

        using var response = await client.PostAsync("/api/search-data/rebuild", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("queued").GetInt32().ShouldBe(1);
        await SearchServiceTests.WaitUntilAsync(async () =>
        {
            await using var context = db.CreateDbContext();
            return !await context.Files.AnyAsync(f => f.Embedding == null, Ct);
        });
    }
}
