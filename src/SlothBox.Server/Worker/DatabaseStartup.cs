using Microsoft.EntityFrameworkCore;
using SlothBox.Data;

namespace SlothBox.Server.Worker;

// Spec 4.3 step 4: connect to Postgres and apply the migrations. The worker retries this while Postgres is down.
public interface IDatabaseStartup
{
    Task MigrateAsync(CancellationToken ct);
}

public sealed class DatabaseStartup(IDbContextFactory<SlothBoxDbContext> contexts) : IDatabaseStartup
{
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
