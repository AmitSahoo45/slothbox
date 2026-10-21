using SlothBox.Core;
using SlothBox.Core.Reading;

namespace SlothBox.Tests.Reading;

// Counts every allocation in the process, so nothing else may run at the same time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationCollection
{
    public const string Name = "Allocation";
}

[Collection(AllocationCollection.Name)]
public sealed class ContentReaderMemoryTests : IDisposable
{
    private readonly TempFolder _temp = new();

    [Fact]
    public async Task A_large_docx_is_read_up_to_the_text_limit_without_loading_the_whole_document()
    {
        var path = TestFiles.Docx(_temp.PathOf("large.docx"),
            Enumerable.Repeat(new string('a', 1_000), 10_000).ToArray());
        var reader = new ContentReader(new PipelineSettings());

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var content = await reader.ReadAsync(path, TestContext.Current.CancellationToken);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        content.ShouldBeOfType<FileContent.Text>().Value.Length.ShouldBe(20_000);
        allocated.ShouldBeLessThan(16L * 1024 * 1024);
    }

    public void Dispose() => _temp.Dispose();
}
