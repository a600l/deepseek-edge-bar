using System;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeepSeekEdgeBar;

public partial class SettingsWindow : Window
{
    public static event Action<double>? OpacityPreviewRequested;

    private static readonly SolidColorBrush PlatformOkBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush PlatformGrayBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    public SettingsWindow()
    {
        InitializeComponent();
        // SizeToContent grows the window to fit; cap it so the dialog never runs off a
        // short screen. The ScrollViewer in the XAML absorbs the overflow.
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40);
        ApiKeyBox.Password = SettingsStore.LoadApiKey();
        OpacitySlider.Value = SettingsStore.LoadOpacity();
        StartWithWindowsCheck.IsChecked = SettingsStore.LoadStartWithWindows();
        ToggleHotkeyCheck.IsChecked = SettingsStore.LoadToggleHotkey();
        FullBarBox.Text = SettingsStore.LoadFullBarAmount().ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        OpacitySlider.ValueChanged += (s, e) =>
        {
            OpacityValue.Text = $"{Math.Round(OpacitySlider.Value * 100)}%";
            OpacityPreviewRequested?.Invoke(OpacitySlider.Value);
        };
        OpacityValue.Text = $"{Math.Round(OpacitySlider.Value * 100)}%";
        PlatformTokenBox.Text = SettingsStore.LoadPlatformSessionToken();
        if (PlatformTokenBox.Text.Length > 0)
            PlatformStatus.Text = "Platform token configured — usage stats enabled.";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void SettingsWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.SaveApiKey(ApiKeyBox.Password.Trim());
        SettingsStore.SaveOpacity(OpacitySlider.Value);
        SettingsStore.SaveStartWithWindows(StartWithWindowsCheck.IsChecked == true);
        SettingsStore.SaveToggleHotkey(ToggleHotkeyCheck.IsChecked == true);
        if (decimal.TryParse(FullBarBox.Text.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal fullBar) && fullBar > 0m)
            SettingsStore.SaveFullBarAmount(fullBar);
        DialogResult = true;
        Close();
    }

    private async void UsePlatformTokenButton_Click(object sender, RoutedEventArgs e)
    {
        string token = PlatformTokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            PlatformStatus.Foreground = PlatformGrayBrush;
            PlatformStatus.Text = "Paste a userToken first — copy it from your logged-in browser.";
            return;
        }
        UsePlatformTokenButton.IsEnabled = false;
        UsePlatformTokenButton.Content = "Verifying…";
        PlatformStatus.Foreground = PlatformGrayBrush;
        try
        {
            await new DeepSeekUsageClient().GetUsageAsync(token);
            if (!IsLoaded) return;
            SettingsStore.SavePlatformSessionToken(token);
            PlatformTokenBox.Text = string.Empty;
            PlatformStatus.Foreground = PlatformOkBrush;
            PlatformStatus.Text = "Token accepted — usage stats enabled.";
        }
        catch (DeepSeekUsageAuthException)
        {
            if (!IsLoaded) return;
            PlatformStatus.Foreground = PlatformGrayBrush;
            PlatformStatus.Text = "Token invalid or expired — copy a fresh platform userToken";
        }
        catch (HttpRequestException ex)
        {
            if (!IsLoaded) return;
            PlatformStatus.Foreground = PlatformGrayBrush;
            PlatformStatus.Text = ex.Message;
        }
        catch (Exception)
        {
            if (!IsLoaded) return;
            PlatformStatus.Foreground = PlatformGrayBrush;
            PlatformStatus.Text = "Token check failed.";
        }
        finally
        {
            if (IsLoaded)
            {
                UsePlatformTokenButton.IsEnabled = true;
                UsePlatformTokenButton.Content = "Use platform token";
            }
        }
    }
}