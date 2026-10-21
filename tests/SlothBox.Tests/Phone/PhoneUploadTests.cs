using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using SlothBox.Core;
using SlothBox.Server.Phone;

namespace SlothBox.Tests.Phone;

public sealed class PhoneUploadTests : IDisposable
{
    private const string Token = "synthetic-phone-token";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. Encoding.ASCII.GetBytes("synthetic photo bytes")];

    private readonly TempFolder _temp = new();
    private readonly RecordingQueue _queue = new();

    private string Inbox => _temp.PathOf("output", "Inbox");

    private sealed class FixedSettings(SlothBoxSettings settings) : ISettingsStore
    {
        public SlothBoxSettings Current => settings;
    }

    private sealed class RecordingQueue : IJobQueue
    {
        public List<IncomingFile> Enqueued { get; } = [];
        public bool Offline { get; init; }

        public Task EnqueueAsync(IncomingFile file, CancellationToken ct)
        {
            if (Offline)
                throw new InvalidOperationException("The database is offline.");
            Enqueued.Add(file);
            return Task.CompletedTask;
        }

        public Task<Job?> ClaimNextAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> RequeueInterruptedAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class PhoneServer(ISettingsStore settings, IJobQueue queue, IFileProvider pageFiles)
        : WebApplicationFactory<Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Testing").ConfigureTestServices(services =>
            {
                services.AddSingleton(settings);
                services.AddSingleton(queue);
                services.AddSingleton(new PhonePage(pageFiles));
            });

        // As the phone's browser would call it: on the LAN listener, with a LAN host name.
        public async Task<HttpResponse> SendAsync(string method, string path, HttpContent? content = null)
        {
            var body = content is null ? null : await content.ReadAsByteArrayAsync();
            var context = await Server.SendAsync(context =>
            {
                context.Connection.LocalPort = 5081;
                context.Request.Host = new HostString("192.0.2.1", 5081);
                context.Request.Method = method;
                context.Request.Path = path;
                if (content is not null)
                {
                    context.Request.ContentType = content.Headers.ContentType!.ToString();
                    context.Request.ContentLength = body!.Length;
                    context.Request.Body = new MemoryStream(body);
                }
            }, TestContext.Current.CancellationToken);
            return context.Response;
        }
    }

    private PhoneServer Server(string outputRoot = "", int maxMegabytes = 50, bool enabled = true,
        IJobQueue? queue = null, bool pageBuilt = true)
    {
        if (pageBuilt)
            _temp.Write(Path.Combine("wwwroot", "phone", "index.html"), "<!doctype html><title>SlothBox</title>");
        var settings = new SlothBoxSettings
        {
            OutputRoot = outputRoot.Length > 0 ? outputRoot : _temp.PathOf("output"),
            Phone = new PhoneSettings(enabled, 5081, Token, maxMegabytes)
        };
        Directory.CreateDirectory(_temp.PathOf("wwwroot"));
        return new PhoneServer(new FixedSettings(settings), queue ?? _queue,
            new PhysicalFileProvider(_temp.PathOf("wwwroot")));
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string clientName = "IMG_2041.jpg") =>
        new() { { new ByteArrayContent(bytes), "file", clientName } };

    private static Task<string> TextAsync(HttpResponse response) =>
        new StreamReader(response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_upload_lands_in_the_Inbox_under_a_generated_name_and_is_queued()
    {
        await using var server = Server();

        var response = await server.SendAsync("POST", $"/u/{Token}/upload", Upload(Jpeg, "../../Windows/evil.exe"));

        response.StatusCode.ShouldBe(200);
        var saved = Directory.GetFiles(Inbox).ShouldHaveSingleItem();
        Path.GetFileName(saved).ShouldMatch(@"^phone-\d{8}-\d{6}-[0-9a-f]{6}\.jpg$");
        File.ReadAllBytes(saved).ShouldBe(Jpeg);
        using var result = JsonDocument.Parse(await TextAsync(response));
        result.RootElement.GetProperty("fileName").GetString().ShouldBe(Path.GetFileName(saved));
        result.RootElement.GetProperty("received").GetBoolean().ShouldBeTrue();
        var job = _queue.Enqueued.ShouldHaveSingleItem();
        (job.Path, job.Origin, job.WatchedFolderId).ShouldBe((saved, FileOrigin.Phone, (int?)null));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, ".png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0 }, ".gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50 }, ".webp")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63, 0, 0 }, ".heic")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A }, ".pdf")]
    public async Task Images_and_pdfs_are_recognised_by_their_first_bytes(byte[] bytes, string extension)
    {
        await using var server = Server();

        (await server.SendAsync("POST", $"/u/{Token}/upload", Upload(bytes, "upload.bin"))).StatusCode.ShouldBe(200);

        Path.GetExtension(Directory.GetFiles(Inbox).ShouldHaveSingleItem()).ShouldBe(extension);
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 })]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 })]
    [InlineData(new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F })]
    public async Task Anything_else_is_refused_with_415_whatever_its_name(byte[] bytes)
    {
        await using var server = Server();

        var response = await server.SendAsync("POST", $"/u/{Token}/upload", Upload(bytes, "photo.jpg"));

        response.StatusCode.ShouldBe(415);
        Directory.Exists(Inbox).ShouldBeFalse();
        _queue.Enqueued.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_refused_with_413_and_nothing_is_kept()
    {
        await using var server = Server(maxMegabytes: 1);
        byte[] large = [.. Jpeg, .. new byte[1024 * 1024]];

        var response = await server.SendAsync("POST", $"/u/{Token}/upload", Upload(large));

        response.StatusCode.ShouldBe(413);
        (Directory.Exists(Inbox) ? Directory.GetFiles(Inbox) : []).ShouldBeEmpty();
        _queue.Enqueued.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_wrong_token_is_refused_with_401_and_nothing_is_saved()
    {
        await using var server = Server();

        var upload = await server.SendAsync("POST", "/u/an-old-token/upload", Upload(Jpeg));
        var page = await server.SendAsync("GET", "/u/an-old-token");

        upload.StatusCode.ShouldBe(401);
        Directory.Exists(Inbox).ShouldBeFalse();
        page.StatusCode.ShouldBe(401);
        page.ContentType.ShouldStartWith("text/html");
        (await TextAsync(page)).ShouldContain("This link no longer works. Scan the new QR code on your laptop.");
    }

    [Fact]
    public async Task The_phone_page_is_served_for_the_right_token_and_never_cached()
    {
        await using var server = Server();

        var page = await server.SendAsync("GET", $"/u/{Token}");

        page.StatusCode.ShouldBe(200);
        page.ContentType.ShouldStartWith("text/html");
        (await TextAsync(page)).ShouldBe("<!doctype html><title>SlothBox</title>");
        page.Headers.CacheControl.ToString().ShouldBe("no-store");
        page.Headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
    }

    [Fact]
    public async Task Without_a_built_phone_page_the_page_route_answers_503()
    {
        await using var server = Server(pageBuilt: false);

        (await server.SendAsync("GET", $"/u/{Token}")).StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task Before_setup_chooses_an_output_folder_uploads_answer_503()
    {
        await using var server = new PhoneServer(
            new FixedSettings(new SlothBoxSettings { Phone = new PhoneSettings(true, 5081, Token, 50) }), _queue,
            new NullFileProvider());

        (await server.SendAsync("POST", $"/u/{Token}/upload", Upload(Jpeg))).StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task With_the_phone_page_switched_off_both_routes_answer_404()
    {
        await using var server = Server(enabled: false);

        (await server.SendAsync("GET", $"/u/{Token}")).StatusCode.ShouldBe(404);
        (await server.SendAsync("POST", $"/u/{Token}/upload", Upload(Jpeg))).StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task An_upload_is_kept_even_when_the_queue_is_offline()
    {
        await using var server = Server(queue: new RecordingQueue { Offline = true });

        (await server.SendAsync("POST", $"/u/{Token}/upload", Upload(Jpeg))).StatusCode.ShouldBe(200);

        File.ReadAllBytes(Directory.GetFiles(Inbox).ShouldHaveSingleItem()).ShouldBe(Jpeg);
    }

    public void Dispose() => _temp.Dispose();
}
