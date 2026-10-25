using System.Security;
using SlothBox.Core;
using SlothBox.Server.Tray;

namespace SlothBox.App;

// Spec 7.8: the tray icon, its menu and its notices. Everything here runs on the WinForms thread; Server calls in from
// its own threads, so those calls are posted to it.
internal sealed class TrayIcon : ITrayNotifier, IDisposable
{
    // NotifyIcon.Text takes up to 127 characters.
    private const int TooltipLength = 127;

    private readonly ApplicationContext _context = new();
    // A window handle on the WinForms thread, so other threads can post to it before Application.Run starts.
    private readonly Control _thread = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new("Starting…") { Enabled = false };
    private readonly ToolStripMenuItem _pause = new("Pause") { Enabled = false };
    private readonly ToolStripMenuItem _undo = new("Undo last") { Enabled = false };
    private readonly ToolStripMenuItem _startAtLogin = new("Start at login") { Enabled = false };
    private readonly NotifyIcon _icon;
    private TrayCommands? _commands;
    private IAutoStart? _autoStart;
    // Where a click on the notice on screen leads: Needs review for a review notice, nowhere for the others.
    private string? _noticeLink;

    public TrayIcon()
    {
        _ = _thread.Handle;
        _pause.Click += async (_, _) => await RunAsync(async commands =>
        {
            if (commands.Paused)
                await commands.ResumeAsync(CancellationToken.None);
            else
                await commands.PauseAsync(CancellationToken.None);
            return null;
        }, "SlothBox");
        _undo.Click += async (_, _) => await RunAsync(async commands =>
            await commands.UndoLastAsync(CancellationToken.None), "Nothing undone");
        _startAtLogin.Click += (_, _) => ToggleStartAtLogin();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _status, new ToolStripSeparator(),
            new ToolStripMenuItem("Open dashboard", null, (_, _) => Browser.Open(TrayCommands.DashboardUrl)),
            _pause, _undo, new ToolStripSeparator(),
            _startAtLogin, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => _context.ExitThread())
        });
        _menu.Opening += (_, _) => ShowState();
        _icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "SlothBox", ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => Browser.Open(TrayCommands.DashboardUrl);
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_noticeLink is { } link)
                Browser.Open(link);
        };
    }

    // Shows the icon and runs the WinForms thread until Exit. The menu's commands are Server's.
    public void Run(TrayCommands commands, IAutoStart autoStart)
    {
        _commands = commands;
        _autoStart = autoStart;
        _pause.Enabled = _undo.Enabled = _startAtLogin.Enabled = true;
        _icon.Visible = true;
        Application.Run(_context);
        // Stopping can take up to 30 s (spec 4.3); the icon goes now, not when that is done.
        _icon.Visible = false;
    }

    // From any thread: SlothBox is stopping.
    public void Exit() => Post(_context.ExitThread);

    public void ShowReviewNotice(Guid fileId, string fileName) =>
        Post(() => Notice(new TrayNotice("A file needs review", fileName), TrayCommands.ReviewUrl));

    public void SetStatus(string statusLine) => Post(() =>
    {
        _status.Text = statusLine;
        var tooltip = $"SlothBox: {statusLine}";
        _icon.Text = tooltip.Length <= TooltipLength ? tooltip : tooltip[..TooltipLength];
    });

    // Runs the action on the WinForms thread; false once the tray has closed.
    public bool Post(Action action)
    {
        try
        {
            _thread.BeginInvoke(action);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    // Pause or Resume, and whether start at login is on, as they are when the menu opens.
    private void ShowState()
    {
        _pause.Text = _commands?.Paused == true ? "Resume" : "Pause";
        try
        {
            _startAtLogin.Checked = _autoStart?.IsEnabled ?? false;
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
        {
            _startAtLogin.Checked = false;
        }
    }

    // Server's work runs off the WinForms thread, so the menu stays responsive while a file is put back.
    private async Task RunAsync(Func<TrayCommands, Task<TrayNotice?>> command, string failure)
    {
        if (_commands is not { } commands)
            return;
        TrayNotice? notice;
        try
        {
            notice = await Task.Run(() => command(commands));
        }
        catch (Exception e)
        {
            notice = new TrayNotice(failure, e.Message);
        }
        if (notice is not null)
            Notice(notice, null);
    }

    private void ToggleStartAtLogin()
    {
        if (_autoStart is not { } autoStart)
            return;
        try
        {
            autoStart.SetEnabled(!autoStart.IsEnabled);
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
        {
            Notice(new TrayNotice("Start at login",
                $"Windows did not let SlothBox change start at login: {e.Message}"), null);
        }
    }

    private void Notice(TrayNotice notice, string? link)
    {
        _noticeLink = link;
        _icon.ShowBalloonTip(10_000, notice.Title, notice.Text, ToolTipIcon.None);
    }

    public void Dispose()
    {
        // A hidden icon leaves no stale icon behind in the notification area.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _thread.Dispose();
        _context.Dispose();
    }
}
