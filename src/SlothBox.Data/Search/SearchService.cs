using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using SlothBox.Core;

namespace SlothBox.Data.Search;

// Spec 6.3: the top 50 keyword matches and the top 50 meaning matches, merged by reciprocal rank fusion (k = 60)
// in one query. It also fills in missing vectors, "Rebuild missing search data" (5.6).
public sealed class SearchService(IDbContextFactory<SlothBoxDbContext> contexts, IFileRepository files,
    IEmbedder embedder, ISettingsStore settings, ILogger<SearchService> logger, int maxHits = 20) : ISearchService
{
    // {filters} holds the optional filters, added to both branches.
    private const string HybridQuery = """
        WITH kw AS (
            SELECT id, row_number() OVER (ORDER BY rank DESC) AS r
            FROM (
                SELECT f.id, ts_rank_cd(f.search, q) AS rank
                FROM files f, websearch_to_tsquery('english', @query) AS q
                WHERE f.status = 'filed' AND f.search @@ q
                  AND EXISTS (SELECT 1 FROM actions a WHERE a.file_id = f.id AND a.status = 'done'){filters}
                ORDER BY rank DESC
                LIMIT 50
            ) k
        ),
        vec AS (
            SELECT id, row_number() OVER (ORDER BY dist) AS r
            FROM (
                SELECT f.id, f.embedding <=> @query_vector::vector AS dist
                FROM files f
                WHERE @with_meaning AND f.status = 'filed' AND f.embedding IS NOT NULL
                  AND EXISTS (SELECT 1 FROM actions a WHERE a.file_id = f.id AND a.status = 'done'){filters}
                ORDER BY f.embedding <=> @query_vector::vector
                LIMIT 50
            ) v
        )
        SELECT id,
               SUM(1.0 / (60 + r)) AS score,
               bool_or(src = 'kw')  AS matched_words,
               bool_or(src = 'vec') AS matched_meaning
        FROM (
            SELECT id, r, 'kw'  AS src FROM kw
            UNION ALL
            SELECT id, r, 'vec' AS src FROM vec
        ) merged
        GROUP BY id
        ORDER BY score DESC
        LIMIT @max_hits
        """;

    private int _rebuilding;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        var text = query.Query.Trim();
        if (text.Length == 0)
            return [];
        var vector = await EmbedQueryAsync(text, ct);

        var filters = new StringBuilder();
        var parameters = new List<NpgsqlParameter>
        {
            new("query", NpgsqlDbType.Text) { Value = text },
            new("query_vector", NpgsqlDbType.Text) { Value = vector is null ? DBNull.Value : VectorText(vector) },
            new("with_meaning", NpgsqlDbType.Boolean) { Value = vector is not null },
            new("max_hits", NpgsqlDbType.Integer) { Value = maxHits }
        };
        if (query.Type is { } type)
        {
            filters.Append(" AND f.type = @type");
            parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = DbValue.Of(type) });
        }
        if (query.CategoryId is { } categoryId)
        {
            filters.Append(" AND f.category_id = @category_id");
            parameters.Add(new NpgsqlParameter("category_id", NpgsqlDbType.Integer) { Value = categoryId });
        }
        // Whole local days: from the start of From up to the start of the day after To.
        if (query.From is { } from)
        {
            filters.Append(" AND f.file_date >= @from");
            parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = LocalDayStart(from) });
        }
        if (query.To is { } to)
        {
            filters.Append(" AND f.file_date < @to");
            parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz)
            {
                Value = LocalDayStart(to.AddDays(1))
            });
        }

        var rows = new List<(Guid Id, double Score, bool Words, bool Meaning)>();
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // pgvector's default ef_search (40) is below LIMIT 50: without it the meaning branch stops at 40.
            // HNSW applies the filters after it picks its candidates; the iterative scan keeps walking the index
            // until the branch is full, so a narrow filter does not empty it.
            await using (var settings = new NpgsqlCommand(
                             "SET LOCAL hnsw.ef_search = 100; SET LOCAL hnsw.iterative_scan = strict_order",
                             connection, transaction))
                await settings.ExecuteNonQueryAsync(ct);
            await using var command = new NpgsqlCommand(HybridQuery.Replace("{filters}", filters.ToString()),
                connection, transaction);
            command.Parameters.AddRange(parameters.ToArray());
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetGuid(0), (double)reader.GetDecimal(1), reader.GetBoolean(2),
                        reader.GetBoolean(3)));
            await transaction.CommitAsync(ct);
        }

        var hits = new List<SearchHit>(rows.Count);
        foreach (var row in rows)
            if (await files.GetAsync(row.Id, ct) is { } file)
                hits.Add(new SearchHit(file, row.Score, row.Words, row.Meaning));
        return hits;
    }

    // Filed files whose embedding failed (5.6): they are found by words only until they get a vector.
    public async Task<int> CountMissingAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await Missing(db).CountAsync(ct);
    }

    // Spec 7.4.8: reports queued work, not completion. The vectors are filled in the background; one rebuild runs
    // at a time, and asking again while it runs only reports what is still missing.
    public async Task<int> QueueRebuildAsync(CancellationToken stopping, CancellationToken ct)
    {
        var missing = await CountMissingAsync(ct);
        if (missing > 0 && Interlocked.CompareExchange(ref _rebuilding, 1, 0) == 0)
            _ = Task.Run(async () =>
            {
                try
                {
                    await RebuildMissingAsync(stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Rebuilding missing search data failed.");
                }
                finally
                {
                    Volatile.Write(ref _rebuilding, 0);
                }
            }, CancellationToken.None);
        return missing;
    }

    // One file at a time, with the pipeline's document text (step 6). It stops at the first embedding failure,
    // since EmbeddingGemma is then most likely offline. Returns how many files got a vector.
    public async Task<int> RebuildMissingAsync(CancellationToken ct)
    {
        List<(Guid Id, string Title, string Caption, string Text, int? CategoryId)> missing;
        Dictionary<int, string> categories;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            missing = (await Missing(db).AsNoTracking()
                    .Select(f => new { f.Id, f.Title, f.Caption, f.ExtractedText, f.CategoryId })
                    .ToListAsync(ct))
                .Select(f => (f.Id, f.Title, f.Caption, f.ExtractedText, f.CategoryId)).ToList();
        }

        var textChars = settings.Current.Pipeline.EmbedTextChars;
        var embedded = 0;
        foreach (var file in missing)
        {
            var category = file.CategoryId is { } id && categories.TryGetValue(id, out var name) ? name : "Other";
            // The same text as FilePipeline's step 6, so a rebuilt vector matches one made at filing time.
            var body = $"{category}. {file.Caption} {StoredText.Cut(file.Text, textChars)}".Trim();
            float[] vector;
            try
            {
                vector = await embedder.EmbedDocumentAsync(file.Title, body, ct);
            }
            catch (Exception e) when (e is TimeoutException or HttpRequestException or InvalidOperationException)
            {
                logger.LogWarning("Rebuilding search data stopped after {Count} files: {Error}", embedded, e.Message);
                break;
            }
            await files.SaveEmbeddingAsync(file.Id, vector, ct);
            embedded++;
        }
        return embedded;
    }

    private static IQueryable<StoredFile> Missing(SlothBoxDbContext db) =>
        db.Files.Where(f => f.Status == FileStatus.Filed && f.Embedding == null &&
            db.Actions.Any(a => a.FileId == f.Id && a.Status == ActionStatus.Done));

    // Whatever stops EmbeddingGemma (offline, model missing, a bad answer), the words branch still answers;
    // the banner reports the cause (spec 9). Only the caller's cancellation goes through.
    private async Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct)
    {
        try
        {
            return await embedder.EmbedQueryAsync(text, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // pgvector's text form, cast to vector in SQL.
    private static string VectorText(float[] vector) =>
        $"[{string.Join(",", vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}]";

    private static DateTimeOffset LocalDayStart(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
