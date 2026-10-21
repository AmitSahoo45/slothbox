using SlothBox.Core;
using SlothBox.Data.Categories;

namespace SlothBox.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class CategoryRepositoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_seed_categories_are_listed_in_id_order_with_their_folders()
    {
        await using var db = await postgres.CreateDatabaseAsync(Ct);

        var categories = await new CategoryRepository(db).ListAsync(Ct);

        categories.Count.ShouldBe(19);
        categories.Count(c => c.IsSystem).ShouldBe(7);
        categories.Select(c => c.Id).ShouldBe(Enumerable.Range(1, 19));
        categories.ShouldContain(new Category(3, FileKind.Receipt, "Bills and recharges",
            "Electricity, mobile, internet, rent, subscriptions", "Bills and recharges", false));
        categories.ShouldContain(new Category(16, FileKind.Other, "Installers", "Installers", "Installers", true));
    }
}
