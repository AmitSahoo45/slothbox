using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data;

// Spec 6.2 app_state: a few facts about this install, such as when first-run setup finished (setup_completed_at).
public sealed class AppStateStore(IDbContextFactory<SlothBoxDbContext> contexts) : IAppState
{
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.AppState.Where(state => state.Key == key).Select(state => state.Value).SingleOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO app_state (key, value) VALUES ({key}, {value})
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """, ct);
    }
}
