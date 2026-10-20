using SlothBox.Core;

namespace SlothBox.Tests;

internal static class TestRecords
{
    public static FileRecord File(string sha256, FileStatus status = FileStatus.Filed)
    {
        var now = DateTimeOffset.UtcNow;
        return new FileRecord(Guid.NewGuid(), sha256, FileOrigin.Folder, null, "receipt.jpg", "receipt.jpg",
            "receipt.jpg", FileKind.Receipt, null, "", "", "", now, 5, status, null, null, now, now);
    }
}
