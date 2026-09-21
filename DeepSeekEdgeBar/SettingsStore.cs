using System;
using System.Globalization;
using Microsoft.Win32;

namespace DeepSeekEdgeBar;

public static class SettingsStore
{
    private const string SubKey = @"Software\DeepSeekEdgeBar";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string LoadApiKey()
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey);
        return k?.GetValue("ApiKey", "")?.ToString() ?? "";
    }
    public static void SaveApiKey(string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(SubKey);
        k?.SetValue("ApiKey", value ?? "");
    }

    public static double LoadOpacity() => ReadDouble("Opacity", 1.0);
    public static void SaveOpacity(double value) => WriteDouble("Opacity", value);

    public static bool LoadStartWithWindows() => ReadBool("StartWithWindows", false);
    public static void SaveStartWithWindows(bool value)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (value)
        {
            var path = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            k?.SetValue("DeepSeekEdgeBar", $"\"{path}\"");
        }
        else
        {
            k?.DeleteValue("DeepSeekEdgeBar", false);
        }
        WriteBool("StartWithWindows", value);
    }

    // Ctrl+Alt+D is a global hotkey, so it is off unless the user asks for it.
    public static bool LoadToggleHotkey() => ReadBool("ToggleHotkey", false);
    public static void SaveToggleHotkey(bool value) => WriteBool("ToggleHotkey", value);

    private const string PlatformSessionToken = "PlatformSessionToken";
    public static string LoadPlatformSessionToken() => ReadString(PlatformSessionToken, "");
    public static void SavePlatformSessionToken(string value) => WriteString(PlatformSessionToken, value ?? "");

    public static decimal LoadFullBarAmount() => (decimal)ReadDouble("FullBarAmount", 10.0);
    public static void SaveFullBarAmount(decimal value)
        => WriteDouble("FullBarAmount", Math.Clamp((double)value, 0.01, 1000000.0));

    public static string LoadEdge() => ReadString("Edge", "Right");
    public static void SaveEdge(string value) => WriteString("Edge", value == "Left" ? "Left" : "Right");
    public static bool IsLeftEdge() => LoadEdge() == "Left";

    private static string ReadString(string name, string def)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(SubKey); return k?.GetValue(name, def)?.ToString() ?? def; }
        catch { return def; }
    }
    private static void WriteString(string name, string value)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(SubKey); k?.SetValue(name, value); } catch { }
    }
    private static double ReadDouble(string name, double def)
    {
        var s = ReadString(name, def.ToString(CultureInfo.InvariantCulture));
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
    }
    private static void WriteDouble(string name, double value)
        => WriteString(name, value.ToString("F2", CultureInfo.InvariantCulture));
    private static bool ReadBool(string name, bool def)
        => bool.TryParse(ReadString(name, def ? "true" : "false"), out var v) ? v : def;
    private static void WriteBool(string name, bool value)
        => WriteString(name, value ? "true" : "false");
}