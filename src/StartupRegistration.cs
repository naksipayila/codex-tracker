using System;
using System.IO;
using Microsoft.Win32;

namespace CodexUsageTray
{
    internal static class StartupRegistration
    {
        private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CodexUsageTray";
        private const string LegacyShortcutName = "Codex Usage Tray.lnk";

        public static bool IsEnabled(string applicationDirectory)
        {
            var configuredPath = ReadConfiguredPath();
            if (string.IsNullOrWhiteSpace(configuredPath)) return false;
            return PathsEqual(configuredPath, Path.Combine(applicationDirectory, "CodexTracker.exe"));
        }

        public static bool HasAnyRegistration()
        {
            return !string.IsNullOrWhiteSpace(ReadConfiguredPath()) || File.Exists(GetLegacyShortcutPath());
        }

        public static void SetEnabled(string applicationDirectory, bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true))
            {
                if (key == null) throw new InvalidOperationException("The Windows startup registry key could not be opened.");
                if (enabled)
                    key.SetValue(ValueName, "\"" + Path.Combine(applicationDirectory, "CodexTracker.exe") + "\"",
                        RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }

            try { File.Delete(GetLegacyShortcutPath()); } catch { }
        }

        private static string ReadConfiguredPath()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
            {
                var value = key == null ? null : key.GetValue(ValueName) as string;
                if (string.IsNullOrWhiteSpace(value)) return null;
                value = value.Trim();
                if (value.Length >= 2 && value[0] == '"')
                {
                    var closingQuote = value.IndexOf('"', 1);
                    if (closingQuote > 1) value = value.Substring(1, closingQuote - 1);
                }
                return value;
            }
        }

        private static string GetLegacyShortcutPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup", LegacyShortcutName);
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
