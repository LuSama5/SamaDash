using System.IO;
using System.Windows;
using System.Windows.Interop;
using SamaDash.Services;
using WinForms = System.Windows.Forms;

namespace SamaDash;

public partial class App : Application
{
    Mutex? mutex;
    WinForms.NotifyIcon? tray;
    MainWindow? window;
    MiniBar? miniBar;

    protected override void OnStartup(StartupEventArgs e)
    {
        mutex = new Mutex(true, "SamaDash.SingleInstance", out bool first);
        if (!first)
        {
            // già in esecuzione: chiedo all'altra istanza di mostrare la barra
            Native.AllowSetForegroundWindow(-1);
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal)) signal.Set();
            Shutdown();
            return;
        }
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        window = new MainWindow();
        new WindowInteropHelper(window).EnsureHandle();
        ListenForShowSignal();

        miniBar = new MiniBar(window);
        miniBar.Start();

        tray = new WinForms.NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "SamaDash",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left) window.Toggle();
        };

        var settings = Settings.Current;
        if (!settings.FirstRunDone)
        {
            Autostart.Set(true);
            settings.FirstRunDone = true;
            settings.Save();
            tray.ShowBalloonTip(4000, "SamaDash è attivo",
                "Clicca questa icona per aprire o chiudere la dashboard.", WinForms.ToolTipIcon.None);
        }
        else if (Autostart.IsEnabled)
        {
            Autostart.Set(true); // aggiorna il percorso se l'exe è stato spostato
        }
    }

    static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Storage.Dir);
            File.AppendAllText(Storage.PathOf("errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    const string ShowSignalName = "SamaDash.Show";

    void ListenForShowSignal()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(() => window!.ShowBar());
        }) { IsBackground = true };
        thread.Start();
    }

    WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Mostra dashboard", null, (_, _) => window!.ShowBar());
        menu.Items.Add("Modifica shortcut (JSON)", null, (_, _) => Shell.Open(Storage.PathOf(ShortcutStore.FileName)));
        menu.Items.Add("Apri cartella dati", null, (_, _) => Shell.Open(Storage.Dir));

        var mini = new WinForms.ToolStripMenuItem("Mini barra") { CheckOnClick = true };
        mini.Click += (_, _) => miniBar!.Enabled = mini.Checked;
        menu.Items.Add(mini);
        menu.Items.Add("Riporta la mini barra in alto", null, (_, _) => miniBar!.ResetPosition());

        var autoHide = new WinForms.ToolStripMenuItem("Barra di Windows a scomparsa") { CheckOnClick = true };
        autoHide.Click += (_, _) =>
        {
            Native.TaskbarAutoHide = autoHide.Checked;
            miniBar!.UpdateTaskbarButton();
        };
        menu.Items.Add(autoHide);

        var autostart = new WinForms.ToolStripMenuItem("Avvio con Windows") { CheckOnClick = true };
        menu.Opening += (_, _) =>
        {
            autostart.Checked = Autostart.IsEnabled;
            mini.Checked = miniBar!.Enabled;
            autoHide.Checked = Native.TaskbarAutoHide;
        };
        autostart.Click += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => Shutdown());
        return menu;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        window?.Tracker.Dispose();
        if (tray != null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
        mutex?.Dispose();
        base.OnExit(e);
    }
}
