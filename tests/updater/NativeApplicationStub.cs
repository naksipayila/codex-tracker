using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace CodexUsageTray
{
    internal static class NativeApplication
    {
        public static void Run(string applicationDirectory, bool isolated = false)
        {
            var versionPath = Path.Combine(applicationDirectory, "src", "version.txt");
            var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "unknown";
            var recovery = Environment.GetEnvironmentVariable("CODEX_UPDATE_RECOVERY") == "1";
            var logPath = Environment.GetEnvironmentVariable("FAKE_APPLICATION_LOG");
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
                Environment.GetEnvironmentVariable("FAKE_APPLICATION_FAIL_VERSION"),
                StringComparison.Ordinal
            )) return;

            var readyPath = Environment.GetEnvironmentVariable("CODEX_UPDATE_READY_FILE");
            var token = Environment.GetEnvironmentVariable("CODEX_UPDATE_TOKEN");
            if (!string.IsNullOrEmpty(readyPath) && !string.IsNullOrEmpty(token))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(readyPath));
                File.WriteAllText(readyPath, token, new UTF8Encoding(false));
            }
            if (string.Equals(
                version,
                Environment.GetEnvironmentVariable("FAKE_APPLICATION_UNSTABLE_VERSION"),
                StringComparison.Ordinal
            ))
            {
                Thread.Sleep(500);
                return;
            }
            Thread.Sleep(30000);
        }
    }
}
