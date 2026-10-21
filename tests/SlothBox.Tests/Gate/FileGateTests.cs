using SlothBox.Core;
using SlothBox.Core.Gate;

namespace SlothBox.Tests.Gate;

public sealed class FileGateTests : IDisposable
{
    // SHA-256 of the UTF-8 text "hello".
    private const string HelloSha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    private readonly TempFolder _temp = new();
    private readonly FakeWatchedFolders _folders = new();
    private readonly FakeIgnoredHashes _ignored = new();
    private readonly FakeFiles _files = new();
    private readonly List<TimeSpan> _waits = [];
    private Action _duringWait = () => { };

    private Task<GateResult> Check(string path, int? folderId = 1)
    {
        var gate = new FileGate(_folders, _ignored, _files, new PipelineSettings(), (wait, _) =>
        {
            _waits.Add(wait);
            _duringWait();
            return Task.CompletedTask;
        });
        return gate.CheckAsync(new IncomingFile(path, FileOrigin.Folder, folderId, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
    }

    // A file last written minutes ago has already been unchanged for longer than the stability window.
    private string OldFile(string name, string content = "hello")
    {
        var path = _temp.Write(name, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    [Fact]
    public async Task A_crdownload_file_is_skipped_and_the_same_file_renamed_to_pdf_is_ready()
    {
        var partial = OldFile("statement.pdf.crdownload");
        (await Check(partial)).ShouldBe(new GateResult.Skip(GateSkipReason.Temporary));

        var finished = Path.ChangeExtension(partial, null);
        File.Move(partial, finished);
        (await Check(finished)).ShouldBe(new GateResult.Ready(HelloSha256));
    }

    [Theory]
    [InlineData("setup.exe.part")]
    [InlineData("video.mp4.partial")]
    [InlineData("cache.tmp")]
    [InlineData("page.download")]
    [InlineData("REPORT.PDF.CRDOWNLOAD")]
    [InlineData("~$budget.xlsx")]
    [InlineData("desktop.ini")]
    [InlineData("Thumbs.db")]
    [InlineData("receipt.jpg.slothbox-tmp")]
    [InlineData("receipt.jpg.17.slothbox-tmp")]
    public async Task Temporary_and_shell_files_are_skipped(string name) =>
        (await Check(OldFile(name))).ShouldBe(new GateResult.Skip(GateSkipReason.Temporary));

    [Fact]
    public async Task Hidden_files_are_skipped()
    {
        // Unix has no hidden attribute; .NET reports dot-files as hidden there.
        var path = OldFile(OperatingSystem.IsWindows() ? "notes.txt" : ".notes.txt");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

        (await Check(path)).ShouldBe(new GateResult.Skip(GateSkipReason.Hidden));
    }

    [Fact]
    public async Task System_files_are_skipped()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The system attribute exists only on Windows.");
        var path = OldFile("driver.dat");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.System);

        (await Check(path)).ShouldBe(new GateResult.Skip(GateSkipReason.System));
    }

    [Fact]
    public async Task Files_in_the_folder_baseline_are_skipped_and_new_files_are_not()
    {
        var existing = OldFile("old-invoice.pdf");
        var arrived = OldFile("new-invoice.pdf", "a new invoice");
        _folders.Baselines[1] = [existing];

        (await Check(existing)).ShouldBe(new GateResult.Skip(GateSkipReason.Baseline));
        (await Check(arrived)).ShouldBeOfType<GateResult.Ready>();
    }

    [Fact]
    public async Task A_file_whose_hash_was_undone_is_skipped()
    {
        _ignored.Hashes.Add(HelloSha256);

        (await Check(OldFile("receipt.jpg"))).ShouldBe(new GateResult.Skip(GateSkipReason.Ignored));
    }

    [Fact]
    public async Task A_file_whose_hash_is_already_filed_is_a_duplicate()
    {
        var filed = TestRecords.File(HelloSha256);
        _files.Records.Add(filed);

        (await Check(OldFile("receipt (1).jpg"))).ShouldBe(new GateResult.Duplicate(filed.Id, HelloSha256));
    }

    [Fact]
    public async Task A_file_open_in_another_program_is_not_ready_until_it_is_closed()
    {
        var path = OldFile("locked.pdf");
        // A viewer that only reads the file still holds it open.
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            (await Check(path)).ShouldBe(new GateResult.NotReady());

        (await Check(path)).ShouldBe(new GateResult.Ready(HelloSha256));
    }

    [Fact]
    public async Task A_fresh_file_waits_three_seconds_and_is_ready_when_unchanged()
    {
        var path = _temp.Write("screenshot.png", "hello");

        (await Check(path)).ShouldBe(new GateResult.Ready(HelloSha256));
        _waits.ShouldBe([TimeSpan.FromSeconds(3)]);
    }

    [Fact]
    public async Task A_file_that_grows_during_the_wait_is_not_ready()
    {
        var path = _temp.Write("download.pdf", "first bytes");
        _duringWait = () => File.AppendAllText(path, " and more bytes");

        (await Check(path)).ShouldBe(new GateResult.NotReady());
        _waits.ShouldBe([TimeSpan.FromSeconds(3)]);
    }

    [Fact]
    public async Task A_file_unchanged_for_longer_than_the_window_is_checked_without_waiting()
    {
        (await Check(OldFile("old.pdf"))).ShouldBe(new GateResult.Ready(HelloSha256));
        _waits.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_file_is_skipped() =>
        (await Check(_temp.PathOf("gone.pdf"))).ShouldBe(new GateResult.Skip(GateSkipReason.Missing));

    [Fact]
    public async Task A_file_deleted_during_the_wait_is_skipped()
    {
        var path = _temp.Write("short-lived.pdf", "hello");
        _duringWait = () => File.Delete(path);

        (await Check(path)).ShouldBe(new GateResult.Skip(GateSkipReason.Missing));
    }

    public void Dispose() => _temp.Dispose();
}
