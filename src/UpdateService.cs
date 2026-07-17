using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexUsageTray;

internal readonly record struct UpdateResult(bool Success, string Message);

internal sealed class UpdateService
{
    private readonly string applicationDirectory;
    private readonly Func<int> getKeeperProcessId;
    private readonly Action prepareForUpdate;
    private readonly Action stateChanged;
    private readonly CancellationToken applicationToken;
    private int checking;

    private const string ReleaseManifestUrl = "https://github.com/naksipayila/codex-tracker/releases/latest/download/latest.json";

    public bool IsChecking => Volatile.Read(ref checking) != 0;
    public bool RepairNeeded => File.Exists(GetPendingPath());

    public UpdateService(string applicationDirectory, Func<int> getKeeperProcessId,
        Action prepareForUpdate, Action stateChanged, CancellationToken applicationToken)
    {
        this.applicationDirectory = applicationDirectory;
        this.getKeeperProcessId = getKeeperProcessId;
        this.prepareForUpdate = prepareForUpdate;
        this.stateChanged = stateChanged;
        this.applicationToken = applicationToken;
    }

    public async Task<bool> CheckAsync(bool startup)
    {
        if (Interlocked.Exchange(ref checking, 1) != 0) return false;
        stateChanged();
        try
        {
            applicationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(Path.Combine(applicationDirectory, ".git")))
            {
                await CheckReleaseAsync(startup);
                return true;
            }
            var branch = (await RunGitAsync(["branch", "--show-current"])).Output.Trim();
            if (branch != "main") throw new InvalidOperationException($"Automatic updates require the main branch. The current branch is {branch}.");
            var status = (await RunGitAsync(["status", "--porcelain", "--untracked-files=all"])).Output.Trim();
            if (status.Length != 0) throw new InvalidOperationException("The working tree has local changes. Commit or discard them before updating.");
            var remote = (await RunGitAsync(["remote", "get-url", "origin"])).Output.Trim();
            if (!IsTrustedRemote(remote)) throw new InvalidOperationException("Automatic updates require the official GitHub remote.");
            await RunGitAsync(["fetch", "--quiet", "--no-tags", "origin", "refs/heads/main:refs/remotes/origin/main"]);
            var local = (await RunGitAsync(["rev-parse", "HEAD"])).Output.Trim();
            var target = (await RunGitAsync(["rev-parse", "refs/remotes/origin/main"])).Output.Trim();
            if (local == target && !RepairNeeded)
            {
                if (!startup) System.Windows.MessageBox.Show("Codex Tracker is up to date.", "Codex Tracker",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }

            if (local != target)
            {
                var ancestry = await RunGitCaptureAsync(["merge-base", "--is-ancestor", local, target]);
                if (ancestry.ExitCode == 1) throw new InvalidOperationException("The local main branch has diverged from origin/main.");
                if (ancestry.ExitCode != 0) throw new InvalidOperationException(GetError(ancestry, "Could not compare update commits."));
                await ValidateTargetAsync(target);
            }

            var repairing = local == target;
            var details = repairing
                ? "The previous update needs repair. The application will close, verify its native executable, and restart."
                : await GetUpdateDetailsAsync(local, target);
            var confirmation = System.Windows.MessageBox.Show(details,
                repairing ? "Repair Codex Tracker" : "Update Codex Tracker",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return true;
            await BeginUpdateAsync(local, target);
            return true;
        }
        catch (OperationCanceledException) when (applicationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception error)
        {
            if (!startup) System.Windows.MessageBox.Show(error.Message, "Automatic update is unavailable",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref checking, 0);
            stateChanged();
        }
    }

    private async Task CheckReleaseAsync(bool startup)
    {
        var manifest = await ReadReleaseManifestAsync();
        var currentVersion = GetCurrentVersion();
        if (manifest.Version < currentVersion ||
            (manifest.Version == currentVersion && !RepairNeeded))
        {
            if (!startup) System.Windows.MessageBox.Show("Codex Tracker is up to date.", "Codex Tracker",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var details = manifest.Notes.Length == 0
            ? $"Codex Tracker {manifest.Version} is available." + Environment.NewLine + Environment.NewLine +
                "The application will close, verify the update, and restart."
            : "What's new:" + Environment.NewLine + manifest.Notes + Environment.NewLine + Environment.NewLine +
                "The application will close, verify the update, and restart.";
        var confirmation = System.Windows.MessageBox.Show(
            details,
            "Update Codex Tracker",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No
        );
        if (confirmation == MessageBoxResult.Yes)
            await BeginReleaseUpdateAsync(manifest);
    }

    private async Task<ReleaseManifest> ReadReleaseManifestAsync()
    {
        using var client = CreateHttpClient();
        using var response = await client.GetAsync(ReleaseManifestUrl, applicationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(applicationToken);
        var document = JsonSerializer.Deserialize<ReleaseManifestDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (document == null || !IsReleaseVersion(document.Version, out var version) ||
            string.IsNullOrWhiteSpace(document.PackageUrl) || !IsSha256(document.Sha256))
        {
            throw new InvalidDataException("The release update manifest is invalid.");
        }
        if (!Uri.TryCreate(document.PackageUrl, UriKind.Absolute, out var packageUri) ||
            !IsTrustedPackageUri(packageUri, version))
        {
            throw new InvalidDataException("The release update package URL is not trusted.");
        }
        return new ReleaseManifest(version, packageUri, document.Sha256.ToLowerInvariant(),
            SanitizeReleaseNotes(document.Notes));
    }

    private async Task BeginReleaseUpdateAsync(ReleaseManifest manifest)
    {
        var token = Guid.NewGuid().ToString("N");
        var stateDirectory = GetStateDirectory();
        var package = Path.Combine(stateDirectory, $"release-{token}.exe");
        var handoff = Path.Combine(stateDirectory, $"update-handoff-{token}.ready");
        var appReady = Path.Combine(stateDirectory, $"update-app-{token}.ready");
        Process updater = null;
        UpdateDownloadWindow progress = null;
        Directory.CreateDirectory(stateDirectory);
        try
        {
            progress = new UpdateDownloadWindow();
            progress.Show();
            await DownloadReleasePackageAsync(manifest, package, progress);
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "CodexUsageTray");
            Directory.CreateDirectory(temporaryDirectory);
            var helper = Path.Combine(temporaryDirectory, $"updater-{token}.exe");
            File.Copy(Path.Combine(applicationDirectory, "CodexTracker.exe"), helper, true);
            var log = Path.Combine(stateDirectory, "update.log");
            var result = GetResultPath();
            TryDelete(handoff);
            TryDelete(appReady);

            var startInfo = new ProcessStartInfo
            {
                FileName = helper,
                WorkingDirectory = applicationDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            Add(startInfo, "--package-update");
            Add(startInfo, "--repo", applicationDirectory);
            Add(startInfo, "--state-dir", stateDirectory);
            Add(startInfo, "--parent-pid", Environment.ProcessId.ToString());
            Add(startInfo, "--keeper-pid", getKeeperProcessId().ToString());
            Add(startInfo, "--package", package);
            Add(startInfo, "--package-sha256", manifest.Sha256);
            Add(startInfo, "--expected-exe-sha256", ComputeFileSha256(Path.Combine(applicationDirectory, "CodexTracker.exe")));
            Add(startInfo, "--target-version", manifest.Version.ToString(3));
            Add(startInfo, "--handoff-ready", handoff);
            Add(startInfo, "--app-ready", appReady);
            Add(startInfo, "--log", log);
            Add(startInfo, "--result", result);
            Add(startInfo, "--token", token);
            updater = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the update helper.");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (updater.HasExited) throw new InvalidOperationException($"The update helper exited before handoff ({updater.ExitCode}).");
                try
                {
                    if (File.Exists(handoff) && File.ReadAllText(handoff).Trim() == token)
                    {
                        TryDelete(handoff);
                        progress.Close();
                        progress = null;
                        prepareForUpdate();
                        updater.Dispose();
                        updater = null;
                        return;
                    }
                }
                catch
                {
                }
                await Task.Delay(50, applicationToken);
            }
            throw new TimeoutException("The update helper did not become ready in time.");
        }
        catch
        {
            if (updater != null)
            {
                try
                {
                    if (!updater.HasExited) updater.Kill(true);
                    updater.WaitForExit(5000);
                }
                catch
                {
                }
                updater.Dispose();
            }
            CleanupFailedPackageHandoff(token, package, handoff, appReady);
            TryDelete(package);
            try { progress?.Close(); } catch { }
            throw;
        }
    }

    private async Task DownloadReleasePackageAsync(
        ReleaseManifest manifest,
        string packagePath,
        UpdateDownloadWindow progress
    )
    {
        using var client = CreateHttpClient();
        using var response = await client.GetAsync(manifest.PackageUri, HttpCompletionOption.ResponseHeadersRead, applicationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        progress.ReportDownload(0, totalBytes);
        await using (var input = await response.Content.ReadAsStreamAsync(applicationToken))
        await using (var output = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[80 * 1024];
            long downloadedBytes = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), applicationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), applicationToken);
                downloadedBytes += read;
                progress.ReportDownload(downloadedBytes, totalBytes);
            }
        }
        progress.ReportVerifying();
        var actualHash = await Task.Run(() => ComputeFileSha256(packagePath), applicationToken);
        if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(packagePath);
            throw new InvalidDataException("The downloaded release failed SHA-256 verification.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Codex-Tracker-Updater/1.0");
        return client;
    }

    private static Version GetCurrentVersion() =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    private static string SanitizeReleaseNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var value = new string(notes.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        value = string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length > 1200 ? value[..1197] + "..." : value;
    }

    private static bool IsSha256(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool IsReleaseVersion(string value, out Version version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('.');
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, out var number) || number < 0))
            return false;
        version = new Version(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        return version > new Version(0, 0, 0);
    }

    private static bool IsTrustedPackageUri(Uri packageUri, Version version)
    {
        var expectedPath = "/naksipayila/codex-tracker/releases/download/v" +
            version.ToString(3) + "/CodexTracker.exe";
        return packageUri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(packageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(packageUri.AbsolutePath.TrimEnd('/'), expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public UpdateResult? ReadResult()
    {
        var path = GetResultPath();
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) ||
                !root.TryGetProperty("message", out var message)) return null;
            var result = new UpdateResult(success.GetBoolean(), message.GetString() ?? "Update failed.");
            if (result.Success || !RepairNeeded) File.Delete(path);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private async Task ValidateTargetAsync(string commit)
    {
        await RunGitAsync(["cat-file", "-e", $"{commit}:CodexTracker.exe"]);
        foreach (var path in NativeBuildManifest.RequiredFiles)
        {
            await RunGitAsync(["cat-file", "-e", $"{commit}:{path}"]);
        }
    }

    private async Task<string> GetUpdateDetailsAsync(string local, string target)
    {
        var log = await RunGitAsync(["log", "--reverse", "--format=%s", $"{local}..{target}"]);
        var changes = log.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeSubject).Where(value => value.Length > 0).ToList();
        if (changes.Count > 12)
            changes = [$"... {changes.Count - 12} earlier commits not shown", .. changes.TakeLast(12)];
        return "What's new:" + Environment.NewLine +
            string.Join(Environment.NewLine, changes.Select(value => "- " + value)) +
            Environment.NewLine + Environment.NewLine +
            "The application will close, verify the native update, and restart.";
    }

    private async Task BeginUpdateAsync(string expected, string target)
    {
        var token = Guid.NewGuid().ToString("N");
        var stateDirectory = GetStateDirectory();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "CodexUsageTray");
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(temporaryDirectory);
        var helper = Path.Combine(temporaryDirectory, $"updater-{token}.exe");
        File.Copy(Path.Combine(applicationDirectory, "CodexTracker.exe"), helper, true);
        var handoff = Path.Combine(stateDirectory, $"update-handoff-{token}.ready");
        var appReady = Path.Combine(stateDirectory, $"update-app-{token}.ready");
        var log = Path.Combine(stateDirectory, "update.log");
        var result = GetResultPath();
        TryDelete(handoff);
        TryDelete(appReady);

        var startInfo = new ProcessStartInfo
        {
            FileName = helper,
            WorkingDirectory = applicationDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        Add(startInfo, "--update");
        Add(startInfo, "--repo", applicationDirectory);
        Add(startInfo, "--state-dir", stateDirectory);
        Add(startInfo, "--parent-pid", Environment.ProcessId.ToString());
        Add(startInfo, "--keeper-pid", getKeeperProcessId().ToString());
        Add(startInfo, "--expected", expected);
        Add(startInfo, "--target", target);
        Add(startInfo, "--handoff-ready", handoff);
        Add(startInfo, "--app-ready", appReady);
        Add(startInfo, "--log", log);
        Add(startInfo, "--result", result);
        Add(startInfo, "--token", token);
        using var updater = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the update helper.");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (updater.HasExited) throw new InvalidOperationException($"The update helper exited before handoff ({updater.ExitCode}).");
            try
            {
                if (File.Exists(handoff) && File.ReadAllText(handoff).Trim() == token)
                {
                    TryDelete(handoff);
                    prepareForUpdate();
                    return;
                }
            }
            catch
            {
            }
            await Task.Delay(50);
        }
        var updaterExited = false;
        try
        {
            updater.Kill(true);
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await updater.WaitForExitAsync(exitTimeout.Token);
            updaterExited = updater.HasExited;
        }
        catch
        {
        }
        if (!updaterExited)
        {
            try { updaterExited = updater.HasExited; } catch { }
        }
        if (updaterExited) RemoveOwnedMarkers(updater.Id, expected, target, token);
        else throw new InvalidOperationException("The update helper is still active; its recovery state was preserved.");
        throw new TimeoutException("The update helper did not become ready in time.");
    }

    private void RemoveOwnedMarkers(int updaterProcessId, string expected, string target, string token)
    {
        try
        {
            var lockPath = GetLockPath();
            var pendingPath = GetPendingPath();
            var lockContent = File.ReadAllText(lockPath);
            var pendingContent = File.ReadAllText(pendingPath);
            if (!lockContent.StartsWith(updaterProcessId + "|", StringComparison.Ordinal) ||
                !pendingContent.Contains($"|{expected}|{target}|{token}|", StringComparison.Ordinal)) return;
            if (File.ReadAllText(pendingPath) == pendingContent) File.Delete(pendingPath);
            if (File.ReadAllText(lockPath) == lockContent) File.Delete(lockPath);
        }
        catch
        {
        }
    }

    private async Task<CommandCapture> RunGitAsync(IReadOnlyList<string> arguments)
    {
        var result = await RunGitCaptureAsync(arguments);
        if (result.ExitCode != 0) throw new InvalidOperationException(GetError(result, "Git command failed."));
        return result;
    }

    private async Task<CommandCapture> RunGitCaptureAsync(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = applicationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(applicationDirectory);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start Git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            try
            {
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(exitTimeout.Token);
            }
            catch
            {
            }
            if (applicationToken.IsCancellationRequested) throw new OperationCanceledException(applicationToken);
            throw new TimeoutException("Git did not finish within 60 seconds.");
        }
        return new CommandCapture(process.ExitCode, await outputTask, await errorTask);
    }

    private string GetStateDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA");
        var baseDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray")
            : Path.GetFullPath(overrideDirectory);
        var identity = new string(applicationDirectory.Select(character =>
            character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character).ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];
        var directory = Path.Combine(baseDirectory, "updates", hash);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string GetLockPath() => Path.Combine(GetStateDirectory(), "update.lock");
    private string GetPendingPath() => Path.Combine(GetStateDirectory(), "update.pending");
    private string GetResultPath() => Path.Combine(GetStateDirectory(), "update-result.json");

    private static bool IsTrustedRemote(string remote)
    {
        var normalized = remote.Trim().TrimEnd('/').ToLowerInvariant();
        return normalized is "https://github.com/naksipayila/codex-tracker.git" or
            "https://github.com/naksipayila/codex-tracker" or
            "git@github.com:naksipayila/codex-tracker.git" or
            "ssh://git@github.com/naksipayila/codex-tracker.git";
    }

    private static string SanitizeSubject(string subject)
    {
        var value = new string(subject.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        value = string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length > 160 ? value[..157] + "..." : value;
    }

    private static string GetError(CommandCapture result, string fallback) =>
        string.IsNullOrWhiteSpace(result.Error + result.Output)
            ? fallback
            : (result.Error + Environment.NewLine + result.Output).Trim();

    private static void Add(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void CleanupFailedPackageHandoff(
        string token,
        string package,
        string handoff,
        string appReady
    )
    {
        TryDelete(handoff);
        TryDelete(appReady);
        TryDelete(package);
        var pending = GetPendingPath();
        try
        {
            var pendingContent = File.Exists(pending) ? File.ReadAllText(pending) : "";
            if (!pendingContent.Contains("|" + token + "|", StringComparison.Ordinal)) return;
            File.Delete(pending);
            var lockPath = GetLockPath();
            if (File.Exists(lockPath)) File.Delete(lockPath);
        }
        catch
        {
        }
    }

    private sealed class ReleaseManifestDocument
    {
        public string Version { get; set; }
        public string PackageUrl { get; set; }
        public string Sha256 { get; set; }
        public string Notes { get; set; }
    }

    private readonly record struct ReleaseManifest(Version Version, Uri PackageUri, string Sha256, string Notes);
}

internal sealed class UpdateDownloadWindow : Window
{
    private readonly Label statusLabel;
    private readonly Label detailsLabel;
    private readonly ProgressBar progressBar;
    private readonly Label percentageLabel;

    public UpdateDownloadWindow()
    {
        Width = 430;
        Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = Brushes.Transparent;
        AllowsTransparency = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        statusLabel = new Label
        {
            Content = "Downloading update...",
            Foreground = Theme.TextPrimaryBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeH2,
            FontWeight = Theme.FontWeightSemibold,
            Padding = new Thickness(0),
        };
        detailsLabel = new Label
        {
            Content = "Preparing download...",
            Foreground = Theme.TextSecondaryBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeSmall,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 12),
        };
        progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 10,
            IsIndeterminate = true,
            Foreground = Theme.AccentBrush,
            Background = Theme.ButtonNormalBrush,
        };
        percentageLabel = new Label
        {
            Content = "",
            Foreground = Theme.TextSecondaryBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeSmall,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var layout = new StackPanel();
        layout.Children.Add(statusLabel);
        layout.Children.Add(detailsLabel);
        layout.Children.Add(progressBar);
        layout.Children.Add(percentageLabel);
        Content = new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24),
            Effect = Theme.PanelShadow(),
            Child = layout,
        };
    }

    internal string StatusText => statusLabel.Content as string;
    internal string DetailsText => detailsLabel.Content as string;
    internal double ProgressValue => progressBar.Value;
    internal bool IsIndeterminate => progressBar.IsIndeterminate;

    public void ReportDownload(long downloadedBytes, long totalBytes)
    {
        if (totalBytes <= 0)
        {
            progressBar.IsIndeterminate = true;
            detailsLabel.Content = FormatBytes(downloadedBytes) + " downloaded";
            percentageLabel.Content = "";
            return;
        }

        var percent = Math.Clamp(downloadedBytes * 100d / totalBytes, 0, 100);
        progressBar.IsIndeterminate = false;
        progressBar.Value = percent;
        detailsLabel.Content = $"{FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes)}";
        percentageLabel.Content = $"{percent:0}%";
    }

    public void ReportVerifying()
    {
        statusLabel.Content = "Verifying update...";
        detailsLabel.Content = "Checking package integrity...";
        progressBar.IsIndeterminate = true;
        percentageLabel.Content = "";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / (1024d * 1024d):0.0} MB";
    }
}
