using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

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

    public async Task CheckAsync(bool startup)
    {
        if (Interlocked.Exchange(ref checking, 1) != 0) return;
        stateChanged();
        try
        {
            applicationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(Path.Combine(applicationDirectory, ".git")))
                throw new InvalidOperationException("Automatic updates require a Git clone of the repository.");
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
                return;
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
            if (confirmation != MessageBoxResult.Yes) return;
            await BeginUpdateAsync(local, target);
        }
        catch (OperationCanceledException) when (applicationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!startup) System.Windows.MessageBox.Show(error.Message, "Automatic update is unavailable",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref checking, 0);
            stateChanged();
        }
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
        foreach (var path in new[]
        {
            "global.json",
            "Codex Tracker.exe",
            "src/CodexUsageTray.csproj",
            "src/app.manifest",
            "src/NativeApplication.cs",
            "src/WidgetWindow.cs",
            "src/NativeTypes.cs",
            "src/LatrixIntegration.cs",
            "src/UpdateService.cs",
            "src/NativeSettings.cs",
            "src/NativeMethods.cs",
            "src/launcher/Program.cs",
            "src/launcher/build.ps1",
            "src/launcher/icon.ico",
        })
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
        File.Copy(Path.Combine(applicationDirectory, "Codex Tracker.exe"), helper, true);
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
}
