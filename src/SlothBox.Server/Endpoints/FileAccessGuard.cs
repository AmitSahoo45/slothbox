using SlothBox.Core;

namespace SlothBox.Server.Endpoints;

// Spec 8.4 rule 4: a file is served or opened only from inside the output folder or a watched folder.
public sealed class FileAccessGuard(ISettingsStore settings, IWatchedFolderRepository folders)
{
    public async Task<bool> AllowsAsync(string path, CancellationToken ct)
    {
        var file = Path.GetFullPath(path);
        var roots = (await folders.ListAsync(ct)).Select(folder => folder.Path).ToList();
        if (settings.Current.OutputRoot.Length > 0)
            roots.Add(settings.Current.OutputRoot);
        return roots.Any(root => file.StartsWith(Folder(root), StringComparison.OrdinalIgnoreCase));
    }

    // The folder with a trailing separator, so D:\Out does not also match D:\Outbox.
    private static string Folder(string root)
    {
        var full = Path.GetFullPath(root);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }
}
