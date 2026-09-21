using System;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeepSeekEdgeBar;

public partial class SettingsWindow : Window
{
    private static readonly SolidColorBrush PlatformOkBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush PlatformGrayBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    public SettingsWindow()
    {
        InitializeComponent();
        AccountEmailBox.Text = SettingsStore.LoadAccountEmail();
        RefreshAccountState();
        Loaded += SettingsWindow_Loaded;
        ApiKeyBox.Password = SettingsStore.LoadApiKey();
        OpacitySlider.Value = SettingsStore.LoadOpacity();
        HideToTrayCheck.IsChecked = SettingsStore.LoadHideToTray();
        StartWithWindowsCheck.IsChecked = SettingsStore.LoadStartWithWindows();
        EdgeCombo.SelectedIndex = SettingsStore.LoadEdge() == "Left" ? 1 : 0;
        OpacitySlider.ValueChanged += (s, e) => OpacityValue.Text = $"{Math.Round(OpacitySlider.Value * 100)}%";
        OpacityValue.Text = $"{Math.Round(OpacitySlider.Value * 100)}%";
        PlatformTokenBox.Text = SettingsStore.LoadPlatformSessionToken();
        if (PlatformTokenBox.Text.Length > 0)
            PlatformStatus.Text = "Platform token configured — usage stats enabled.";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.SaveApiKey(ApiKeyBox.Password.Trim());
        SettingsStore.SaveOpacity(OpacitySlider.Value);
        SettingsStore.SaveHideToTray(HideToTrayCheck.IsChecked == true);
        SettingsStore.SaveStartWithWindows(StartWithWindowsCheck.IsChecked == true);
        SettingsStore.SaveEdge((EdgeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Right");
        DialogResult = true;
        Close();
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        string email = AccountEmailBox.Text.Trim();
        string password = AccountPasswordBox.Password;
        SignInButton.IsEnabled = false;
        SignInButton.Content = "Signing in…";
        AccountStatus.Text = "Signing in…";
        try
        {
            var client = new DeepSeekAccountClient();
            var session = await client.LoginAsync(email, password);
            string token = session.Token;
            try { token = await client.CheckDeviceAsync(token) ?? token; } catch { }
            try
            {
                var current = await client.GetCurrentAsync(token);
                if (!string.IsNullOrEmpty(current.Token)) token = current.Token;
            }
            catch { }
            SettingsStore.SaveSessionToken(token);
            SettingsStore.SaveAccountEmail(string.IsNullOrWhiteSpace(session.Email) ? email : session.Email);
            SettingsStore.SaveAccountUserId(session.UserId);
            AccountPasswordBox.Password = string.Empty;
            ResetSignInUI();
            RefreshAccountState();
        }
        catch (DeepSeekAuthException ex)
        {
            ResetSignInUI();
            AccountStatus.Text = ex.Message;
        }
        catch (HttpRequestException)
        {
            ResetSignInUI();
            AccountStatus.Text = "Sign-in failed — check your connection.";
        }
        catch (Exception)
        {
            ResetSignInUI();
            AccountStatus.Text = "Sign-in failed.";
        }
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.ClearAccount();
        AccountPasswordBox.Password = string.Empty;
        TokenBox.Text = string.Empty;
        RefreshAccountState();
        AccountStatus.Text = "Signed out";
    }

    private async void UseTokenButton_Click(object sender, RoutedEventArgs e)
    {
        string token = TokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            AccountStatus.Text = "Paste a userToken first — copy it from your logged-in browser.";
            return;
        }
        UseTokenButton.IsEnabled = false;
        UseTokenButton.Content = "Verifying…";
        try
        {
            var session = await new DeepSeekAccountClient().GetCurrentAsync(token);
            if (!IsLoaded) return;
            SettingsStore.SaveSessionToken(RefreshIfAvailable(session.Token, token));
            SettingsStore.SaveAccountEmail(string.IsNullOrWhiteSpace(session.Email) ? "" : session.Email);
            SettingsStore.SaveAccountUserId(session.UserId);
            if (!string.IsNullOrWhiteSpace(session.Name)) SettingsStore.SaveDisplayName(session.Name);
            if (!string.IsNullOrWhiteSpace(session.Provider)) SettingsStore.SaveProvider(session.Provider);
            TokenBox.Text = string.Empty;
            RefreshAccountState();
            string email = SettingsStore.LoadAccountEmail();
            AccountStatus.Text = BuildSignedInLabel(session.Name, email, session.Provider);
        }
        catch (DeepSeekAuthException)
        {
            if (!IsLoaded) return;
            AccountStatus.Text = "That token is invalid or expired — copy a fresh userToken";
        }
        catch (HttpRequestException)
        {
            if (!IsLoaded) return;
            AccountStatus.Text = "Token check failed — check your connection";
        }
        catch (Exception)
        {
            if (!IsLoaded) return;
            AccountStatus.Text = "Token check failed.";
        }
        finally
        {
            if (IsLoaded)
            {
                UseTokenButton.IsEnabled = true;
                UseTokenButton.Content = "Use token";
            }
        }
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

    private static string RefreshIfAvailable(string sessionToken, string pastedToken)
        => string.IsNullOrWhiteSpace(sessionToken) ? pastedToken : sessionToken;

    private static string BuildSignedInLabel(string name, string email, string provider)
    {
        string label;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(email))
            label = $"Signed in as {name} ({email})";
        else if (!string.IsNullOrWhiteSpace(name))
            label = $"Signed in as {name}";
        else if (!string.IsNullOrWhiteSpace(email))
            label = $"Signed in as {email}";
        else
            return "Signed in (session token)";
        if (!string.IsNullOrWhiteSpace(name) &&
            string.Equals(provider, "GOOGLE", StringComparison.OrdinalIgnoreCase))
            label += " · via Google";
        return label;
    }

    private void ResetSignInUI()
    {
        SignInButton.IsEnabled = true;
        SignInButton.Content = "Sign in";
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (SettingsStore.LoadSessionToken().Length > 0)
            ValidateSessionAsync();
    }

    private async void ValidateSessionAsync()
    {
        string token = SettingsStore.LoadSessionToken();
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            var session = await new DeepSeekAccountClient().GetCurrentAsync(token);
            if (!IsLoaded) return;
            if (!string.IsNullOrWhiteSpace(session.Token))
            {
                SettingsStore.SaveSessionToken(session.Token);
                if (!string.IsNullOrWhiteSpace(session.Email)) SettingsStore.SaveAccountEmail(session.Email);
            }
            if (!string.IsNullOrWhiteSpace(session.Name)) SettingsStore.SaveDisplayName(session.Name);
            if (!string.IsNullOrWhiteSpace(session.Provider)) SettingsStore.SaveProvider(session.Provider);
            string email = SettingsStore.LoadAccountEmail();
            AccountStatus.Text = BuildSignedInLabel(session.Name, email, session.Provider);
        }
        catch (DeepSeekAuthException)
        {
            if (!IsLoaded) return;
            SettingsStore.ClearAccount();
            RefreshAccountState();
            AccountStatus.Text = "Session expired — sign in again";
        }
        catch { }
    }

    private void RefreshAccountState()
    {
        if (SettingsStore.LoadSessionToken().Length > 0)
        {
            SignInButton.Visibility = Visibility.Collapsed;
            SignOutButton.Visibility = Visibility.Visible;
            AccountEmailBox.IsEnabled = false;
            AccountPasswordBox.IsEnabled = false;
            TokenBox.IsEnabled = false;
            UseTokenButton.IsEnabled = false;
            string email = SettingsStore.LoadAccountEmail();
            AccountStatus.Text = BuildSignedInLabel(SettingsStore.LoadDisplayName(), email, SettingsStore.LoadProvider());
        }
        else
        {
            SignInButton.Visibility = Visibility.Visible;
            SignOutButton.Visibility = Visibility.Collapsed;
            AccountEmailBox.IsEnabled = true;
            AccountPasswordBox.IsEnabled = true;
            TokenBox.IsEnabled = true;
            UseTokenButton.IsEnabled = true;
            AccountStatus.Text = "Not signed in — optional";
        }
    }
}