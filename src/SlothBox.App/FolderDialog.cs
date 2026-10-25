using SlothBox.Core;

namespace SlothBox.App;

// Spec 8.2 "Native folder dialog": FolderBrowserDialog on the WinForms thread, owned by a top-most window so it opens in
// front of the browser that asked for it. A second request while it is open gets the same answer.
internal sealed class FolderDialog(TrayIcon tray) : INativeDialogs
{
    private readonly Lock _gate = new();
    private Task<string?>? _open;

    public Task<string?> PickFolderAsync(CancellationToken ct)
    {
        Task<string?> open;
        lock (_gate)
            open = _open ??= Show();
        return open.WaitAsync(ct);
    }

    // Under the lock: the dialog goes to the WinForms thread, and is forgotten once it closes.
    private Task<string?> Show()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = tray.Post(() =>
        {
            try
            {
                answer.TrySetResult(Pick());
            }
            catch (Exception e)
            {
                answer.TrySetException(e);
            }
            finally
            {
                lock (_gate)
                    _open = null;
            }
        });
        if (!posted)
            answer.TrySetException(new InvalidOperationException("SlothBox is closing."));
        return answer.Task;
    }

    private static string? Pick()
    {
        // A window above all others, so the dialog it owns opens in front of the browser instead of behind it.
        using var owner = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, Opacity = 0,
            StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1)
        };
        owner.Show();
        owner.Activate();
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder for SlothBox", UseDescriptionForTitle = true, ShowNewFolderButton = true
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
