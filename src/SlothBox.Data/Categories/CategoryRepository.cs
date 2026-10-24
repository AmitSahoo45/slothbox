using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SlothBox.Core;

namespace SlothBox.Data.Categories;

public sealed class CategoryRepository(IDbContextFactory<SlothBoxDbContext> contexts) : ICategoryRepository
{
    public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
        return rows.ConvertAll(ToCategory);
    }

    public async Task<Category> SaveAsync(Category category, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        StoredCategory row;
        if (category.Id == 0)
            db.Categories.Add(row = new StoredCategory
            {
                Type = category.Type, IsSystem = false, CreatedAt = DateTimeOffset.UtcNow
            });
        else
            row = await db.Categories.SingleOrDefaultAsync(c => c.Id == category.Id, ct) ??
                throw new InvalidOperationException($"Category {category.Id} does not exist.");
        row.Name = category.Name;
        row.Description = category.Description;
        row.Folder = category.Folder;
        await db.SaveChangesAsync(ct);
        return ToCategory(row);
    }

    // A file of any status may point at it, and so may an action's `before` (Undo would put a file back into it).
    public async Task<bool> DeleteEmptyAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var key = id.ToString(CultureInfo.InvariantCulture);
        if (await db.Files.AnyAsync(f => f.CategoryId == id, ct) ||
            await db.Actions.FromSql($"SELECT * FROM actions WHERE before ->> 'categoryId' = {key}").AnyAsync(ct))
            return false;
        try
        {
            return await db.Categories.Where(c => c.Id == id && !c.IsSystem).ExecuteDeleteAsync(ct) > 0;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // A file was filed into it between the check and the delete.
            return false;
        }
    }

    private static Category ToCategory(StoredCategory c) =>
        new(c.Id, c.Type, c.Name, c.Description, c.Folder, c.IsSystem);
}
