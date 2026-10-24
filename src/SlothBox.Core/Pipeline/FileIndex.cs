namespace SlothBox.Core.Pipeline;

// Step 6 (spec 5.6): the vector EmbeddingGemma makes of a filed file's title, category, caption and text.
public static class FileIndex
{
    // On failure the file stays filed with no vector; "Rebuild missing search data" fills it in.
    public static async Task<bool> EmbedAsync(FileRecord file, string category, IEmbedder embedder,
        IFileRepository files, PipelineSettings pipeline, CancellationToken ct)
    {
        var body = $"{category}. {file.Caption} {StoredText.Cut(file.ExtractedText, pipeline.EmbedTextChars)}".Trim();
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
}
