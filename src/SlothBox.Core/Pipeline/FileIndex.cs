namespace SlothBox.Core.Pipeline;

// Step 6 (spec 5.6): the vector EmbeddingGemma makes of a filed file's title, category, caption and text.
public static class FileIndex
{
    // Whatever the embedder throws, the file stays filed with no vector; "Rebuild missing search data" fills it in.
    // Only shutdown stops it. Saving the vector is a database write like any other, so its errors still propagate.
    public static async Task<bool> EmbedAsync(FileRecord file, string category, IEmbedder embedder,
        IFileRepository files, PipelineSettings pipeline, CancellationToken ct)
    {
        var body = $"{category}. {file.Caption} {StoredText.Cut(file.ExtractedText, pipeline.EmbedTextChars)}".Trim();
        float[] vector;
        try
        {
            vector = await embedder.EmbedDocumentAsync(file.Title, body, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
        await files.SaveEmbeddingAsync(file.Id, vector, ct);
        return true;
    }
}
