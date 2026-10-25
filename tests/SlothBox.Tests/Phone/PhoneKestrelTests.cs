using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SlothBox.Core;
using SlothBox.Server.Phone;

namespace SlothBox.Tests.Phone;

// The in-memory test server ignores Kestrel's body limits, so these run the phone routes on real Kestrel
// (loopback, random port) with Kestrel's own limit set far below the upload limit's scale.
public sealed class PhoneKestrelTests : IAsyncLifetime
{
    private const string Token = "synthetic-phone-token";
    private const int KestrelLimitBytes = 256 * 1024;

    private readonly TempFolder _temp = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedSettings(SlothBoxSettings settings) : ISettingsStore
    {
        public SlothBoxSettings Current => settings;

        public SlothBoxSettings Update(Func<SlothBoxSettings, SlothBoxSettings> change) =>
            throw new NotSupportedException();
    }

    private sealed class NoQueue : IJobQueue
    {
        public Task EnqueueAsync(IncomingFile file, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> EnqueueUnlessFinishedAsync(IncomingFile file, Core.Folders.FileStamp stamp,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<Job?> ClaimNextAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteAsync(long jobId, JobOutcome outcome, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> RequeueInterruptedAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<QueueCounts> CountsAsync(CancellationToken ct) => Task.FromResult(new QueueCounts(0, 0));
    }

    // Hides its length, so HttpClient sends the body in chunks.
    private sealed class UnknownLength(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = KestrelLimitBytes;
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<ISettingsStore>(new FixedSettings(new SlothBoxSettings
        {
            OutputRoot = _temp.PathOf("output"),
            Phone = new PhoneSettings(true, 5081, Token, MaxUploadMegabytes: 1)
        }));
        builder.Services.AddSingleton<IJobQueue>(new NoQueue());
        builder.Services.AddSingleton(new PhonePage(new NullFileProvider()));
        _app = builder.Build();
        PhoneEndpoints.Map(_app);
        await _app.StartAsync(Ct);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<HttpContent> UploadAsync(int fileBytes, bool chunked)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, .. new byte[fileBytes]]), "file", "IMG_2041.jpg" }
        };
        if (!chunked)
            return form;
        var body = new StreamContent(new UnknownLength(new MemoryStream(await form.ReadAsByteArrayAsync(Ct))));
        body.Headers.ContentType = form.Headers.ContentType;
        return body;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_over_the_limit_gets_its_413_back(bool chunked)
    {
        using var content = await UploadAsync(2 * 1024 * 1024, chunked);

        using var response = await _client.PostAsync($"/u/{Token}/upload", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_file_under_the_upload_limit_but_over_kestrels_own_limit_is_received()
    {
        using var content = await UploadAsync(900 * 1024, chunked: false);

        using var response = await _client.PostAsync($"/u/{Token}/upload", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _temp.Dispose();
    }
}
