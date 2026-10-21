using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SkiaSharp;
using SlothBox.Core.Rules;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace SlothBox.Core.Reading;

// Pipeline step 3 (spec 5.1, 5.3): images become an upright JPEG, documents become text, the rest goes by name.
// A file that does not parse as its extension says is read by name only; IO errors still reach the caller.
public sealed class ContentReader(PipelineSettings settings) : IContentReader
{
    private const int JpegQuality = 85;
    private const int ScannedPdfPages = 3;
    private const int ScannedPdfMinimumCharacters = 50;

    // Decoding takes four bytes a pixel, so an image is decoded at a reduced scale where its format allows,
    // and one still larger than this is read by name only.
    private const long MaxDecodedPixels = 50_000_000;

    // Word saves a password-protected .docx as an OLE compound file instead of a zip.
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public async Task<FileContent> ReadAsync(string path, CancellationToken ct) =>
        FileGroups.Of(path) switch
        {
            FileGroup.Image => ReadImage(path),
            FileGroup.Heic => new FileContent.Unsupported(ReviewReason.Heic),
            FileGroup.ReadableDocument => Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pdf" => ReadPdf(path),
                ".docx" => await ReadDocxAsync(path, ct),
                _ => await ReadTextAsync(path, ct)
            },
            _ => new FileContent.NameOnly()
        };

    private FileContent ReadImage(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return new FileContent.NameOnly();
        try
        {
            var stored = codec.Info;
            var size = codec.GetScaledDimensions(
                Math.Min(1f, (float)settings.ImageMaxEdgePx / Math.Max(stored.Width, stored.Height)));
            if ((long)size.Width * size.Height > MaxDecodedPixels)
                return new FileContent.NameOnly();
            using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(size.Width, size.Height));
            if (bitmap is null)
                return new FileContent.NameOnly();
            bitmap.SetImmutable();
            return new FileContent.Image(UprightJpeg(bitmap, codec.EncodedOrigin));
        }
        catch (Exception e) when (e is not IOException and not OperationCanceledException)
        {
            return new FileContent.NameOnly();
        }
    }

    private byte[] UprightJpeg(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var quarterTurn = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (width, height) = quarterTurn ? (bitmap.Height, bitmap.Width) : (bitmap.Width, bitmap.Height);
        var scale = Math.Min(1f, (float)settings.ImageMaxEdgePx / Math.Max(width, height));

        using var surface = SKSurface.Create(new SKImageInfo(
            Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale))));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Scale(scale, scale);
        TurnUpright(canvas, origin, bitmap.Width, bitmap.Height);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var upright = surface.Snapshot();
        using var jpeg = upright.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return jpeg.ToArray();
    }

    // Maps stored pixels (w x h) to the upright view that the EXIF orientation describes.
    private static void TurnUpright(SKCanvas canvas, SKEncodedOrigin origin, int w, int h)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Scale(-1, 1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(h, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(h, w);
                canvas.Scale(1, -1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, w);
                canvas.RotateDegrees(-90);
                break;
        }
    }

    private FileContent ReadPdf(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            var text = new StringBuilder();
            var firstPagesCharacters = 0;
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                if (number > ScannedPdfPages && text.Length >= settings.StoredTextChars)
                    break;
                var pageText = ContentOrderTextExtractor.GetText(document.GetPage(number), false);
                if (number <= ScannedPdfPages)
                    firstPagesCharacters += pageText.Count(c => !char.IsWhiteSpace(c));
                text.Append(pageText).Append('\n');
            }
            return firstPagesCharacters < ScannedPdfMinimumCharacters
                ? new FileContent.Unsupported(ReviewReason.ScannedPdf)
                : Stored(text.ToString());
        }
        catch (PdfDocumentEncryptedException)
        {
            return new FileContent.Unsupported(ReviewReason.PasswordProtected);
        }
        catch (Exception e) when (e is not IOException and not OperationCanceledException)
        {
            return new FileContent.NameOnly();
        }
    }

    private async Task<FileContent> ReadDocxAsync(string path, CancellationToken ct)
    {
        var start = new byte[CompoundFileSignature.Length];
        await using (var stream = File.OpenRead(path))
            await stream.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false, ct);
        if (start.AsSpan().SequenceEqual(CompoundFileSignature))
            return new FileContent.Unsupported(ReviewReason.PasswordProtected);

        try
        {
            using var document = WordprocessingDocument.Open(path, false);
            if (document.MainDocumentPart is not { } body)
                return new FileContent.Text("");
            // Streamed, and stopped once there is enough text: a huge or crafted document never loads whole.
            using var reader = OpenXmlReader.Create(body);
            var text = new StringBuilder();
            while (text.Length <= settings.StoredTextChars && reader.Read())
            {
                if (reader.ElementType == typeof(Text) && reader.IsStartElement)
                    text.Append(reader.GetText());
                else if (reader.ElementType == typeof(Paragraph) && reader.IsEndElement)
                    text.Append('\n');
            }
            return Stored(text.ToString().TrimEnd('\n'));
        }
        catch (Exception e) when (e is not IOException and not OperationCanceledException)
        {
            return new FileContent.NameOnly();
        }
    }

    private async Task<FileContent> ReadTextAsync(string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[settings.StoredTextChars];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return Stored(new string(buffer, 0, read));
    }

    private FileContent.Text Stored(string text) =>
        new(StoredText.Cut(StoredText.Clean(text), settings.StoredTextChars));
}
