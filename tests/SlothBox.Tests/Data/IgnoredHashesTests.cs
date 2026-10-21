using SlothBox.Core;
using SlothBox.Data.Files;

namespace SlothBox.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class IgnoredHashesTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ignored_hashes_are_listed_once_each_and_can_be_allowed_again()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);
        var ignored = new IgnoredHashes(db);
        var at = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

        await ignored.AddAsync(new IgnoredHash("aaa", "undone", at), Ct);
        await ignored.AddAsync(new IgnoredHash("aaa", "undone", at.AddHours(1)), Ct);
        await ignored.AddAsync(new IgnoredHash("bbb", "undone", at.AddHours(2)), Ct);

        (await ignored.ListAsync(Ct)).ShouldBe(
            [new IgnoredHash("aaa", "undone", at), new IgnoredHash("bbb", "undone", at.AddHours(2))],
            ignoreOrder: true);
        await ignored.RemoveAsync("aaa", Ct);
        (await ignored.ContainsAsync("aaa", Ct)).ShouldBeFalse();
        (await ignored.ContainsAsync("bbb", Ct)).ShouldBeTrue();
    }
}
