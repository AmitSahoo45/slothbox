using SlothBox.Data;

namespace SlothBox.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class AppStateTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_key_is_empty_until_set_and_a_second_set_replaces_its_value()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var state = new AppStateStore(db);

        (await state.GetAsync("setup_completed_at", Ct)).ShouldBeNull();
        await state.SetAsync("setup_completed_at", "2026-10-24T10:00:00.0000000+00:00", Ct);
        await state.SetAsync("setup_completed_at", "2026-10-24T11:00:00.0000000+00:00", Ct);

        (await state.GetAsync("setup_completed_at", Ct)).ShouldBe("2026-10-24T11:00:00.0000000+00:00");
    }
}
