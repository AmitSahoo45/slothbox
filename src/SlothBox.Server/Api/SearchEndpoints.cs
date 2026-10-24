using SlothBox.Core;
using SlothBox.Core.Rules;
using SlothBox.Data.Search;

namespace SlothBox.Server.Api;

// The handlers behind GET /api/search and POST /api/search-data/rebuild; StubEndpoints maps and documents them.
internal static class SearchEndpoints
{
    // Spec 6.3 and 7.4.4: each hit carries the file's details and whether it matched by words, meaning or both.
    internal static async Task<IResult> Search(string? q, string? type, int? categoryId, DateOnly? from, DateOnly? to,
        SearchService search, ICategoryRepository categories, IWatchedFolderRepository folders,
        IReceiptRepository receipts, IActionLog actions, CancellationToken ct)
    {
        FileKind? kind = null;
        if (!string.IsNullOrEmpty(type))
        {
            kind = Enum.GetValues<FileKind>().Cast<FileKind?>()
                .FirstOrDefault(k => string.Equals(k.ToString(), type, StringComparison.OrdinalIgnoreCase));
            if (kind is null)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown type",
                    detail: "type is receipt, screenshot, document, other or unknown.");
        }

        var hits = await search.SearchAsync(new SearchQuery(q ?? "", kind, categoryId, from, to), ct);
        var categoryNames = (await categories.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var folderLabels = (await folders.ListAsync(ct)).ToDictionary(f => f.Id, f => f.Label);
        var items = new List<SearchHitDto>(hits.Count);
        foreach (var hit in hits)
        {
            var file = hit.File;
            var receipt = file.Type == FileKind.Receipt ? await receipts.GetAsync(file.Id, ct) : null;
            var history = await actions.GetForFileAsync(file.Id, ct);
            var activity = new ActivityItemDto
            {
                FileId = file.Id, At = file.UpdatedAt, Status = file.Status, Type = file.Type,
                Category = file.CategoryId is { } id && categoryNames.TryGetValue(id, out var name) ? name : "Other",
                Title = file.Title, NewName = Path.GetFileName(file.CurrentPath),
                Folder = Path.GetDirectoryName(file.CurrentPath) ?? "", Origin = file.Origin,
                FolderLabel = file.WatchedFolderId is { } folderId && folderLabels.TryGetValue(folderId, out var label)
                    ? label
                    : null,
                ThumbUrl = FileGroups.Of(file.CurrentPath) == FileGroup.Image ? $"/api/files/{file.Id}/thumb" : null,
                // Hits are filed files whose filing finished, which Undo can reverse (6.6); the undo still checks.
                CanUndo = true
            };
            var details = new FileDetailsDto(activity, file.OriginalName, file.OriginalPath, file.CurrentPath,
                file.Caption, file.SizeBytes, file.CategoryId, file.ReviewReason,
                receipt is null
                    ? null
                    : new ReceiptDto(receipt.Vendor, receipt.Date, receipt.Amount, receipt.Currency,
                        receipt.PaymentMethod),
                history.Select(a => new ActionDto(a.Id, a.Kind, a.FromPath, a.ToPath, a.Status,
                    a.CompletedAt ?? a.CreatedAt, a.Error)).ToList());
            items.Add(new SearchHitDto(details, hit.Score, hit.MatchedWords, hit.MatchedMeaning));
        }
        return Results.Ok(new SearchResultDto(items));
    }

    // Spec 5.6 and 7.4.8: re-embeds filed files that have no vector, in the background, and reports how many.
    internal static async Task<IResult> Rebuild(SearchService search, IHostApplicationLifetime lifetime,
        CancellationToken ct) =>
        Results.Ok(new RebuildResultDto(await search.QueueRebuildAsync(lifetime.ApplicationStopping, ct)));
}
