using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using SlothBox.Data;
using Testcontainers.PostgreSql;

namespace SlothBox.Tests.Data;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

// One container per test run; every test gets a fresh database copied from a migrated template.
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Template = "slothbox_template";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg17")
        .WithName($"slothbox-test-db-{Guid.NewGuid():N}")
        .WithDatabase(Template)
        .WithUsername("slothbox_test")
        .WithPassword("synthetic-test-password")
        .WithTmpfsMount("/var/lib/postgresql/data")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = new SlothBoxDbContext(Options(ConnectionString(Template)));
        await db.Database.MigrateAsync();
    }

    public async Task<TestDatabase> CreateDatabaseAsync(CancellationToken ct)
    {
        var name = $"test_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE {name} TEMPLATE {Template}", ct);
        return new TestDatabase(this, name, ConnectionString(name));
    }

    internal Task DropDatabaseAsync(string name) =>
        ExecuteAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", CancellationToken.None);

    internal static DbContextOptions<SlothBoxDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<SlothBoxDbContext>().UseNpgsql(connectionString, o => o.UseVector()).Options;

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    // No pooling: an idle pooled connection would block the template copy, and pools would pile up per database.
    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Pooling = false
        }.ConnectionString;
}

public sealed class TestDatabase(PostgresFixture fixture, string name, string connectionString)
    : IDbContextFactory<SlothBoxDbContext>, IAsyncDisposable
{
    public SlothBoxDbContext CreateDbContext() => new(PostgresFixture.Options(connectionString));

    public ValueTask DisposeAsync() => new(fixture.DropDatabaseAsync(name));
}
