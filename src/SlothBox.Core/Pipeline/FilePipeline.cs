using SlothBox.Core.Gate;
using SlothBox.Core.Rules;

namespace SlothBox.Core.Pipeline;

// Spec 5.1 steps 2 to 6 for one job: gate, read, ask Gemma, name and move, index.
public sealed class FilePipeline(
    IFileGate gate,
    IContentReader reader,
    IFileAnalyzer analyzer,
    INameBuilder names,
    ISafeMover mover,
    IEmbedder embedder,
    IFileRepository files,
    IReceiptRepository receipts,
    ICategoryRepository categories,
    IWatchedFolderRepository folders,
    ISettingsStore settings,
    Func<string, bool>? isTaken = null) : IFilePipeline
{
    private const string PhoneUpload = "phone upload";

    private readonly Func<string, bool> _isTaken = isTaken ?? TempFiles.IsTaken;

    // What a file becomes: its row, its receipt row and where it goes.
    private sealed record Filing(FileKind Type, int? CategoryId, string Category, string Title, string Caption,
        string Text, ReceiptData? Receipt, FileStatus Status, ReviewReason? Reason, Guid? DuplicateOf,
        string Target, WatchedFolder? Folder);

    public async Task<PipelineResult> RunAsync(Job job, CancellationToken ct)
    {
        var incoming = job.File with { Path = Path.GetFullPath(job.File.Path) };
        var folder = await FolderAsync(incoming, ct);
        // A job from a folder the user has since removed or switched off is left alone: without the folder's mode,
        // a copy-only file could be moved out of its cloud folder.
        if (incoming.Origin == FileOrigin.Folder && folder is null)
            return new PipelineResult.Skipped(GateSkipReason.FolderRemoved);
        if (folder is { Enabled: false })
            return new PipelineResult.Skipped(GateSkipReason.FolderDisabled);
        return await gate.CheckAsync(incoming, ct) switch
        {
            GateResult.Skip skip => new PipelineResult.Skipped(skip.Reason),
            GateResult.NotReady => new PipelineResult.NotReady(),
            GateResult.Duplicate duplicate => await DuplicateAsync(incoming, folder, duplicate, ct),
            GateResult.Ready ready => await ReadAndFileAsync(incoming, folder, ready.Sha256, ct),
            var other => throw new InvalidOperationException($"Unexpected gate result {other}.")
        };
    }

    private async Task<PipelineResult> ReadAndFileAsync(IncomingFile incoming, WatchedFolder? folder, string sha256,
        CancellationToken ct)
    {
        var path = incoming.Path;
        var pipeline = settings.Current.Pipeline;
        var route = Routing.For(path);
        var categoryList = await categories.ListAsync(ct);

        // Step 3. Files Gemma never sees are not read at all (spec 5.3).
        var content = route.AskGemma || route.Kind is null
            ? await reader.ReadAsync(path, ct)
            : new FileContent.NameOnly();
        var text = content is FileContent.Text document ? document.Value : "";
        Analysis analysis;
        ReviewReason? reason;
        if (content is FileContent.Unsupported unsupported)
        {
            analysis = new Analysis(FileKind.Unknown, "", "", "", "", null);
            reason = unsupported.Reason;
        }
        else if (!route.AskGemma)
        {
            // Installers, archives, media and the rest keep their name and go under Other.
            analysis = new Analysis(FileKind.Other, route.OtherCategory!, Path.GetFileNameWithoutExtension(path), "",
                "", null);
            reason = null;
        }
        else
        {
            // Step 4. Gemma sees the first GemmaTextChars of a document; images go whole.
            var forGemma = content is FileContent.Text whole
                ? new FileContent.Text(StoredText.Cut(whole.Value, pipeline.GemmaTextChars))
                : content;
            var label = folder?.Label ?? (incoming.Origin == FileOrigin.Phone ? PhoneUpload : "");
            AnalysisResult result;
            try
            {
                result = await analyzer.AnalyzeAsync(
                    new AnalysisRequest(Path.GetFileName(path), label, forGemma, categoryList), ct);
            }
            catch (HttpRequestException e) when (e.StatusCode is null)
            {
                // Ollama, or Docker, is not running: nothing is written and the job waits (spec 9).
                return new PipelineResult.Offline(e.Message);
            }
            catch (HttpRequestException)
            {
                // Ollama answered, with an error for this file: it goes to Needs review instead of holding the queue.
                result = new AnalysisResult.NeedsReview(ReviewReason.BadOutput);
            }
            var validation = ReviewRules.Check(result, categoryList, pipeline.DefaultCurrency, route.Kind);
            analysis = validation.Analysis ?? new Analysis(FileKind.Unknown, "", "", "", "", null);
            reason = validation.Reason;
            // For images the searchable text is what Gemma could read.
            if (content is FileContent.Image)
                text = analysis.Text;
        }
        text = StoredText.Cut(StoredText.Clean(text), pipeline.StoredTextChars);

        // Step 5. A review item keeps its name and waits in Needs review (spec 5.2); anything else is named by
        // its fields (5.5) and goes under its category.
        var outputRoot = settings.Current.OutputRoot;
        var info = new FileInfo(path);
        var category = reason is null ? CategoryFor(analysis, categoryList) : null;
        string targetFolder, fileName;
        if (category is null)
        {
            targetFolder = Path.Combine(outputRoot, "Needs review");
            fileName = info.Name;
        }
        else
        {
            targetFolder = names.BuildTargetFolder(outputRoot, analysis.Type, category.Folder);
            fileName = names.BuildFileName(analysis,
                new SourceFileInfo(path, info.Name, info.Extension, info.CreationTime, info.Length, outputRoot),
                targetFolder);
        }
        fileName = names.ResolveClash(targetFolder, fileName, _isTaken);
        var filing = new Filing(analysis.Type, category?.Id, category?.Name ?? "", analysis.Title, analysis.Caption,
            text, analysis.Type == FileKind.Receipt ? analysis.Receipt : null,
            reason is null ? FileStatus.Filed : FileStatus.Review, reason, null,
            Path.Combine(targetFolder, fileName), folder);
        return await FileAsync(incoming, sha256, filing, ct);
    }

    // A second copy of filed content goes to Duplicates under its own name, pointing at the file it repeats.
    private async Task<PipelineResult> DuplicateAsync(IncomingFile incoming, WatchedFolder? folder,
        GateResult.Duplicate duplicate, CancellationToken ct)
    {
        var existing = await files.GetAsync(duplicate.ExistingFileId, ct);
        var category = existing?.CategoryId is { } id
            ? (await categories.ListAsync(ct)).FirstOrDefault(c => c.Id == id)?.Name ?? ""
            : "";
        var targetFolder = Path.Combine(settings.Current.OutputRoot, "Duplicates");
        var fileName = names.ResolveClash(targetFolder, Path.GetFileName(incoming.Path), _isTaken);
        var filing = new Filing(existing?.Type ?? FileKind.Other, existing?.CategoryId, category,
            existing?.Title ?? "", existing?.Caption ?? "", "", null, FileStatus.Duplicate, null,
            duplicate.ExistingFileId, Path.Combine(targetFolder, fileName), folder);
        return await FileAsync(incoming, duplicate.Sha256, filing, ct);
    }

    private async Task<PipelineResult> FileAsync(IncomingFile incoming, string sha256, Filing filing,
        CancellationToken ct)
    {
        var path = incoming.Path;
        var info = new FileInfo(path);
        var now = DateTimeOffset.UtcNow;
        // The row is written before the move because the action refers to it; a retry after a failed first filing
        // reuses the unfinished row (spec 6.2).
        var earlier = await files.FindUnfinishedAsync(sha256, path, ct);
        var record = new FileRecord(earlier?.Id ?? Guid.NewGuid(), sha256, incoming.Origin, incoming.WatchedFolderId,
            info.Name, path, path, filing.Type, filing.CategoryId, filing.Title, filing.Caption, filing.Text,
            info.CreationTimeUtc, info.Length, filing.Status, filing.Reason, filing.DuplicateOf,
            earlier?.CreatedAt ?? now, now);
        await files.SaveAsync(record, ct);
        if (filing.Receipt is { } receipt)
            await receipts.SaveAsync(record.Id, receipt, ct);
        else
            await receipts.DeleteAsync(record.Id, ct);

        // Copy-only folders are copied, never moved (spec 5.1 step 5).
        var kind = filing.Folder?.Mode == FolderMode.Copy ? ActionKind.Copy : ActionKind.Move;
        var moved = await mover.MoveAsync(new MoveRequest(record.Id, kind, path, filing.Target, sha256), ct);
        if (!moved.Succeeded)
            return new PipelineResult.Failed(record.Id, moved.Error ?? "The move failed.", moved.Failure);

        var filed = record with { CurrentPath = moved.Destination! };
        var embedded = filing.Status == FileStatus.Filed && await EmbedAsync(filed, filing.Category, ct);
        return new PipelineResult.Filed(filed, filing.Category, filing.Folder?.Label, embedded);
    }

    // Step 6 (spec 5.6). On failure the file stays filed with no vector; "Rebuild missing search data" fills it in.
    private async Task<bool> EmbedAsync(FileRecord file, string category, CancellationToken ct)
    {
        var body = $"{category}. {file.Caption} {StoredText.Cut(file.ExtractedText, settings.Current.Pipeline.EmbedTextChars)}"
            .Trim();
        try
        {
            await files.SaveEmbeddingAsync(file.Id, await embedder.EmbedDocumentAsync(file.Title, body, ct), ct);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or HttpRequestException or InvalidOperationException)
        {
            return false;
        }
    }

    // The validated category by name, falling back to the type's system row ("Other").
    private static Category CategoryFor(Analysis analysis, IReadOnlyList<Category> categoryList) =>
        categoryList.FirstOrDefault(c => c.Type == analysis.Type &&
            string.Equals(c.Name, analysis.Category, StringComparison.OrdinalIgnoreCase))
        ?? categoryList.FirstOrDefault(c => c.Type == analysis.Type && c.IsSystem)
        ?? throw new InvalidOperationException($"No category row for {analysis.Type} files.");

    private async Task<WatchedFolder?> FolderAsync(IncomingFile incoming, CancellationToken ct) =>
        incoming.WatchedFolderId is { } id ? (await folders.ListAsync(ct)).FirstOrDefault(f => f.Id == id) : null;
}
