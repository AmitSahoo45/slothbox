using SlothBox.Core;
using SlothBox.Core.Reading;
using SlothBox.Core.Rules;

namespace SlothBox.Server.Endpoints;

// Thumbnails (256 px, cached in <output>\_cache\thumbs, spec 6.1) and previews (the size Gemma reads, spec 5.1) of
// filed images, made upright the same way as for Gemma. Null for anything that is not a readable image.
public sealed class FileImages(ISettingsStore settings)
{
    private const int ThumbnailEdge = 256;

    public async Task<byte[]?> ThumbnailAsync(FileRecord file, CancellationToken ct)
    {
        var cache = Path.Combine(settings.Current.OutputRoot, "_cache", "thumbs", $"{file.Id:N}.jpg");
        // A cached thumbnail is good until the file itself is written again.
        if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(file.CurrentPath))
            return await File.ReadAllBytesAsync(cache, ct);
        if (await JpegAsync(file.CurrentPath, ThumbnailEdge, ct) is not { } jpeg)
            return null;
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        // Written aside and renamed, so a half-written thumbnail is never served.
        var partial = $"{cache}.{Guid.NewGuid():N}.partial";
        await File.WriteAllBytesAsync(partial, jpeg, ct);
        File.Move(partial, cache, overwrite: true);
        return jpeg;
    }

    public Task<byte[]?> PreviewAsync(FileRecord file, CancellationToken ct) =>
        JpegAsync(file.CurrentPath, settings.Current.Pipeline.ImageMaxEdgePx, ct);

    private async Task<byte[]?> JpegAsync(string path, int edge, CancellationToken ct) =>
        FileGroups.Of(path) == FileGroup.Image &&
        await new ContentReader(settings.Current.Pipeline with { ImageMaxEdgePx = edge }).ReadAsync(path, ct) is
            FileContent.Image image
            ? image.JpegBytes
            : null;
}
