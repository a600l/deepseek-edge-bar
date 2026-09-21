using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DeepSeekEdgeBar;

public partial class MainWindow : Window
{
    private const double CollapsedWidth = 6;
    private const double ExpandedWidth = 260;
    private const double BarHeight = 220;
    private const double PeakBarHeight = 320;
    private const double ExpandedHeight = 460;
    private const double DragThreshold = 6.0;
    private const int RefreshMs = 60000;

    private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush PeakBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush AmberBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush GrayBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private const decimal FullBalance = 10m;

    private readonly DispatcherTimer _refreshTimer;
    private bool _isExpanded;
    private Point _mouseDownPos;
    private string _edge;
    private double _barHeight = BarHeight;

    public MainWindow()
    {
        InitializeComponent();
        Height = BarHeight;
        Opacity = SettingsStore.LoadOpacity();
        _edge = SettingsStore.LoadEdge();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshMs) };
        _refreshTimer.Tick += (s, ev) => RefreshAll();
        MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;
        MouseLeftButtonUp += MainWindow_MouseLeftButtonUp;
        SourceInitialized += MainWindow_SourceInitialized;
        Deactivated += (s, ev) => { if (_isExpanded) CollapsePanel(); };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        _refreshTimer.Start();
        RefreshAll();
    }

    private void CollapsedStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_isExpanded) return;
        collapsedStrip.Opacity = 1.0;
        Grip.Visibility = Visibility.Visible;
    }

    private void CollapsedStrip_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isExpanded) return;
        collapsedStrip.Opacity = 0.85;
        Grip.Visibility = Visibility.Collapsed;
    }

    private void ExpandPanel()
    {
        if (_isExpanded) return;
        _isExpanded = true;
        RepositionForExpand();
        collapsedStrip.Visibility = Visibility.Hidden;
        expandedPanel.Visibility = Visibility.Visible;
        DoubleAnimation anim = new DoubleAnimation(ExpandedWidth, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (s, ev) => RepositionForEdge();
        BeginAnimation(WidthProperty, anim);
        DoubleAnimation hAnim = new DoubleAnimation(ExpandedHeight, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(HeightProperty, hAnim);
    }

    private void CollapsePanel()
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        DoubleAnimation widthAnim = new DoubleAnimation(CollapsedWidth, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        widthAnim.Completed += (s, ev) =>
        {
            if (_isExpanded) return;
            expandedPanel.Visibility = Visibility.Hidden;
            collapsedStrip.Visibility = Visibility.Visible;
            RepositionForEdge();
        };
        BeginAnimation(WidthProperty, widthAnim);
        DoubleAnimation hAnim = new DoubleAnimation(_barHeight, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(HeightProperty, hAnim);
    }

    private void RepositionForExpand()
    {
        DockToEdge();
    }

    private void PositionWindow()
    {
        DockToEdge();
    }

    private void DockToEdge()
    {
        Rect wa = SystemParameters.WorkArea;
        double width = _isExpanded ? ExpandedWidth : ActualWidth;
        double height = _isExpanded ? Math.Max(ActualHeight, ExpandedHeight) : _barHeight;
        Left = SettingsStore.IsLeftEdge() ? wa.Left : wa.Right - width;
        Top = wa.Top + (wa.Height - height) / 2;
    }

    private void RepositionForEdge()
    {
        DockToEdge();
    }

    private void MainWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? origin = e.OriginalSource as DependencyObject;
        while (origin != null)
        {
            if (origin is Button)
            {
                e.Handled = true;
                return;
            }
            origin = VisualTreeHelper.GetParent(origin);
        }

        _mouseDownPos = e.GetPosition(this);
    }

    private void MainWindow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.GetPosition(this) - _mouseDownPos).Length >= DragThreshold) return;
        if (_isExpanded) return;
        ExpandPanel();
    }

    public async void RefreshAll()
    {
        string key = SettingsStore.LoadApiKey();
        string token = SettingsStore.LoadSessionToken();
        string email = SettingsStore.LoadAccountEmail();
        string name = SettingsStore.LoadDisplayName();
        string accountLine = string.Empty;
        string platformToken = SettingsStore.LoadPlatformSessionToken();
        string usageToday = string.Empty;
        string usageMonth = string.Empty;
        string usageTopModel = string.Empty;
        string usageHint = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            await Dispatcher.InvokeAsync(() =>
            {
                collapsedStrip.Background = GrayBrush;
                StatusDot.Fill = GrayBrush;
                SetBarHeight(BarHeight);
                StatusValue.Text = "no API key";
                StatusCountdown.Text = string.Empty;
                BalanceValue.Text = "—";
                BalanceDetail.Text = string.Empty;
                ApiKeyStatus.Text = "No API key — open settings (⚙)";
                AccountLine.Text = string.Empty;
                UsageToday.Text = string.Empty;
                UsageMonth.Text = string.Empty;
                UsageTopModel.Text = string.Empty;
                UsageHint.Text = string.Empty;
            });
            return;
        }

        try
        {
            DeepSeekApiClient client = new DeepSeekApiClient(key);
            BalanceInfo info = await client.GetBalanceAsync();
            List<string> models = await client.GetModelsAsync();

            string accountEmail = email;
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    var session = await new DeepSeekAccountClient().GetCurrentAsync(token);
                    if (!string.IsNullOrWhiteSpace(session.Token))
                    {
                        SettingsStore.SaveSessionToken(session.Token);
                        if (!string.IsNullOrWhiteSpace(session.Email)) { accountEmail = session.Email; SettingsStore.SaveAccountEmail(session.Email); }
                    }
                    accountLine = !string.IsNullOrWhiteSpace(name)
                        ? (string.IsNullOrWhiteSpace(accountEmail) ? $"\u2630 {name}" : $"\u2630 {name} · {accountEmail}")
                        : (string.IsNullOrWhiteSpace(accountEmail) ? "" : $"\u2630 {accountEmail}");
                }
                catch (DeepSeekAuthException)
                {
                    SettingsStore.ClearAccount();
                    accountEmail = "";
                    accountLine = "session expired — re-login in Settings";
                }
            }

            if (!string.IsNullOrWhiteSpace(platformToken))
            {
                try
                {
                    var usage = await new DeepSeekUsageClient().GetUsageAsync(platformToken);
                    usageToday = $"TODAY  {usage.TodayTokens:N0} tok · {usage.TodayRequests:N0} req · ${usage.TodayCost:F4}";
                    usageMonth = $"MO {usage.MonthTokens:N0} tok · ${usage.MonthCost:F4}";
                    if (!string.IsNullOrWhiteSpace(usage.TopModel))
                        usageTopModel = $"TOP  {usage.TopModel}";
                }
                catch (DeepSeekUsageAuthException)
                {
                    usageHint = "usage: token expired";
                }
                catch
                {
                    usageHint = "usage: unavailable";
                }
            }

            DateTime utc = DateTime.UtcNow;
            bool accountProblem = !info.IsAvailable || info.TotalBalance <= 0;
            bool peak = !accountProblem && PeakHoursService.IsPeakTime(utc);
            string countdown = PeakHoursService.FormatCountdown(PeakHoursService.TimeUntilNextChange(utc));

            await Dispatcher.InvokeAsync(() =>
            {
                if (accountProblem)
                {
                    collapsedStrip.Background = RedBrush;
                    StatusDot.Fill = RedBrush;
                    SetBarHeight(BarHeight);
                    StatusValue.Text = "balance unavailable";
                    StatusCountdown.Text = string.Empty;
                    BalanceValue.Text = "—";
                    BalanceDetail.Text = $"unavailable · granted {info.Currency} {info.GrantedBalance:0.00} · topped-up {info.Currency} {info.ToppedUpBalance:0.00}";
                    ApiKeyStatus.Text = string.Empty;
                }
                else
                {
                    collapsedStrip.Background = BarGradient(info.TotalBalance);
                    StatusDot.Fill = peak ? PeakBrush : GreenBrush;
                    SetBarHeight(peak ? PeakBarHeight : BarHeight);
                    StatusValue.Text = peak ? "PEAK · 2× rates" : "OFF-PEAK · lowest rates";
                    StatusCountdown.Text = (peak ? "off in " : "peak in ") + countdown;
                    BalanceValue.Text = $"{info.Currency} {info.TotalBalance:0.00}";
                    BalanceValue.Foreground = BrushForBalance(info.TotalBalance);
                    BalanceDetail.Foreground = BrushForBalance(info.TotalBalance);
                    BalanceDetail.Text = $"available · granted {info.Currency} {info.GrantedBalance:0.00} · topped-up {info.Currency} {info.ToppedUpBalance:0.00}";
                    ApiKeyStatus.Text = string.Empty;
                }
                ModelValue.Text = string.Join(" · ", models);
                LastRefresh.Text = "refreshed " + DateTime.Now.ToString("HH:mm:ss");
                AccountLine.Text = accountLine;
                UsageToday.Text = usageToday;
                UsageMonth.Text = usageMonth;
                UsageTopModel.Text = usageTopModel;
                UsageHint.Text = usageHint;
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                collapsedStrip.Background = RedBrush;
                StatusDot.Fill = RedBrush;
                StatusValue.Text = "offline";
                StatusCountdown.Text = string.Empty;
                BalanceValue.Text = "—";
                BalanceDetail.Text = string.Empty;
                ApiKeyStatus.Text = "API error — check key";
                AccountLine.Text = string.Empty;
                UsageToday.Text = string.Empty;
                UsageMonth.Text = string.Empty;
                UsageTopModel.Text = string.Empty;
                UsageHint.Text = string.Empty;
            });
        }
    }

    public void ApplySettings()
    {
        Opacity = SettingsStore.LoadOpacity();
        string edge = SettingsStore.LoadEdge();
        if (edge != _edge)
        {
            _edge = edge;
            DockToEdge();
        }
        RefreshAll();
    }

    private void SetBarHeight(double targetHeight)
    {
        _barHeight = targetHeight;
        if (_isExpanded) return;
        DoubleAnimation anim = new DoubleAnimation(targetHeight, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(HeightProperty, anim);
    }

    private static SolidColorBrush BrushForBalance(decimal balance)
    {
        double ratio = balance <= 0m ? 0d : Math.Min(1d, (double)(balance / FullBalance));
        double t = Math.Sqrt(ratio);
        Color full = Color.FromRgb(0x22, 0xC5, 0x5E);
        Color empty = Color.FromRgb(0xEF, 0x44, 0x44);
        byte r = (byte)(Math.Min(255, empty.R + (full.R - empty.R) * t));
        byte g = (byte)(Math.Min(255, empty.G + (full.G - empty.G) * t));
        byte b = (byte)(Math.Min(255, empty.B + (full.B - empty.B) * t));
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private static LinearGradientBrush BarGradient(decimal balance)
    {
        double ratio = Math.Max(0d, Math.Min(1d, (double)(balance / FullBalance)));
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 1), EndPoint = new Point(0.5, 0)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), ratio));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x1E, 0x2A, 0x44), ratio));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x1E, 0x2A, 0x44), 1.00));
        return brush;
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        CollapsePanel();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAll();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow w = new SettingsWindow();
        w.ShowDialog();
        ApplySettings();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void MainWindow_SourceInitialized(object? sender, System.EventArgs e)
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;
        const int GWL_EXSTYLE = -20;
        const uint WS_EX_TOOLWINDOW = 0x00000080;
        int exStyle = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | (int)WS_EX_TOOLWINDOW);
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}