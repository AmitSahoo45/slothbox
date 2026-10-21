using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SlothBox.Core;
using SlothBox.Server.Api;

namespace SlothBox.Server.Phone;

// Spec 7.9, 8.4 rule 5 and 9: the only two routes on the LAN listener. An upload is checked by token, size and its
// first bytes, saved in <output>\Inbox under a generated name, and queued.
public static class PhoneEndpoints
{
    private const string OldLinkPage =
        "<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
        "<title>SlothBox</title><p>This link no longer works. Scan the new QR code on your laptop.</p>";

    // The form's boundary and headers come on top of the file itself.
    private const long FormOverheadBytes = 64 * 1024;

    // How much of a refused body Kestrel may read and discard so the answer reaches the phone.
    private const long DrainCeilingBytes = 1L << 30;

    private static readonly byte[] JpegStart = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly HashSet<string> HeifBrands =
        ["heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1"];

    public static void Map(WebApplication app)
    {
        app.MapGet("/u/{token}", GetPage)
            .WithMetadata(new PhoneEndpoint()).WithName("GetPhonePage").WithTags("Phone")
            .Produces<string>(200, "text/html").Produces<string>(401, "text/html").Produces(404)
            .ProducesProblem(503).ProducesProblem(StatusCodes.Status501NotImplemented);
        app.MapPost("/u/{token}/upload", UploadAsync)
            .WithMetadata(new PhoneEndpoint()).WithName("UploadPhoneFile").WithTags("Phone")
            .Accepts<PhoneUploadRequest>("multipart/form-data")
            .Produces<PhoneUploadResultDto>().ProducesProblem(400).ProducesProblem(401).Produces(404)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(503)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
    }

    private static IResult GetPage(string token, ISettingsStore settings, PhonePage page, HttpContext context)
    {
        KeepPrivate(context.Response);
        var phone = settings.Current.Phone;
        if (!phone.Enabled)
            return Results.NotFound();
        if (!TokenMatches(token, phone))
            return Results.Content(OldLinkPage, "text/html; charset=utf-8", Encoding.UTF8, 401);
        var file = page.File;
        return file.Exists
            ? Results.Stream(file.CreateReadStream(), "text/html; charset=utf-8")
            : Results.Problem(statusCode: 503, title: "The phone page is not part of this build.");
    }

    private static async Task<IResult> UploadAsync(string token, HttpContext context, ISettingsStore settings,
        IJobQueue jobs, ILoggerFactory logs, CancellationToken ct)
    {
        KeepPrivate(context.Response);
        // Kestrel must be allowed to read and discard a body this route refuses: otherwise it drops the
        // connection, and the phone sees a network error instead of the 401 or 413. The upload limit itself is
        // enforced below while the file is written.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = DrainCeilingBytes;
        var current = settings.Current;
        if (!current.Phone.Enabled)
            return Results.NotFound();
        if (!TokenMatches(token, current.Phone))
            return Results.Problem(statusCode: 401, title: "This link no longer works.");
        if (current.OutputRoot.Length == 0)
            return Results.Problem(statusCode: 503, title: "SlothBox is not set up yet.");

        var limit = current.Phone.MaxUploadMegabytes * 1024L * 1024L;
        if (context.Request.ContentLength > limit + FormOverheadBytes)
            return TooLarge(current.Phone);
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) ||
            !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
            HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
            return Results.Problem(statusCode: 415, title: "Send the file as a form upload.");

        try
        {
            var reader = new MultipartReader(boundary, context.Request.Body);
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) &&
                    disposition.IsFileDisposition())
                    return await SaveAsync(section.Body, current, limit, jobs, logs.CreateLogger("SlothBox.Phone"), ct);
            }
            return Results.Problem(statusCode: 400, title: "No file was sent.");
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(current.Phone);
        }
    }

    private static async Task<IResult> SaveAsync(Stream body, SlothBoxSettings settings, long limit, IJobQueue jobs,
        ILogger log, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var first = await body.ReadAtLeastAsync(buffer.AsMemory(0, 16), 16, throwOnEndOfStream: false, ct);
        if (ExtensionOf(buffer.AsSpan(0, first)) is not { } extension)
            return Results.Problem(statusCode: 415, title: "Only images and PDFs can be sent.");

        var inbox = Path.Combine(settings.OutputRoot, "Inbox");
        Directory.CreateDirectory(inbox);
        var name = $"phone-{DateTime.Now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}" +
            extension;
        var path = Path.Combine(inbox, name);
        // Written under the temp suffix the gate skips, then renamed: the Inbox watcher never sees half a file.
        var temp = path + TempFiles.Suffix;
        try
        {
            long total = 0;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16,
                             useAsync: true))
            {
                for (var count = first; count > 0; count = await body.ReadAsync(buffer, ct))
                {
                    total += count;
                    if (total > limit)
                        break;
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            if (total > limit)
            {
                File.Delete(temp);
                return TooLarge(settings.Phone);
            }
            File.Move(temp, path);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        try
        {
            await jobs.EnqueueAsync(new IncomingFile(path, FileOrigin.Phone, null, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The Inbox is watched, so the file is picked up once the queue is back.
            log.LogWarning("A phone upload could not be queued: {Error}", e.Message);
        }
        return Results.Ok(new PhoneUploadResultDto(name, true));
    }

    // Images and PDFs, told apart by their first bytes; the name the phone sends is never trusted.
    private static string? ExtensionOf(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(JpegStart))
            return ".jpg";
        if (head.StartsWith(PngStart))
            return ".png";
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return ".gif";
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return ".webp";
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8) &&
            HeifBrands.Contains(Encoding.ASCII.GetString(head[8..12])))
            return ".heic";
        return head.StartsWith("%PDF-"u8) ? ".pdf" : null;
    }

    private static bool TokenMatches(string token, PhoneSettings phone) =>
        phone.Token.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(phone.Token));

    private static IResult TooLarge(PhoneSettings phone) =>
        Results.Problem(statusCode: 413, title: $"This file is over {phone.MaxUploadMegabytes} MB.");

    // The token is in the URL: keep the page out of caches and referrers.
    private static void KeepPrivate(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XContentTypeOptions = "nosniff";
    }
}
