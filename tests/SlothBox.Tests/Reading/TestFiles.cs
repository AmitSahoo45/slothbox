using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SlothBox.Tests.Reading;

// Synthetic sample files built at test time: no real documents or photos are needed.
internal static class TestFiles
{
    // Left half red, right half blue, so a test can tell which way an image was turned.
    public static string Image(string path, int width, int height, SKEncodedImageFormat format)
    {
        File.WriteAllBytes(path, Encode(width, height, format));
        return path;
    }

    // A JPEG with an EXIF Orientation tag (1 as stored, 3 upside down, 6 and 8 turned a quarter).
    public static string JpegWithOrientation(string path, int width, int height, int orientation)
    {
        var jpeg = Encode(width, height, SKEncodedImageFormat.Jpeg);
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];
        File.WriteAllBytes(path, [.. jpeg[..2], .. exif, .. jpeg[2..]]);
        return path;
    }

    // A 141-byte PNG whose header claims width x height; decoding it would need width * height * 4 bytes.
    public static string PngClaiming(string path, int width, int height)
    {
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        AddChunk(png, "IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 6, 0, 0, 0]);
        AddChunk(png, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        AddChunk(png, "IEND", []);
        File.WriteAllBytes(path, [.. png]);
        return path;
    }

    public static (SKEncodedImageFormat Format, int Width, int Height) Inspect(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded));
        return (codec.EncodedFormat, codec.Info.Width, codec.Info.Height);
    }

    public static bool IsRed(SKColor color) => color.Red > 180 && color.Blue < 80;

    public static bool IsBlue(SKColor color) => color.Blue > 180 && color.Red < 80;

    // One page per string; an empty string makes a page with no text, like a scan.
    public static string Pdf(string path, params string[] pages)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (text.Length > 0)
                page.AddText(text, 8, new PdfPoint(25, 700), font);
        }
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    // A one-page PDF whose /Encrypt dictionary needs a user password nobody knows.
    public static string EncryptedPdf(string path)
    {
        var id = new string('c', 32);
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            $"<< /Filter /Standard /V 1 /R 2 /O <{new string('a', 64)}> /U <{new string('b', 64)}> /P -4 >>"
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<{id}> <{id}>] >>\n");
        pdf.Append($"startxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    public static string Docx(string path, params string[] paragraphs)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        document.AddMainDocumentPart().Document =
            new Document(new Body(paragraphs.Select(OpenXmlElement (text) => new Paragraph(new Run(new Text(text))))));
        return path;
    }

    // Word saves a password-protected .docx as an OLE compound file instead of a zip.
    public static string EncryptedDocx(string path)
    {
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[504]]);
        return path;
    }

    private static byte[] BigEndian(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static void AddChunk(List<byte> png, string type, byte[] data)
    {
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        png.AddRange(BigEndian(data.Length));
        png.AddRange(typed);
        png.AddRange(BigEndian((int)Crc32(typed)));
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        using var red = new SKPaint { Color = SKColors.Red };
        using var blue = new SKPaint { Color = SKColors.Blue };
        surface.Canvas.DrawRect(0, 0, width / 2f, height, red);
        surface.Canvas.DrawRect(width / 2f, 0, width / 2f, height, blue);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }
}
