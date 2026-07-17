using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CodexUsageTray
{
    internal static class NativeBuildManifest
    {
        internal static readonly string[] InputFiles =
        {
            "global.json",
            "src/CodexUsageTray.csproj",
            "src/app.manifest",
            "src/BuildManifest.cs",
            "src/NativeApplication.cs",
            "src/StartupRegistration.cs",
            "src/NativeSettings.cs",
            "src/NativeMethods.cs",
            "src/WidgetWindow.cs",
            "src/TelemetryPanel.cs",
            "src/TelemetryWindow.cs",
            "src/NativeTypes.cs",
            "src/LatrixIntegration.cs",
            "src/UpdateService.cs",
            "src/launcher/Program.cs",
            "src/launcher/build.ps1",
            "src/launcher/icon.ico",
        };

        internal static readonly string[] RequiredFiles =
        {
            "global.json",
            "src/CodexUsageTray.csproj",
            "src/app.manifest",
            "src/BuildManifest.cs",
            "src/NativeApplication.cs",
            "src/StartupRegistration.cs",
            "src/NativeSettings.cs",
            "src/NativeMethods.cs",
            "src/WidgetWindow.cs",
            "src/TelemetryPanel.cs",
            "src/TelemetryWindow.cs",
            "src/NativeTypes.cs",
            "src/LatrixIntegration.cs",
            "src/UpdateService.cs",
            "src/launcher/Program.cs",
            "src/launcher/build.ps1",
            "src/launcher/icon.ico",
        };

        internal static string ComputeHash(string repositoryRoot)
        {
            ValidateRequiredFiles(repositoryRoot);
            var manifest = new StringBuilder();
            foreach (var relativePath in InputFiles)
            {
                var path = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) throw new FileNotFoundException("Native build input is missing.", path);
                manifest.Append(relativePath).Append('\0');
                if (relativePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    manifest.Append(Convert.ToBase64String(File.ReadAllBytes(path)));
                else
                    manifest.Append(NormalizeText(File.ReadAllText(path)));
                manifest.Append('\0');
            }

            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(
                    sha256.ComputeHash(Encoding.UTF8.GetBytes(manifest.ToString()))
                ).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static void ValidateRequiredFiles(string repositoryRoot)
        {
            foreach (var relativePath in RequiredFiles)
            {
                var path = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) throw new FileNotFoundException("Native build input is missing.", path);
            }
        }

        internal static string NormalizeText(string value)
        {
            return value.Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
