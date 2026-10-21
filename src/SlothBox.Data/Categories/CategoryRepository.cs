using Microsoft.EntityFrameworkCore;
using SlothBox.Core;

namespace SlothBox.Data.Categories;

public sealed class CategoryRepository(IDbContextFactory<SlothBoxDbContext> contexts) : ICategoryRepository
{
    public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
        return rows.ConvertAll(c => new Category(c.Id, c.Type, c.Name, c.Description, c.Folder, c.IsSystem));
    }

    // The editing rules (A22) arrive with the categories API (E16).
    public Task<Category> SaveAsync(Category category, CancellationToken ct) =>
        throw new NotSupportedException("Category editing arrives with the categories API.");

    public Task DeleteEmptyAsync(int id, CancellationToken ct) =>
        throw new NotSupportedException("Category editing arrives with the categories API.");
}
