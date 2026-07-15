using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

internal static class FakeElectron
{
    private static int Main()
    {
        var projectDirectory = Environment.CurrentDirectory;
        var versionPath = Path.Combine(projectDirectory, "version.txt");
        var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "unknown";
        var recovery = Environment.GetEnvironmentVariable("CODEX_UPDATE_RECOVERY") == "1";
        var logPath = Environment.GetEnvironmentVariable("FAKE_ELECTRON_LOG");
        if (!string.IsNullOrEmpty(logPath))
        {
            File.AppendAllText(
                logPath,
                version + "|" + recovery + "|" + Process.GetCurrentProcess().Id + Environment.NewLine,
                new UTF8Encoding(false)
            );
        }

        if (string.Equals(
            version,
            Environment.GetEnvironmentVariable("FAKE_ELECTRON_FAIL_VERSION"),
            StringComparison.Ordinal
        ))
        {
            return 12;
        }

        var readyPath = Environment.GetEnvironmentVariable("CODEX_UPDATE_READY_FILE");
        var token = Environment.GetEnvironmentVariable("CODEX_UPDATE_TOKEN");
        if (!string.IsNullOrEmpty(readyPath) && !string.IsNullOrEmpty(token))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(readyPath));
            File.WriteAllText(readyPath, token, new UTF8Encoding(false));
        }
        if (string.Equals(
            version,
            Environment.GetEnvironmentVariable("FAKE_ELECTRON_UNSTABLE_VERSION"),
            StringComparison.Ordinal
        ))
        {
            Thread.Sleep(500);
            return 13;
        }
        Thread.Sleep(30000);
        return 0;
    }
}
