using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SlothBox.Core;
using SlothBox.Data;
using SlothBox.Data.Files;
using SlothBox.Data.Search;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Search;

[Collection(PostgresCollection.Name)]
public sealed class SearchServiceTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Query and document vectors: unit-ish vectors along a few axes, so cosine distances are easy to order.
    internal static float[] Vector(params (int Axis, float Value)[] parts)
    {
        var vector = new float[768];
        foreach (var (axis, value) in parts)
            vector[axis] = value;
        return vector;
    }

    internal static readonly float[] QueryVector = Vector((0, 1));

    // Stands in for EmbeddingGemma: every query embeds to QueryVector; documents get QueryVector too.
    internal sealed class FakeEmbedder(Exception? failure = null) : IEmbedder
    {
        public List<(string Title, string Body)> Documents { get; } = [];

        public Task<float[]> EmbedDocumentAsync(string title, string body, CancellationToken ct)
        {
            if (failure is not null)
                return Task.FromException<float[]>(failure);
            lock (Documents)
                Documents.Add((title, body));
            return Task.FromResult(QueryVector);
        }

        public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) =>
            failure is null ? Task.FromResult(QueryVector) : Task.FromException<float[]>(failure);
    }

    private sealed class Settings : ISettingsStore
    {
        public SlothBoxSettings Current { get; } = new();

        public SlothBoxSettings Update(Func<SlothBoxSettings, SlothBoxSettings> change) =>
            throw new NotSupportedException();
    }

    // A synthetic file row; a done action marks its filing as finished (spec 6.3 counts only those).
    internal static async Task<Guid> SeedAsync(TestDatabase db, string title, float[]? vector,
        FileStatus status = FileStatus.Filed, bool finished = true, FileKind type = FileKind.Document,
        int? categoryId = null, DateTimeOffset? fileDate = null, string caption = "", string text = "")
    {
        await using var context = db.CreateDbContext();
        var id = Add(context, title, vector, status, finished, type, categoryId, fileDate, caption, text);
        await context.SaveChangesAsync(Ct);
        return id;
    }

    // Adds the rows without saving, so a test can seed many files in one save.
    private static Guid Add(SlothBoxDbContext context, string title, float[]? vector,
        FileStatus status = FileStatus.Filed, bool finished = true, FileKind type = FileKind.Document,
        int? categoryId = null, DateTimeOffset? fileDate = null, string caption = "", string text = "")
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        context.Files.Add(new StoredFile
        {
            Id = id,
            Sha256 = $"synthetic-{id:N}",
            Origin = FileOrigin.Folder,
            OriginalName = "synthetic.pdf",
            OriginalPath = $@"D:\slothbox-test\watched\{id:N}.pdf",
            CurrentPath = $@"D:\slothbox-test\out\{id:N}.pdf",
            Type = type,
            CategoryId = categoryId,
            Title = title,
            Caption = caption,
            ExtractedText = text,
            Status = status,
            ReviewReason = status == FileStatus.Review ? ReviewReason.UnknownType : null,
            FileDate = (fileDate ?? now).ToUniversalTime(),
            Embedding = vector is null ? null : new Pgvector.Vector(vector),
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Actions.Add(new StoredAction
        {
            FileId = id,
            Kind = ActionKind.Move,
            FromPath = $@"D:\slothbox-test\watched\{id:N}.pdf",
            ToPath = $@"D:\slothbox-test\out\{id:N}.pdf",
            Sha256 = $"synthetic-{id:N}",
            Status = finished ? ActionStatus.Done : ActionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = finished ? now : null
        });
        return id;
    }

    internal static SearchService Service(TestDatabase db, IEmbedder embedder, int maxHits = 20) =>
        new(db, new FileRepository(db), embedder, new Settings(), NullLogger<SearchService>.Instance, maxHits);

    private static async Task<int> CategoryAsync(TestDatabase db, FileKind type)
    {
        await using var context = db.CreateDbContext();
        return await context.Categories.Where(c => c.Type == type && !c.IsSystem).Select(c => c.Id).FirstAsync(Ct);
    }

    [Fact]
    public async Task A_file_matching_only_by_meaning_is_found_and_one_matching_both_ranks_first()
    {
        // Case 15.
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var both = await SeedAsync(db, "Jio recharge", Vector((0, 1), (1, 0.3f)), caption: "UPI payment to Jio");
        var meaning = await SeedAsync(db, "Mobile top-up for a prepaid SIM", Vector((0, 1), (1, 0.1f)));
        var words = await SeedAsync(db, "Jio recharge receipt copy", null);

        var hits = await Service(db, new FakeEmbedder()).SearchAsync(new SearchQuery("jio recharge"), Ct);

        hits[0].File.Id.ShouldBe(both);
        (hits[0].MatchedWords, hits[0].MatchedMeaning).ShouldBe((true, true));
        var meaningOnly = hits.Single(h => h.File.Id == meaning);
        (meaningOnly.MatchedWords, meaningOnly.MatchedMeaning).ShouldBe((false, true));
        var wordsOnly = hits.Single(h => h.File.Id == words);
        (wordsOnly.MatchedWords, wordsOnly.MatchedMeaning).ShouldBe((true, false));
        hits[0].Score.ShouldBeGreaterThan(meaningOnly.Score);
    }

    [Fact]
    public async Task With_60_filed_files_the_meaning_branch_returns_50()
    {
        // Case 21. The planner must walk the HNSW index for the default ef_search (40) to cut the branch short.
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        await ForceIndexScansAsync(db);
        await using (var context = db.CreateDbContext())
        {
            for (var i = 0; i < 60; i++)
                Add(context, $"Synthetic file {i}", Vector((0, 1), (1 + i, 0.5f)));
            await context.SaveChangesAsync(Ct);
        }

        var hits = await Service(db, new FakeEmbedder(), maxHits: 100)
            .SearchAsync(new SearchQuery("zzzz nothing matches these words"), Ct);

        hits.Count.ShouldBe(50);
        hits.ShouldAllBe(h => h.MatchedMeaning && !h.MatchedWords);
    }

    [Fact]
    public async Task A_filter_still_finds_meaning_matches_beyond_the_nearest_rows()
    {
        // HNSW applies filters after it picks its nearest candidates; here every receipt lies beyond them.
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        await ForceIndexScansAsync(db);
        await using (var context = db.CreateDbContext())
        {
            for (var i = 0; i < 130; i++)
                Add(context, $"Synthetic document {i}", Vector((0, 1), (1 + i, 0.2f)));
            // A tight cluster of receipts, each farther from the query than every document but still linked into
            // the index graph (vectors orthogonal to everything get pruned out of it).
            for (var i = 0; i < 20; i++)
                Add(context, $"Synthetic receipt {i}", Vector((0, 1), (1 + i, 0.2f), (300, 0.8f)),
                    type: FileKind.Receipt);
            await context.SaveChangesAsync(Ct);
        }

        var hits = await Service(db, new FakeEmbedder(), maxHits: 100)
            .SearchAsync(new SearchQuery("zzzz nothing matches these words", FileKind.Receipt), Ct);

        // HNSW is approximate and its graph is random, so a run may miss a receipt or two; without the iterative
        // scan the branch returns none of them.
        hits.Count.ShouldBeInRange(15, 20);
        hits.ShouldAllBe(h => h.File.Type == FileKind.Receipt && h.MatchedMeaning);
    }

    [Fact]
    public async Task Any_embedding_failure_still_finds_words()
    {
        // OllamaSharp reports an Ollama error answer, such as a missing model, with its own exception type.
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var words = await SeedAsync(db, "Jio recharge", QueryVector);

        var hits = await Service(db, new FakeEmbedder(new OllamaSharp.Models.Exceptions.OllamaException(
            "model \"embeddinggemma\" not found"))).SearchAsync(new SearchQuery("jio recharge"), Ct);

        hits.ShouldHaveSingleItem().File.Id.ShouldBe(words);
    }

    [Fact]
    public async Task Only_filed_files_whose_filing_finished_are_found()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var filed = await SeedAsync(db, "Jio recharge", QueryVector);
        await SeedAsync(db, "Jio recharge in review", QueryVector, FileStatus.Review);
        await SeedAsync(db, "Jio recharge undone", QueryVector, FileStatus.Undone);
        await SeedAsync(db, "Jio recharge never finished", QueryVector, finished: false);

        var hits = await Service(db, new FakeEmbedder()).SearchAsync(new SearchQuery("jio recharge"), Ct);

        hits.Select(h => h.File.Id).ShouldBe([filed]);
    }

    [Fact]
    public async Task Type_category_and_date_filters_apply_to_words_and_meaning()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var bills = await CategoryAsync(db, FileKind.Receipt);
        var october = LocalNoon(new DateOnly(2026, 10, 15));
        var match = await SeedAsync(db, "Jio recharge", QueryVector, type: FileKind.Receipt, categoryId: bills,
            fileDate: october);
        await SeedAsync(db, "Jio recharge", QueryVector, type: FileKind.Document, fileDate: october);
        await SeedAsync(db, "Jio recharge", QueryVector, type: FileKind.Receipt, fileDate: october);
        await SeedAsync(db, "Jio recharge", QueryVector, type: FileKind.Receipt, categoryId: bills,
            fileDate: LocalNoon(new DateOnly(2026, 9, 30)));
        await SeedAsync(db, "Jio recharge", QueryVector, type: FileKind.Receipt, categoryId: bills,
            fileDate: LocalNoon(new DateOnly(2026, 10, 21)));

        var hits = await Service(db, new FakeEmbedder()).SearchAsync(new SearchQuery("jio recharge",
            FileKind.Receipt, bills, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20)), Ct);

        var hit = hits.ShouldHaveSingleItem();
        hit.File.Id.ShouldBe(match);
        (hit.MatchedWords, hit.MatchedMeaning).ShouldBe((true, true));
    }

    [Fact]
    public async Task The_date_range_covers_whole_local_days()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var day = new DateOnly(2026, 10, 20);
        var morning = await SeedAsync(db, "Jio recharge", QueryVector, fileDate: LocalTime(day, new TimeOnly(0, 5)));
        var night = await SeedAsync(db, "Jio recharge", QueryVector, fileDate: LocalTime(day, new TimeOnly(23, 55)));

        var hits = await Service(db, new FakeEmbedder()).SearchAsync(
            new SearchQuery("jio recharge", From: day, To: day), Ct);

        hits.Select(h => h.File.Id).ShouldBe([morning, night], ignoreOrder: true);
    }

    [Fact]
    public async Task Without_the_embedder_search_still_finds_words()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var words = await SeedAsync(db, "Jio recharge", QueryVector);
        await SeedAsync(db, "Mobile top-up", QueryVector);

        var hits = await Service(db, new FakeEmbedder(new HttpRequestException("Ollama is not running")))
            .SearchAsync(new SearchQuery("jio recharge"), Ct);

        var hit = hits.ShouldHaveSingleItem();
        hit.File.Id.ShouldBe(words);
        (hit.MatchedWords, hit.MatchedMeaning).ShouldBe((true, false));
    }

    [Fact]
    public async Task A_blank_query_finds_nothing()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        await SeedAsync(db, "Jio recharge", QueryVector);

        (await Service(db, new FakeEmbedder()).SearchAsync(new SearchQuery("   "), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Rebuild_embeds_filed_files_without_a_vector_with_the_pipeline_text()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var bills = await CategoryAsync(db, FileKind.Receipt);
        string category;
        await using (var context = db.CreateDbContext())
            category = (await context.Categories.SingleAsync(c => c.Id == bills, Ct)).Name;
        var missing = await SeedAsync(db, "Jio recharge", null, type: FileKind.Receipt, categoryId: bills,
            caption: "UPI payment of 599 INR to Jio", text: new string('x', 2100));
        await SeedAsync(db, "Already searchable", QueryVector);
        await SeedAsync(db, "Waiting in review", null, FileStatus.Review);
        await SeedAsync(db, "Never finished", null, finished: false);
        var embedder = new FakeEmbedder();
        var search = Service(db, embedder);

        (await search.CountMissingAsync(Ct)).ShouldBe(1);
        (await search.RebuildMissingAsync(Ct)).ShouldBe(1);

        embedder.Documents.ShouldHaveSingleItem()
            .ShouldBe(("Jio recharge", $"{category}. UPI payment of 599 INR to Jio {new string('x', 2000)}"));
        (await search.CountMissingAsync(Ct)).ShouldBe(0);
        var hit = (await search.SearchAsync(new SearchQuery("zzzz"), Ct)).Single(h => h.File.Id == missing);
        hit.MatchedMeaning.ShouldBeTrue();
    }

    [Fact]
    public async Task Rebuild_stops_at_the_first_embedding_failure()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        await SeedAsync(db, "Jio recharge", null);
        await SeedAsync(db, "Electricity bill", null);
        var search = Service(db, new FakeEmbedder(new HttpRequestException("Ollama is not running")));

        (await search.RebuildMissingAsync(Ct)).ShouldBe(0);
        (await search.CountMissingAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Queueing_a_rebuild_reports_the_missing_files_and_fills_them_in_the_background()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        await SeedAsync(db, "Jio recharge", null);
        await SeedAsync(db, "Electricity bill", null);
        var search = Service(db, new FakeEmbedder());

        (await search.QueueRebuildAsync(Ct, Ct)).ShouldBe(2);

        await WaitUntilAsync(async () => await search.CountMissingAsync(Ct) == 0);
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await done())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "The background rebuild did not finish.");
            await Task.Delay(50, Ct);
        }
    }

    // A small table would otherwise be scanned in full, which hides how the HNSW index cuts the meaning branch.
    // New sessions on this test database pick the settings up; the database is dropped after the test.
    private static async Task ForceIndexScansAsync(TestDatabase db)
    {
        await using var context = db.CreateDbContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            ALTER DATABASE "{connection.Database}" SET enable_seqscan = off;
            ALTER DATABASE "{connection.Database}" SET enable_sort = off;
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static DateTimeOffset LocalNoon(DateOnly day) => LocalTime(day, new TimeOnly(12, 0));

    private static DateTimeOffset LocalTime(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
