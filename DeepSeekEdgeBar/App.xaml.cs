using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using WPF.NotifyIcon;

namespace DeepSeekEdgeBar;

public partial class App : Application
{
    private NotifyIcon? _trayIcon;
    private IntPtr _appIcon;
    private MainWindow? _mainWindow;
    private bool _forceExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _mainWindow = new MainWindow();
        CreateTrayIcon();
        _mainWindow.Show();
        _mainWindow.Closing += (s, args) =>
        {
            if (!_forceExit && SettingsStore.LoadHideToTray()) { args.Cancel = true; _mainWindow.Hide(); }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        NativeHelpers.DestroyIcon(_appIcon);
        base.OnExit(e);
    }

    private static IntPtr CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(78, 204, 78));
            g.FillRectangle(brush, 4, 9, 24, 14);
            g.FillRectangle(brush, 14, 4, 4, 24);
        }
        return bmp.GetHicon();
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "Show / Hide" };
        show.Click += (s, ev) => ToggleWindow();
        menu.Items.Add(show);
        var settings = new MenuItem { Header = "Settings..." };
        settings.Click += (s, ev) => { var w = new SettingsWindow(); w.ShowDialog(); _mainWindow?.ApplySettings(); };
        menu.Items.Add(settings);
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (s, ev) => { _forceExit = true; _trayIcon?.Dispose(); _trayIcon = null; Shutdown(); };
        menu.Items.Add(exit);
        _appIcon = CreateAppIcon();
        _trayIcon = new NotifyIcon();
        _trayIcon.Create(_mainWindow!, _appIcon, "DeepSeek Edge Bar");
        _trayIcon.DoubleClick += ToggleWindow;
        _trayIcon.RightClick += () => { menu.IsOpen = true; };
    }

    private void ToggleWindow()
    {
        if (_mainWindow == null) return;
        if (_mainWindow.IsVisible) _mainWindow.Hide();
        else { _mainWindow.Show(); _mainWindow.Topmost = true; _mainWindow.Activate(); }
    }
}

internal static class NativeHelpers
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr handle);
}