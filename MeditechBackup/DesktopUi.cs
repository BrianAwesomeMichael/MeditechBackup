using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MeditechBackup;

/// <summary>
/// The window on the backup PC: a tray icon (colour shows the state) and a status
/// window showing the same live page as the 201 admin tab, plus the "This PC" settings.
/// Closing the window keeps backups running in the tray. Exit is in the tray menu.
/// </summary>
public sealed class DesktopUi : ApplicationContext
{
    private readonly StatusHub _hub;
    private readonly BackupEngine _engine;
    private readonly LocalConfig _cfg;
    private readonly NotifyIcon _tray;
    private readonly MainWindow _window;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _pauseItem, _foldersItem;
    private readonly Dictionary<string, Icon> _icons = new();
    private readonly EventWaitHandle? _showEvent;
    private readonly Thread? _showThread;
    private volatile bool _exiting;
    private string _lastState = "";

    public string Url => $"http://localhost:{_cfg.WebPort}/";

    public DesktopUi(IServiceProvider sp, LocalConfig cfg, bool startHidden)
    {
        _cfg = cfg;
        _hub = sp.GetRequiredService<StatusHub>();
        _engine = sp.GetRequiredService<BackupEngine>();

        _window = new MainWindow(this);
        _window.CreateControl();
        _ = _window.Handle; // so BeginInvoke works before the window is first shown

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open status window", null, (_, _) => ShowWindow()).Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Run now", null, (_, _) => _engine.Command("run_now", "backup PC tray"));
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => _engine.Command(_hub.State.Paused ? "resume" : "pause", "backup PC tray"));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        _foldersItem = new ToolStripMenuItem("Open backup folder");
        menu.Items.Add(_foldersItem);
        menu.Items.Add("Open log folder", null, (_, _) => OpenPath(Path.Combine(AppContext.BaseDirectory, "logs")));
        menu.Items.Add("Open in web browser", null, (_, _) => OpenPath(Url));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (stops backups)", null, (_, _) => ConfirmExit());
        menu.Opening += (_, _) =>
        {
            _pauseItem.Text = _hub.State.Paused ? "Resume" : "Pause";
            _foldersItem.DropDownItems.Clear();
            foreach (var root in _cfg.BackupRoots())
            {
                var r = root;
                _foldersItem.DropDownItems.Add(r, null, (_, _) => OpenPath(r));
            }
            _foldersItem.Enabled = _foldersItem.DropDownItems.Count > 0;
        };

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Meditech Backup", Icon = IconFor("starting") };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => UpdateTray();
        _timer.Start();

        // A second launch (e.g. double-clicking the exe again) signals this to bring the window forward.
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            _showThread = new Thread(() =>
            {
                while (!_exiting)
                {
                    if (_showEvent.WaitOne(1000) && !_exiting)
                        try { _window.BeginInvoke(ShowWindow); } catch { }
                }
            }) { IsBackground = true, Name = "ShowListener" };
            _showThread.Start();
        }
        catch { }

        // If the backup engine stops on its own, close the UI too (a restart tool can then start it again).
        sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
        {
            if (!_exiting) try { _window.BeginInvoke(ExitNow); } catch { }
        });

        if (!startHidden) ShowWindow();
    }

    public void ShowWindow()
    {
        if (!_window.Visible) _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
        _window.BringToFront();
    }

    private bool _toldAboutTray;
    public void WindowHidden()
    {
        if (_toldAboutTray) return;
        _toldAboutTray = true;
        _tray.ShowBalloonTip(4000, "Meditech Backup is still running",
            "Backups carry on in the background. Double-click the tray icon to open the window again.", ToolTipIcon.Info);
    }

    public void ConfirmExit()
    {
        var running = _hub.RunState == "running";
        var msg = (running ? "A backup is running right now. " : "") +
                  "Exiting stops all backups until Meditech Backup is started again.\n\nExit anyway?";
        if (MessageBox.Show(msg, "Meditech Backup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            ExitNow();
    }

    public bool Exiting => _exiting;

    private void ExitNow()
    {
        _exiting = true;
        _timer.Stop();
        _tray.Visible = false;
        _window.Close();
        ExitThread();
    }

    private void UpdateTray()
    {
        var state = _hub.State.Paused && _hub.RunState != "running" ? "paused" : _hub.RunState;
        var cur = _hub.Current;
        string text = state switch
        {
            "running" when cur != null => $"Backing up {cur.Code}" + (_hub.Cycle.JobCount > 0 ? $" ({_hub.Cycle.JobIndex}/{_hub.Cycle.JobCount})" : ""),
            "running" => "Working…",
            "waiting" => _hub.Cycle.NextAt is DateTime n ? $"Next pass {n.ToLocalTime():ddd h:mm tt}" : "Waiting",
            "paused" => "Paused",
            "error" => "Problem: " + _hub.RunMessage,
            _ => "Starting…",
        };
        text = "Meditech Backup – " + text;
        _tray.Text = text.Length > 127 ? text[..124] + "…" : text;
        if (state != _lastState)
        {
            _tray.Icon = IconFor(state);
            if (state == "error" && _lastState != "")
                _tray.ShowBalloonTip(5000, "Meditech Backup needs attention", _hub.RunMessage, ToolTipIcon.Warning);
            _lastState = state;
        }
        _window.SetTitle(text.Length > 90 ? text[..87] + "…" : text);
    }

    /// <summary>The app icon with a coloured dot: blue running, green waiting, yellow paused, red problem.</summary>
    private Icon IconFor(string state)
    {
        if (_icons.TryGetValue(state, out var ic)) return ic;
        var dot = state switch
        {
            "running" => Color.FromArgb(33, 150, 243),
            "waiting" => Color.FromArgb(67, 160, 71),
            "paused" => Color.FromArgb(255, 179, 0),
            "error" => Color.FromArgb(229, 57, 53),
            _ => Color.Gray,
        };
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var baseIcon = AppIcon(32);
            if (baseIcon != null) g.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
            else g.FillRectangle(Brushes.SteelBlue, 2, 2, 28, 28);
            using var b = new SolidBrush(dot);
            g.FillEllipse(Brushes.White, 17, 17, 15, 15);
            g.FillEllipse(b, 19, 19, 11, 11);
        }
        ic = Icon.FromHandle(bmp.GetHicon());
        _icons[state] = ic;
        return ic;
    }

    public static Icon? AppIcon(int size)
    {
        try
        {
            var ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            return ico == null ? null : new Icon(ico, size, size);
        }
        catch { return null; }
    }

    public static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show($"Could not open {path}:\n{e.Message}", "Meditech Backup"); }
    }

    /// <summary>Called by a second copy of the exe: bring the running one forward (or open the page if it runs as a service).</summary>
    public static void ShowOtherInstance(int port)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(Program.ShowEventName);
            ev.Set();
            return;
        }
        catch { }
        // No window to show: it's running as a Windows service. Open the status page instead.
        OpenPath($"http://localhost:{port}/");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _timer.Dispose();
            _tray.Dispose();
            _showEvent?.Dispose();
            _window.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The status window: the local status page in an embedded Edge (WebView2) view.</summary>
public sealed class MainWindow : Form
{
    private readonly DesktopUi _ui;
    private WebView2? _web;
    private bool _webStarted;

    public MainWindow(DesktopUi ui)
    {
        _ui = ui;
        Text = "Meditech Backup";
        Icon = DesktopUi.AppIcon(32) ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1400, 900);
        Size = new Size(Math.Min(1400, wa.Width - 40), Math.Min(950, wa.Height - 40));
        MinimumSize = new Size(700, 450);
    }

    public void SetTitle(string t) { if (Text != t) Text = t; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_webStarted) { _webStarted = true; _ = StartWebView(); }
    }

    private async Task StartWebView()
    {
        try
        {
            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeditechBackup", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            // Links that open new windows (e.g. to 201) go to the normal browser.
            _web.CoreWebView2.NewWindowRequested += (_, a) => { a.Handled = true; DesktopUi.OpenPath(a.Uri); };
            _web.Source = new Uri(_ui.Url);
        }
        catch (Exception ex)
        {
            if (_web != null) { Controls.Remove(_web); _web.Dispose(); _web = null; }
            ShowFallback(ex.Message);
        }
    }

    /// <summary>Old PC without the Edge WebView2 runtime: offer the browser instead.</summary>
    private void ShowFallback(string why)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(30), WrapContents = false };
        panel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(800, 0), Font = new Font(Font.FontFamily, 11),
            Text = "Backups are running. This window couldn't show the status page because the Microsoft Edge WebView2 Runtime " +
                   "isn't installed on this PC.\n\nYou can open the same page in a web browser, or install the runtime " +
                   "(free, from Microsoft) and restart Meditech Backup.\n\n(" + why + ")",
        });
        var open = new Button { Text = "Open status page in browser", AutoSize = true, Padding = new Padding(8) };
        open.Click += (_, _) => DesktopUi.OpenPath(_ui.Url);
        var get = new LinkLabel { Text = "Download the WebView2 Runtime", AutoSize = true, Font = new Font(Font.FontFamily, 10) };
        get.LinkClicked += (_, _) => DesktopUi.OpenPath("https://developer.microsoft.com/microsoft-edge/webview2/");
        panel.Controls.Add(open);
        panel.Controls.Add(get);
        Controls.Add(panel);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X button hides to the tray; only "Exit" in the tray menu stops the program.
        if (e.CloseReason == CloseReason.UserClosing && !_ui.Exiting)
        {
            e.Cancel = true;
            Hide();
            _ui.WindowHidden();
            return;
        }
        base.OnFormClosing(e);
    }
}
