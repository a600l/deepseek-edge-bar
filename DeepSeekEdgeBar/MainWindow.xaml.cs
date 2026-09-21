using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DeepSeekEdgeBar;

public partial class MainWindow : Window
{
    // The visible strip is 6px; the window is 12px so there is a 6px transparent grab
    // area facing the desktop.
    private const double CollapsedWidth = 12;
    private const double ExpandedWidth = 260;
    private const double BarHeight = 220;
    private const double PeakBarHeight = 320;
    private const double ExpandedHeight = 460;
    private const double DragThreshold = 6.0;
    private const int RefreshMs = 60000;
    // Arbitrary, but must be unique among this window's hotkeys.
    private const int HotkeyId = 0xD5B;

    // Budget for one refresh. The usage endpoint alone is two sequential HTTP calls at
    // 15s each, so this must stay above ~30s while remaining under the 60s timer.
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(45);

    // The model list changes a few times a year; re-fetching it every minute is waste.
    private static readonly TimeSpan ModelCacheLifetime = TimeSpan.FromMinutes(30);

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _peakTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SolidColorBrush _greenBrush;
    private readonly SolidColorBrush _peakBrush;
    private readonly SolidColorBrush _errorBrush;
    private readonly SolidColorBrush _idleBrush;

    private List<string> _models = new();
    private DateTime _modelsFetchedUtc = DateTime.MinValue;

    // Peak state is recomputed every second from the clock alone, so these track what is
    // currently on screen and let the tick skip redundant work.
    private bool _hasLiveBalance;
    private bool? _lastPeak;

    private bool _isExpanded;
    private bool _isRefreshing;
    private bool _clickOnStrip;
    private bool _suppressDeactivateCollapse;
    private Point _mouseDownPos;
    private string _edge;
    private double _barHeight = BarHeight;
    private double _stripOpacity = 1.0;
    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        _greenBrush = (SolidColorBrush)FindResource("BarStatusGreenBrush");
        _peakBrush = (SolidColorBrush)FindResource("BarStatusPeakBrush");
        _errorBrush = (SolidColorBrush)FindResource("BarStatusErrorBrush");
        _idleBrush = (SolidColorBrush)FindResource("BarStatusIdleBrush");

        Height = BarHeight;
        ApplyStripOpacity(SettingsStore.LoadOpacity());
        _edge = SettingsStore.LoadEdge();
        AlignToEdge();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshMs) };
        _refreshTimer.Tick += (s, ev) => _ = RefreshAllAsync();
        // Local-only tick: the refresh timer runs once a minute, which is far too coarse
        // for a countdown and would leave the PEAK badge up to a minute stale across a
        // boundary. This one touches no network.
        _peakTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _peakTimer.Tick += (s, ev) => PeakTick();
        MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;
        MouseLeftButtonUp += MainWindow_MouseLeftButtonUp;
        SizeChanged += MainWindow_SizeChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        Deactivated += MainWindow_Deactivated;
        Closed += MainWindow_Closed;
        SettingsWindow.OpacityPreviewRequested += value => { if (IsLoaded) ApplyStripOpacity(value); };
    }

    /// <summary>
    /// The opacity setting dims only the docked strip — its purpose is an unobtrusive
    /// always-there peek. Applying it to the Window would also fade the expanded panel,
    /// which is the opposite of what you want while reading it.
    /// </summary>
    private void ApplyStripOpacity(double value)
    {
        _stripOpacity = Math.Clamp(value, 0.2, 1.0);
        collapsedStrip.Opacity = _stripOpacity;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        _refreshTimer.Start();
        _peakTimer.Start();
        _ = RefreshAllAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _peakTimer.Stop();
        UnregisterHotkey();
        _hwndSource?.RemoveHook(WindowProc);
        // Cancel only. Disposing here would race with a refresh that is mid-flight and
        // still holding a linked token; the process is exiting anyway.
        _lifetime.Cancel();
    }

    // Collapsing on deactivate is the point of the widget, but opening the settings
    // dialog deactivates us too — without this the panel would snap shut behind it.
    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivateCollapse) return;
        if (_isExpanded) CollapsePanel();
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isExpanded)
        {
            CollapsePanel();
            e.Handled = true;
        }
        else if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _ = RefreshAllAsync();
            e.Handled = true;
        }
        else if ((e.Key == Key.Enter || e.Key == Key.Space) && !_isExpanded && collapsedStrip.IsKeyboardFocused)
        {
            ExpandPanel();
            e.Handled = true;
        }
    }

    private void CollapsedStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        // Brighten relative to the configured level so hovering still feels like feedback
        // when the strip has been dimmed.
        collapsedStrip.Opacity = Math.Min(1.0, _stripOpacity + 0.15);
        Grip.Visibility = Visibility.Visible;
    }

    private void CollapsedStrip_MouseLeave(object sender, MouseEventArgs e)
    {
        collapsedStrip.Opacity = _stripOpacity;
        Grip.Visibility = Visibility.Collapsed;
    }

    private void AlignToEdge()
    {
        // Both the strip and the panel hug the docked edge, so the collapse animation
        // slides off-screen rather than away from it.
        bool left = SettingsStore.IsLeftEdge();
        collapsedStrip.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        expandedPanel.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void ExpandPanel()
    {
        if (_isExpanded) return;
        _isExpanded = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        expandedPanel.Visibility = Visibility.Visible;
        // The strip sits at the same screen edge as the panel; leaving it up draws a
        // coloured sliver over the panel's edge.
        collapsedStrip.Visibility = Visibility.Collapsed;
        // Height is assigned rather than animated: an animated Height on this Window does
        // not take effect, which silently clipped the panel to the collapsed bar height.
        Height = ExpandedHeight;
        BeginAnimation(WidthProperty, new DoubleAnimation(ExpandedWidth, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
    }

    private void CollapsePanel()
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        collapsedStrip.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var widthAnim = new DoubleAnimation(CollapsedWidth, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = ease
        };
        widthAnim.Completed += (s, ev) =>
        {
            if (_isExpanded) return;
            // Collapsed rather than Hidden: the panel's natural height would otherwise
            // still drive the window's layout and keep the strip at full height.
            expandedPanel.Visibility = Visibility.Collapsed;
        };
        Height = _barHeight;
        BeginAnimation(WidthProperty, widthAnim);
    }

    private void PositionWindow() => DockToEdge();

    // Known limitation: SystemParameters.WorkArea always describes the primary monitor,
    // so the bar docks to the primary display even when moved elsewhere. Making this
    // monitor-aware needs MonitorFromWindow + GetMonitorInfo plus a DPI conversion.
    // Docks to the work area of whichever monitor the window currently sits on rather
    // than the primary display. rcWork from GetMonitorInfo is in physical pixels, so it
    // is divided by the monitor's scale factor to land in WPF DIPs.
    private void DockToEdge()
    {
        if (ActualWidth <= 0d || ActualHeight <= 0d) return;
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO();
        info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        double scaleX = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double scaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        double workLeft = info.rcWork.Left / scaleX;
        double workTop = info.rcWork.Top / scaleY;
        double workRight = info.rcWork.Right / scaleX;
        double workBottom = info.rcWork.Bottom / scaleY;

        Left = SettingsStore.IsLeftEdge() ? workLeft : workRight - ActualWidth;
        Top = workTop + ((workBottom - workTop) - ActualHeight) / 2;
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => DockToEdge();

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

        _clickOnStrip = ContainsStrip(e.OriginalSource as DependencyObject);
        _mouseDownPos = e.GetPosition(this);
    }

    private static bool ContainsStrip(DependencyObject? origin)
    {
        while (origin != null)
        {
            if (origin is Border b && b.Name == "collapsedStrip") return true;
            origin = VisualTreeHelper.GetParent(origin);
        }
        return false;
    }

    private void MainWindow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.GetPosition(this) - _mouseDownPos).Length >= DragThreshold) return;
        if (!_isExpanded) { ExpandPanel(); return; }
        if (_clickOnStrip) CollapsePanel();
    }

    /// <summary>
    /// Refreshes balance, models and usage. Safe to call concurrently: overlapping calls
    /// return immediately rather than interleaving their UI updates. Never throws.
    /// </summary>
    public async Task RefreshAllAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cts.CancelAfter(RefreshTimeout);
        try
        {
            await RefreshCoreAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Window closing, or the budget elapsed — leave the last good values up.
        }
        catch
        {
            ShowOfflineState();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    // Every await here resumes on the UI thread (nothing in this class uses
    // ConfigureAwait(false)), so the updates below need no Dispatcher marshalling.
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        string apiKey = SettingsStore.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowNoApiKeyState();
            return;
        }

        var client = new DeepSeekApiClient(apiKey);
        Task<BalanceInfo> balanceTask = client.GetBalanceAsync(ct);
        Task<List<string>> modelsTask = GetModelsSafeAsync(client, ct);
        await Task.WhenAll(balanceTask, modelsTask).ConfigureAwait(true);

        // Balance is the headline number so it stays fatal; models are decoration and
        // must not blank the panel when they fail.
        BalanceInfo info = await balanceTask.ConfigureAwait(true);
        List<string> models = await modelsTask.ConfigureAwait(true);

        string usageToday = string.Empty;
        string usageMonth = string.Empty;
        string usageTopModel = string.Empty;
        string usageHint = string.Empty;
        string platformToken = SettingsStore.LoadPlatformSessionToken();
        if (!string.IsNullOrWhiteSpace(platformToken))
        {
            try
            {
                UsageSnapshot usage = await new DeepSeekUsageClient()
                    .GetUsageAsync(platformToken, ct).ConfigureAwait(true);
                // Usage is bursty, so on most days "today" is legitimately zero. Say so
                // rather than borrowing another day's numbers for the label.
                bool todayHasUsage = usage.TodayTokens > 0 || usage.TodayRequests > 0;
                usageToday = todayHasUsage
                    ? $"TODAY  {usage.TodayTokens:N0} tok · {usage.TodayRequests:N0} req · ${usage.TodayCost:F4}"
                    : "TODAY  no usage yet";
                usageMonth = $"MONTH  {usage.MonthTokens:N0} tok · ${usage.MonthCost:F4}";
                if (!string.IsNullOrWhiteSpace(usage.TopModel))
                    usageTopModel = $"TOP  {usage.TopModel}";
                if (!todayHasUsage && usage.LastActiveDate is { Length: 10 })
                    usageHint = $"last used {FormatShortDate(usage.LastActiveDate)}";
            }
            catch (DeepSeekUsageAuthException)
            {
                usageHint = "usage: token expired";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                usageHint = "usage: unavailable";
            }
        }

        ApplyLiveState(info, models, usageToday, usageMonth, usageTopModel, usageHint);
    }

    private async Task<List<string>> GetModelsSafeAsync(DeepSeekApiClient client, CancellationToken ct)
    {
        if (_models.Count > 0 && DateTime.UtcNow - _modelsFetchedUtc < ModelCacheLifetime)
            return _models;
        try
        {
            List<string> fetched = await client.GetModelsAsync(ct).ConfigureAwait(true);
            _models = fetched;
            _modelsFetchedUtc = DateTime.UtcNow;
            return fetched;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return _models;
        }
    }

    private void ShowNoApiKeyState()
    {
        _hasLiveBalance = false;
        _lastPeak = null;
        collapsedStrip.Background = _idleBrush;
        StatusDot.Fill = _idleBrush;
        SetBarHeight(BarHeight);
        StatusValue.Text = "no API key";
        StatusCountdown.Text = string.Empty;
        BalanceValue.Text = "—";
        BalanceValue.Foreground = _idleBrush;
        BalanceDetail.Text = string.Empty;
        BalanceDetail.Foreground = _idleBrush;
        ApiKeyStatus.Text = "No API key — open settings (⚙)";
        ModelValue.Text = "—";
        ClearUsageRows();
    }

    private void ShowOfflineState()
    {
        _hasLiveBalance = false;
        _lastPeak = null;
        collapsedStrip.Background = _errorBrush;
        StatusDot.Fill = _errorBrush;
        SetBarHeight(BarHeight);
        StatusValue.Text = "offline";
        StatusCountdown.Text = string.Empty;
        BalanceValue.Text = "—";
        BalanceDetail.Text = string.Empty;
        ApiKeyStatus.Text = "API error — check key";
        ModelValue.Text = "—";
        LastRefresh.Text = "refresh failed";
        ClearUsageRows();
    }

    /// <summary>
    /// "2026-09-21" to "Sep 21" — the platform's day key is ISO, which is too wide for the hint line.
    /// </summary>
    private static string FormatShortDate(string isoDate)
    {
        return DateTime.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed.ToString("MMM d", CultureInfo.InvariantCulture)
            : isoDate;
    }

    private void ClearUsageRows()
    {
        UsageToday.Text = string.Empty;
        UsageMonth.Text = string.Empty;
        UsageTopModel.Text = string.Empty;
        UsageHint.Text = string.Empty;
    }

    /// <summary>
    /// Per-second peak/off-peak tick. Reads the clock and nothing else, so the countdown
    /// advances smoothly and the badge flips within a second of the boundary instead of
    /// waiting for the next 60s refresh. No-ops whenever the panel is not showing a live
    /// balance, so it can never overwrite an error or "no API key" state.
    /// </summary>
    private void PeakTick()
    {
        if (!_hasLiveBalance) return;
        DateTime utc = DateTime.UtcNow;
        bool peak = PeakHoursService.IsPeakTime(utc);
        if (_lastPeak != peak)
        {
            _lastPeak = peak;
            StatusDot.Fill = peak ? _peakBrush : _greenBrush;
            SetBarHeight(peak ? PeakBarHeight : BarHeight);
            StatusValue.Text = peak ? "PEAK · 2× rates" : "OFF-PEAK · lowest rates";
        }
        StatusCountdown.Text = (peak ? "off in " : "peak in ")
            + PeakHoursService.FormatCountdown(PeakHoursService.TimeUntilNextChange(utc));
    }

    /// <summary>
    /// Explains the pricing rule, and warns once the holiday table has run out so a stale
    /// calendar shows up as a note rather than a silently wrong PEAK badge.
    /// </summary>
    private static string PeakTooltip(DateTime utc)
    {
        const string rule = "Peak: Mon–Fri 09:00–12:00 and 14:00–18:00 Beijing time, billed at 2×. "
            + "Weekends and Chinese public holidays are off-peak all day (1×).";
        int year = PeakHoursService.ToBeijing(utc).Year;
        return MarketCalendar.Covers(year)
            ? rule
            : $"{rule}\n\n⚠ Public-holiday data ends in {MarketCalendar.LastCoveredYear} — "
              + "holidays in " + year + " are being treated as normal working days. "
              + "Add them to MarketCalendar.cs.";
    }

    private void ApplyLiveState(
        BalanceInfo info,
        List<string> models,
        string usageToday,
        string usageMonth,
        string usageTopModel,
        string usageHint)
    {
        DateTime utc = DateTime.UtcNow;
        bool accountProblem = !info.IsAvailable || info.TotalBalance <= 0;
        decimal fullAmount = SettingsStore.LoadFullBarAmount();
        StatusCard.ToolTip = PeakTooltip(utc);

        if (accountProblem)
        {
            _hasLiveBalance = false;
            _lastPeak = null;
            collapsedStrip.Background = _errorBrush;
            StatusDot.Fill = _errorBrush;
            SetBarHeight(BarHeight);
            StatusValue.Text = "balance unavailable";
            StatusCountdown.Text = string.Empty;
            BalanceValue.Text = "—";
            BalanceValue.Foreground = _errorBrush;
            BalanceDetail.Foreground = _errorBrush;
            BalanceDetail.Text = $"unavailable · granted {info.Currency} {info.GrantedBalance:0.00} · topped-up {info.Currency} {info.ToppedUpBalance:0.00}";
            ApiKeyStatus.Text = string.Empty;
        }
        else
        {
            SolidColorBrush balanceBrush = BrushForBalance(info.TotalBalance, fullAmount);
            collapsedStrip.Background = BarGradient(info.TotalBalance, fullAmount);
            BalanceValue.Text = $"{info.Currency} {info.TotalBalance:0.00}";
            BalanceValue.Foreground = balanceBrush;
            BalanceDetail.Foreground = balanceBrush;
            BalanceDetail.Text = $"available · granted {info.Currency} {info.GrantedBalance:0.00} · topped-up {info.Currency} {info.ToppedUpBalance:0.00}";
            ApiKeyStatus.Text = string.Empty;
            // Hand the badge, bar height and countdown to the per-second tick so this
            // refresh and the tick can never disagree about the current state.
            _hasLiveBalance = true;
            _lastPeak = null;
            PeakTick();
        }

        string modelList = models.Count > 0 ? string.Join(" · ", models) : "—";
        ModelValue.Text = modelList;
        // The line trims on overflow, so keep the full list reachable.
        ModelValue.ToolTip = models.Count > 0 ? "Models: " + modelList : null;
        LastRefresh.Text = "refreshed " + DateTime.Now.ToString("HH:mm:ss");
        UsageToday.Text = usageToday;
        UsageMonth.Text = usageMonth;
        UsageTopModel.Text = usageTopModel;
        UsageHint.Text = usageHint;
    }

    public void ApplySettings()
    {
        ApplyStripOpacity(SettingsStore.LoadOpacity());
        string edge = SettingsStore.LoadEdge();
        if (edge != _edge)
        {
            _edge = edge;
            AlignToEdge();
            DockToEdge();
        }
        ApplyHotkeySetting();
        _ = RefreshAllAsync();
    }

    private void SetBarHeight(double targetHeight)
    {
        _barHeight = targetHeight;
        collapsedStrip.Height = targetHeight;
        if (_isExpanded) return;
        Height = targetHeight;
    }

    // Red at empty, green at full, with a sqrt ease so small balances still move the hue.
    private SolidColorBrush BrushForBalance(decimal balance, decimal fullAmount)
    {
        double ratio = balance <= 0m ? 0d : Math.Min(1d, (double)(balance / fullAmount));
        double t = Math.Sqrt(ratio);
        Color full = _greenBrush.Color;
        Color empty = _errorBrush.Color;
        byte r = (byte)Math.Min(255, empty.R + (full.R - empty.R) * t);
        byte g = (byte)Math.Min(255, empty.G + (full.G - empty.G) * t);
        byte b = (byte)Math.Min(255, empty.B + (full.B - empty.B) * t);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    // Two-tone strip: balance colour up to the ratio, track colour above it.
    private LinearGradientBrush BarGradient(decimal balance, decimal fullAmount)
    {
        double ratio = Math.Max(0d, Math.Min(1d, (double)(balance / fullAmount)));
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 1),
            EndPoint = new Point(0.5, 0)
        };
        brush.GradientStops.Add(new GradientStop(_greenBrush.Color, 0.00));
        brush.GradientStops.Add(new GradientStop(_greenBrush.Color, ratio));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x1E, 0x2A, 0x44), ratio));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x1E, 0x2A, 0x44), 1.00));
        return brush;
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => CollapsePanel();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivateCollapse = true;
        try
        {
            new SettingsWindow().ShowDialog();
        }
        finally
        {
            _suppressDeactivateCollapse = false;
        }
        ApplySettings();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        CollapsePanel();
        Hide();
    }

    private void MainWindow_SourceInitialized(object? sender, System.EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        int exStyle = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WindowProc);
        ApplyHotkeySetting();
    }

    /// <summary>
    /// Registers or releases Ctrl+Alt+D to match the saved setting. Called at startup and
    /// after the settings dialog closes.
    /// </summary>
    public void ApplyHotkeySetting()
    {
        if (SettingsStore.LoadToggleHotkey()) RegisterHotkey();
        else UnregisterHotkey();
    }

    private void RegisterHotkey()
    {
        if (_hotkeyRegistered || _hwndSource == null) return;
        const int MOD_ALT = 0x0001;
        const int MOD_CONTROL = 0x0002;
        const int MOD_NOREPEAT = 0x4000;
        const int VK_D = 0x44;
        // MOD_NOREPEAT stops the key auto-repeat from flickering the panel while held.
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            _hwndSource.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_D);
    }

    private void UnregisterHotkey()
    {
        if (!_hotkeyRegistered || _hwndSource == null) return;
        NativeMethods.UnregisterHotKey(_hwndSource.Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            TogglePanel();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void TogglePanel()
    {
        // "Hide" parks the bar in the tray, so the hotkey has to be able to bring it back.
        if (!IsVisible)
        {
            Show();
            ExpandPanel();
            return;
        }
        if (_isExpanded) CollapsePanel();
        else ExpandPanel();
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    internal const int MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, int dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
