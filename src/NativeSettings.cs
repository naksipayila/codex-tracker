using System;
using System.IO;
using System.Text.Json;

namespace CodexUsageTray;

internal sealed class NativeSettings
{
    public double XRatio { get; set; } = 0.3;
    public bool LaunchAtStartup { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool ShowFiveHour { get; set; } = true;
    public bool ShowWeekly { get; set; } = true;

    public static NativeSettings Load()
    {
        var settings = new NativeSettings();
        try
        {
            var widgetPath = Path.Combine(GetUserDataDirectory(), "widget-position.json");
            if (File.Exists(widgetPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(widgetPath));
                var root = document.RootElement;
                if (root.TryGetProperty("xRatio", out var ratio) && ratio.TryGetDouble(out var value))
                    settings.XRatio = Math.Clamp(value, 0, 1);
                if (root.TryGetProperty("launchAtStartup", out var launch) &&
                    (launch.ValueKind == JsonValueKind.True || launch.ValueKind == JsonValueKind.False))
                    settings.LaunchAtStartup = launch.GetBoolean();
                if (root.TryGetProperty("hideInFullscreen", out var hide) &&
                    (hide.ValueKind == JsonValueKind.True || hide.ValueKind == JsonValueKind.False))
                    settings.HideInFullscreen = hide.GetBoolean();
                if (root.TryGetProperty("showFiveHour", out var showFiveHour) &&
                    (showFiveHour.ValueKind == JsonValueKind.True || showFiveHour.ValueKind == JsonValueKind.False))
                    settings.ShowFiveHour = showFiveHour.GetBoolean();
                if (root.TryGetProperty("showWeekly", out var showWeekly) &&
                    (showWeekly.ValueKind == JsonValueKind.True || showWeekly.ValueKind == JsonValueKind.False))
                    settings.ShowWeekly = showWeekly.GetBoolean();
            }
        }
        catch
        {
        }

        try { File.Delete(Path.Combine(GetUserDataDirectory(), "update-preferences.json")); } catch { }
        return settings;
    }

    public void SaveWidget()
    {
        WriteJsonAtomically(Path.Combine(GetUserDataDirectory(), "widget-position.json"), new
        {
            xRatio = Math.Clamp(XRatio, 0, 1),
            launchAtStartup = LaunchAtStartup,
            hideInFullscreen = HideInFullscreen,
            showFiveHour = ShowFiveHour,
            showWeekly = ShowWeekly,
        });
    }

    public static string GetUserDataDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA");
        var directory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "codex-usage-tray")
            : Path.GetFullPath(overrideDirectory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }
}
