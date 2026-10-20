using System.Security.Cryptography;

namespace SlothBox.Core;

public static class FileHash
{
    public static async Task<string> Sha256Async(string path, FileShare share, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
