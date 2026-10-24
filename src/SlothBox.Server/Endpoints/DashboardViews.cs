using SlothBox.Core;
using SlothBox.Core.Rules;
using SlothBox.Data.Dashboard;
using SlothBox.Server.Api;

namespace SlothBox.Server.Endpoints;

// Turns the dashboard's rows into the API's DTOs (spec 8.2, 8.3).
public static class DashboardViews
{
    public static ActivityItemDto Activity(FileView view) => new()
    {
        FileId = view.File.Id,
        At = view.At,
        Status = view.File.Status,
        Type = view.File.Type,
        Category = view.Category ?? "",
        Title = view.File.Title,
        NewName = Path.GetFileName(view.File.CurrentPath),
        Folder = Path.GetDirectoryName(view.File.CurrentPath) ?? "",
        Origin = view.File.Origin,
        FolderLabel = view.FolderLabel,
        ThumbUrl = ThumbUrl(view.File.Id, view.File.CurrentPath),
        CanUndo = view.CanUndo
    };

    public static FileRefiledEvent Refiled(ActivityItemDto item) => new()
    {
        FileId = item.FileId, At = item.At, Status = item.Status, Type = item.Type, Category = item.Category,
        Title = item.Title, NewName = item.NewName, Folder = item.Folder, Origin = item.Origin,
        FolderLabel = item.FolderLabel, ThumbUrl = item.ThumbUrl, CanUndo = item.CanUndo
    };

    // Images only; any other file shows its type icon (spec 7.4.2).
    public static string? ThumbUrl(Guid fileId, string path) =>
        FileGroups.Of(path) == FileGroup.Image ? $"/api/files/{fileId}/thumb" : null;

    // The drawer's details of each file, read together.
    public static async Task<IReadOnlyList<FileDetailsDto>> DetailsAsync(IReadOnlyList<FileView> views,
        DashboardQueries queries, CancellationToken ct)
    {
        var extras = await queries.ExtrasAsync(views.Select(view => view.File.Id).ToList(), ct);
        return views.Select(view => Details(view, extras[view.File.Id])).ToList();
    }

    private static FileDetailsDto Details(FileView view, FileExtras extras)
    {
        var file = view.File;
        var receipt = extras.Receipt;
        return new FileDetailsDto(Activity(view), file.OriginalName, file.OriginalPath, file.CurrentPath,
            file.Caption, file.SizeBytes, file.CategoryId, file.ReviewReason,
            receipt is null
                ? null
                : new ReceiptDto(receipt.Vendor, receipt.Date, receipt.Amount, receipt.Currency, receipt.PaymentMethod),
            extras.History.Select(action => new ActionDto(action.Id, action.Kind, action.FromPath, action.ToPath,
                action.Status, action.CompletedAt ?? action.CreatedAt, action.Error)).ToList());
    }
}
