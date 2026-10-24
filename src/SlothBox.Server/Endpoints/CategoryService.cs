using SlothBox.Core;
using SlothBox.Data.Categories;
using SlothBox.Server.Api;

namespace SlothBox.Server.Endpoints;

// The Categories page (spec 7.4.7, A22). Gemma reads the list on every call, so a change reaches the next file.
public sealed class CategoryService(ICategoryRepository categories, CategoryQueries queries)
{
    private const string BadFolder =
        "Use a folder name Windows accepts: no \\ / : * ? \" < > |, and no dot or space at the end.";

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
        StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct)
    {
        var counts = await queries.FileCountsAsync(ct);
        return [.. (await categories.ListAsync(ct)).Select(c => ToDto(c, counts.GetValueOrDefault(c.Id)))];
    }

    public async Task<Outcome<CategoryDto>> CreateAsync(CreateCategoryRequest request, CancellationToken ct)
    {
        if (request.Type == FileKind.Other)
            return new Outcome<CategoryDto>.Refused("The Other categories are fixed: those files are sorted by extension.");
        if (request.Type is not (FileKind.Receipt or FileKind.Screenshot or FileKind.Document))
            return new Outcome<CategoryDto>.Refused("Pick receipt, screenshot or document.");
        var name = (request.Name ?? "").Trim();
        var description = (request.Description ?? "").Trim();
        var folder = string.IsNullOrWhiteSpace(request.Folder) ? name : request.Folder.Trim();
        if (Invalid(name, description, folder) is { } invalid)
            return new Outcome<CategoryDto>.Refused(invalid);
        if (Clash(await categories.ListAsync(ct), request.Type, name, folder, 0) is { } clash)
            return new Outcome<CategoryDto>.Conflict(clash);
        var saved = await categories.SaveAsync(new Category(0, request.Type, name, description, folder, false), ct);
        return new Outcome<CategoryDto>.Done(ToDto(saved, 0));
    }

    // Renaming changes only the display name and what Gemma reads; the folder is fixed once files are in it.
    public async Task<Outcome<CategoryDto>> UpdateAsync(int id, UpdateCategoryRequest request, CancellationToken ct)
    {
        var all = await categories.ListAsync(ct);
        var category = all.SingleOrDefault(c => c.Id == id);
        if (category is null)
            return new Outcome<CategoryDto>.NotFound();
        if (category.IsSystem)
            return new Outcome<CategoryDto>.Conflict("Built-in categories can't be changed.");
        var name = (request.Name ?? "").Trim();
        var description = (request.Description ?? "").Trim();
        var folder = string.IsNullOrWhiteSpace(request.Folder) ? category.Folder : request.Folder.Trim();
        if (Invalid(name, description, folder) is { } invalid)
            return new Outcome<CategoryDto>.Refused(invalid);
        if (Clash(all, category.Type, name, folder, id) is { } clash)
            return new Outcome<CategoryDto>.Conflict(clash);
        var files = (await queries.FileCountsAsync(ct)).GetValueOrDefault(id);
        if (files > 0 && !string.Equals(folder, category.Folder, StringComparison.Ordinal))
            return new Outcome<CategoryDto>.Conflict("The folder name is fixed once files are in it.");
        var saved = await categories.SaveAsync(category with { Name = name, Description = description, Folder = folder },
            ct);
        return new Outcome<CategoryDto>.Done(ToDto(saved, files));
    }

    public async Task<Outcome<MutationResultDto>> DeleteAsync(int id, CancellationToken ct)
    {
        var category = (await categories.ListAsync(ct)).SingleOrDefault(c => c.Id == id);
        if (category is null)
            return new Outcome<MutationResultDto>.NotFound();
        if (category.IsSystem)
            return new Outcome<MutationResultDto>.Conflict("Built-in categories can't be removed.");
        return await categories.DeleteEmptyAsync(id, ct)
            ? new Outcome<MutationResultDto>.Done(new MutationResultDto(true, $"Removed {category.Name}."))
            : new Outcome<MutationResultDto>.Conflict("Move its files to another category first.");
    }

    // The name and description go into every Gemma call, so both are kept short.
    private static string? Invalid(string name, string description, string folder)
    {
        if (name.Length == 0)
            return "Give the category a name.";
        if (name.Length > 60)
            return "Keep the name to 60 characters.";
        if (description.Length > 300)
            return "Keep the description to 300 characters.";
        if (folder.Length > 60 || folder is "." or ".." || folder.Any(c => c < ' ' || "\\/:*?\"<>|".Contains(c)) ||
            folder.EndsWith('.') || folder.EndsWith(' ') || ReservedNames.Contains(folder.Split('.')[0].TrimEnd()))
            return BadFolder;
        return null;
    }

    // Names and folders are unique within a type, without regard to case: Gemma's answer is matched that way.
    private static string? Clash(IEnumerable<Category> all, FileKind type, string name, string folder, int exceptId)
    {
        var sameType = all.Where(c => c.Type == type && c.Id != exceptId).ToList();
        if (sameType.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"There is already a {type.ToString().ToLowerInvariant()} category called {name}.";
        return sameType.Any(c => string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase))
            ? $"Another {type.ToString().ToLowerInvariant()} category already files into {folder}."
            : null;
    }

    private static CategoryDto ToDto(Category c, int files) =>
        new(c.Id, c.Type, c.Name, c.Description, c.Folder, c.IsSystem, files);
}
