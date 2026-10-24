using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using SlothBox.Data;
using Testcontainers.PostgreSql;

namespace SlothBox.Tests.Data;

public class MigrationTests
{
    [Fact]
    public async Task Migration_creates_the_complete_schema_indexes_and_exact_seed_without_destructive_reapply()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<SlothBoxDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=slothbox_test", o => o.UseVector()).Options;
        await using (var model = new SlothBoxDbContext(options))
        {
            model.Database.GetMigrations().ShouldNotBeEmpty();
        }

        await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg17")
            .WithName($"slothbox-test-migration-{Guid.NewGuid():N}")
            .WithDatabase("slothbox_test")
            .WithUsername("slothbox_test")
            .WithPassword("synthetic-migration-test-password")
            .WithTmpfsMount("/var/lib/postgresql/data")
            .Build();
        await postgres.StartAsync(ct);
        options = new DbContextOptionsBuilder<SlothBoxDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), o => o.UseVector()).Options;
        await using var db = new SlothBoxDbContext(options);
        await db.Database.MigrateAsync(ct);
        await db.Database.MigrateAsync(ct);
        (await db.Database.GetAppliedMigrationsAsync(ct)).ShouldBe(["20261020140119_Initial",
            "20261024131724_FinishedPaths"]);
        (await db.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();
        db.Database.HasPendingModelChanges().ShouldBeFalse();

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(ct);
        async Task<string> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToString(await command.ExecuteScalarAsync(ct),
                System.Globalization.CultureInfo.InvariantCulture)!;
        }

        (await Scalar("SELECT count(*) FROM categories")).ShouldBe("19");
        (await Scalar("SELECT count(*) FROM categories WHERE is_system")).ShouldBe("7");
        (await Scalar("SELECT count(*) FROM categories WHERE name = 'Other' AND is_system")).ShouldBe("3");
        (await Scalar("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('categories','watched_folders','folder_baseline','finished_paths','files','receipts','actions','jobs','ignored_hashes','app_state')")).ShouldBe("10");
        (await Scalar("SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_files_embedding'"))
            .ShouldContain("USING hnsw (embedding vector_cosine_ops)");
        (await Scalar("SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_files_search'"))
            .ShouldContain("USING gin (search)");
        var jobsIndex = await Scalar("SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_jobs_path_active'");
        jobsIndex.ShouldContain("UNIQUE");
        jobsIndex.ShouldContain("queued");
        jobsIndex.ShouldContain("processing");
        (await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name = 'actions' AND column_name = 'before'")).ShouldBe("jsonb");
        (await Scalar("SELECT is_generated FROM information_schema.columns WHERE table_name = 'files' AND column_name = 'search'")).ShouldBe("ALWAYS");
        (await Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'files'::regclass AND attname = 'embedding'")).ShouldBe("vector(768)");
        (await Scalar("SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns WHERE table_name = 'receipts' AND column_name = 'amount'")).ShouldBe("12,2");

        await using var insert = new NpgsqlCommand("""
            INSERT INTO jobs(path, origin, status, attempts, created_at, updated_at)
            VALUES ('D:\slothbox-test\sample.pdf', 'folder', 'queued', 0, now(), now())
            """, connection);
        await insert.ExecuteNonQueryAsync(ct);
        var conflict = await Should.ThrowAsync<PostgresException>(async () => await insert.ExecuteNonQueryAsync(ct));
        conflict.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        await using var finish = new NpgsqlCommand("UPDATE jobs SET status = 'done'", connection);
        await finish.ExecuteNonQueryAsync(ct);
        await insert.ExecuteNonQueryAsync(ct);
        (await Scalar("SELECT count(*) FROM jobs")).ShouldBe("2");
    }
}
