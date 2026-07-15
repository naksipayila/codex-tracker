using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex Usage Tray")]
[assembly: AssemblyDescription("Codex Usage Tray launcher")]
[assembly: AssemblyProduct("Codex Usage Tray")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--pin-hwnd" && args[2] == "--parent-pid")
        {
            PinWindow(args[1], args[3], Array.IndexOf(args, "--hide-in-fullscreen") >= 0);
            return;
        }

        if (args.Length > 0 && args[0] == "--update")
        {
            RunUpdater(args);
            return;
        }

        CleanupStaleUpdaterFiles();
        var applicationDirectory = NormalizeDirectory(AppDomain.CurrentDomain.BaseDirectory);
        if (IsUpdateInProgress(applicationDirectory)) return;

        var projectDirectory = Path.Combine(applicationDirectory, "src");
        var pendingPath = GetUpdatePendingPath();
        if (!File.Exists(pendingPath))
        {
            var legacyPendingPath = Path.Combine(applicationDirectory, LegacyUpdatePendingFileName);
            if (File.Exists(legacyPendingPath)) pendingPath = legacyPendingPath;
        }
        if (File.Exists(pendingPath))
        {
            StartPendingUpdate(applicationDirectory, pendingPath);
            return;
        }

        if (!EnsureDependencies(projectDirectory)) return;

        try
        {
            StartElectron(projectDirectory, null, null, false);
        }
        catch (Exception error)
        {
            ShowError("The widget could not be started.", error.Message);
        }
    }

    private static void PinWindow(string windowHandleText, string parentProcessIdText, bool hideInFullscreen)
    {
        long windowHandleValue;
        int parentProcessId;
        if (!long.TryParse(windowHandleText, out windowHandleValue) || !int.TryParse(parentProcessIdText, out parentProcessId)) return;

        pinnedWindow = new IntPtr(windowHandleValue);
        pinnedParentProcessId = parentProcessId;
        hideInFullscreenApps = hideInFullscreen;
        winEventCallback = HandleWinEvent;
        foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            winEventCallback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess
        );
        objectHook = SetWinEventHook(
            EventObjectCreate,
            EventObjectReorder,
            IntPtr.Zero,
            winEventCallback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess
        );

        var fallbackTimer = new System.Windows.Forms.Timer { Interval = 250 };
        fallbackTimer.Tick += delegate
        {
            if (!IsWindow(pinnedWindow) || !IsParentProcessRunning())
            {
                fallbackTimer.Stop();
                Application.ExitThread();
                return;
            }
            UpdatePinnedWindow();
        };

        try
        {
            UpdatePinnedWindow();
            fallbackTimer.Start();
            Application.Run();
        }
        finally
        {
            fallbackTimer.Dispose();
            if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
            if (objectHook != IntPtr.Zero) UnhookWinEvent(objectHook);
        }
    }

    private static bool IsParentProcessRunning()
    {
        try
        {
            using (var process = Process.GetProcessById(pinnedParentProcessId))
            {
                return !process.HasExited;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void HandleWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime
    )
    {
        try
        {
            if (eventType == EventSystemForeground ||
                eventType == EventObjectReorder ||
                (objectId == ObjectIdWindow && eventType == EventObjectShow) ||
                IsTaskbarWindow(windowHandle))
            {
                UpdatePinnedWindow();
            }
        }
        catch
        {
            // The fallback timer will retry if a transient shell event cannot be handled.
        }
    }

    private static bool IsTaskbarWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return false;
        var className = new StringBuilder(64);
        if (GetClassName(windowHandle, className, className.Capacity) == 0) return false;
        var windowClass = className.ToString();
        return windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd" ||
            windowClass == "Progman" || windowClass == "WorkerW";
    }

    private static void UpdatePinnedWindow()
    {
        if (hideInFullscreenApps && IsFullscreenForegroundWindow())
        {
            if (!widgetHiddenForFullscreen)
            {
                ShowWindow(pinnedWindow, SwHide);
                widgetHiddenForFullscreen = true;
            }
            return;
        }

        if (widgetHiddenForFullscreen)
        {
            ShowWindow(pinnedWindow, SwShowNoActivate);
            widgetHiddenForFullscreen = false;
        }
        RaisePinnedWindow();
    }

    private static bool IsFullscreenForegroundWindow()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || foregroundWindow == pinnedWindow ||
            !IsWindowVisible(foregroundWindow) || IsTaskbarWindow(foregroundWindow))
        {
            return false;
        }

        var monitor = MonitorFromWindow(foregroundWindow, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var monitorInfo = new MonitorInfo();
        monitorInfo.cbSize = Marshal.SizeOf(monitorInfo);
        WindowRect windowRect;
        return GetMonitorInfo(monitor, ref monitorInfo) && GetWindowRect(foregroundWindow, out windowRect) &&
            windowRect.Left <= monitorInfo.rcMonitor.Left &&
            windowRect.Top <= monitorInfo.rcMonitor.Top &&
            windowRect.Right >= monitorInfo.rcMonitor.Right &&
            windowRect.Bottom >= monitorInfo.rcMonitor.Bottom;
    }

    private static void RaisePinnedWindow()
    {
        if (raisingPinnedWindow || !IsWindow(pinnedWindow)) return;
        raisingPinnedWindow = true;
        try
        {
            // Keep the widget at the top of the topmost band without taking focus.
            SetWindowPos(
                pinnedWindow,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging
            );
        }
        finally
        {
            raisingPinnedWindow = false;
        }
    }

    private static bool IsUpdateInProgress(string applicationDirectory)
    {
        return IsUpdateLockActive(GetUpdateLockPath()) ||
            IsUpdateLockActive(Path.Combine(applicationDirectory, LegacyUpdateLockFileName));
    }

    private static bool IsUpdateLockActive(string lockPath)
    {
        string lockContent;
        try
        {
            lockContent = File.ReadAllText(lockPath);
        }
        catch
        {
            return false;
        }

        try
        {
            var parts = lockContent.Split('|');
            int processId;
            long startedAt;
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out processId) &&
                long.TryParse(parts[1], out startedAt))
            {
                using (var process = Process.GetProcessById(processId))
                {
                    var processStartedAt = (long)(process.StartTime.ToUniversalTime() -
                        new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                    if (!process.HasExited && Math.Abs(processStartedAt - startedAt) < 5000) return true;
                }
            }
        }
        catch
        {
            // The process ended or the lock is malformed, so remove this exact stale lock.
        }

        try
        {
            if (File.ReadAllText(lockPath) == lockContent) File.Delete(lockPath);
        }
        catch
        {
        }
        return false;
    }

    private static bool EnsureDependencies(string projectDirectory)
    {
        var electron = GetElectronPath(projectDirectory);
        if (File.Exists(electron)) return true;

        try
        {
            var result = RunShellCommand(projectDirectory, "npm install", DependencyTimeoutMs);
            if (result.ExitCode == 0 && File.Exists(electron)) return true;
            ShowError(
                "Widget dependencies could not be installed.",
                GetCommandError(result, "Make sure Node.js is installed, then try again.")
            );
        }
        catch (Exception error)
        {
            ShowError("Widget dependencies could not be installed.", error.Message);
        }
        return false;
    }

    private static Process StartElectron(
        string projectDirectory,
        string readyFile,
        string token,
        bool recovery
    )
    {
        var electron = GetElectronPath(projectDirectory);
        if (!File.Exists(electron)) throw new FileNotFoundException("Electron is not installed.", electron);

        var startInfo = new ProcessStartInfo
        {
            FileName = electron,
            Arguments = ".",
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.EnvironmentVariables.Remove("CODEX_UPDATE_LAUNCH");
        startInfo.EnvironmentVariables.Remove("CODEX_UPDATE_READY_FILE");
        startInfo.EnvironmentVariables.Remove("CODEX_UPDATE_TOKEN");
        startInfo.EnvironmentVariables.Remove("CODEX_UPDATE_RECOVERY");
        if (recovery) startInfo.EnvironmentVariables["CODEX_UPDATE_RECOVERY"] = "1";
        if (!string.IsNullOrEmpty(readyFile) && !string.IsNullOrEmpty(token))
        {
            startInfo.EnvironmentVariables["CODEX_UPDATE_LAUNCH"] = "1";
            startInfo.EnvironmentVariables["CODEX_UPDATE_READY_FILE"] = readyFile;
            startInfo.EnvironmentVariables["CODEX_UPDATE_TOKEN"] = token;
        }

        var process = Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Windows did not start Electron.");
        return process;
    }

    private static string GetElectronPath(string projectDirectory)
    {
        return Path.Combine(projectDirectory, "node_modules", "electron", "dist", "electron.exe");
    }

    private static void StartPendingUpdate(string applicationDirectory, string pendingPath)
    {
        var projectDirectory = Path.Combine(applicationDirectory, "src");
        Process updater = null;
        try
        {
            var pending = File.ReadAllText(pendingPath).Trim().Split('|');
            if (pending.Length != 2 || !IsCommitHash(pending[0]) || !IsCommitHash(pending[1]))
            {
                throw new InvalidDataException("The pending update marker is invalid.");
            }

            var paths = CreateUpdatePaths();
            var helper = CopyUpdaterToTemp(paths.Token);
            var startInfo = CreateUpdaterStartInfo(
                helper,
                applicationDirectory,
                Process.GetCurrentProcess().Id,
                0,
                pending[0],
                pending[1],
                paths
            );
            updater = Process.Start(startInfo);
            if (updater == null || !WaitForTokenFile(paths.HandoffReadyPath, paths.Token, updater, HandoffTimeoutMs))
            {
                throw new InvalidOperationException("The update helper did not become ready.");
            }
            TryDeleteFile(paths.HandoffReadyPath);
            updater.Dispose();
            updater = null;
        }
        catch (Exception error)
        {
            if (updater != null)
            {
                KillProcessTree(updater);
                updater.Dispose();
            }
            var updateStillRunning = IsUpdateInProgress(applicationDirectory);
            ShowError("The pending update could not be resumed.", error.Message);
            try
            {
                if (!updateStillRunning && EnsureDependencies(projectDirectory))
                {
                    StartElectron(projectDirectory, null, null, true);
                }
            }
            catch (Exception startError)
            {
                ShowError("The widget could not be restarted.", startError.Message);
            }
        }
    }

    private static void RunUpdater(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var progress = new UpdateProgressForm())
        {
            progress.Shown += delegate
            {
                var worker = new Thread(delegate()
                {
                    try
                    {
                        RunUpdaterCore(args, progress.ReportProgress, progress.ShowFailure);
                    }
                    finally
                    {
                        progress.CloseWhenFinished();
                    }
                });
                worker.IsBackground = true;
                worker.Start();
            };
            Application.Run(progress);
        }
    }

    private static void RunUpdaterCore(
        string[] args,
        Action<string, int> reportProgress,
        Action<string, string> showFailure
    )
    {
        UpdateOptions options = null;
        bool parentExited = false;
        bool ownsPendingMarker = false;
        string ownedLockContent = null;
        string ownedPendingContent = null;
        Process launchedApplication = null;
        try
        {
            reportProgress("Preparing update...", 5);
            options = ParseUpdateOptions(args);
            Directory.CreateDirectory(Path.GetDirectoryName(options.LogPath));
            Directory.CreateDirectory(Path.GetDirectoryName(options.ResultPath));
            TryDeleteFile(options.HandoffReadyPath);
            TryDeleteFile(options.AppReadyPath);
            TryDeleteFile(options.ResultPath);

            AppendLog(options.LogPath, "Updater started for " + options.ExpectedCommit + " -> " + options.TargetCommit + ".");
            ownedLockContent = AcquireUpdateLock(options.ApplicationDirectory);
            ownedPendingContent = options.ExpectedCommit + "|" + options.TargetCommit;
            File.WriteAllText(
                GetUpdatePendingPath(),
                ownedPendingContent,
                new UTF8Encoding(false)
            );
            ownsPendingMarker = true;
            File.WriteAllText(options.HandoffReadyPath, options.Token, new UTF8Encoding(false));
            AppendLog(options.LogPath, "Handoff ready; waiting for the old application to exit.");

            reportProgress("Closing current widget...", 15);
            WaitForProcessExit(options.ParentProcessId, ParentExitTimeoutMs, "Electron");
            parentExited = true;
            WaitForProcessExit(options.KeeperProcessId, ParentExitTimeoutMs, "widget pinning helper");
            TryDeleteFile(options.HandoffReadyPath);
            WaitForRepositoryProcessesToExit(options.ApplicationDirectory, RepositoryExitTimeoutMs);

            ApplyUpdate(options, reportProgress);
            TryDeleteFile(options.AppReadyPath);
            reportProgress("Restarting widget...", 90);
            launchedApplication = StartElectron(
                Path.Combine(options.ApplicationDirectory, "src"),
                options.AppReadyPath,
                options.Token,
                false
            );
            if (!WaitForTokenFile(options.AppReadyPath, options.Token, launchedApplication, AppReadyTimeoutMs))
            {
                throw new InvalidOperationException("The updated widget did not report that it was ready.");
            }
            launchedApplication.Dispose();
            launchedApplication = null;

            AppendLog(options.LogPath, "The updated widget reported ready.");
            reportProgress("Finishing update...", 96);
            if (!DeleteOwnedFileWithRetry(
                GetUpdatePendingPath(),
                ownedPendingContent
            ))
            {
                AppendLog(options.LogPath, "Warning: the pending update marker could not be removed.");
            }
            else
            {
                ownsPendingMarker = false;
            }
            DeleteOwnedFileWithRetry(
                Path.Combine(options.ApplicationDirectory, LegacyUpdatePendingFileName),
                ownedPendingContent
            );
            if (!DeleteOwnedFileWithRetry(
                GetUpdateLockPath(),
                ownedLockContent
            ))
            {
                AppendLog(options.LogPath, "Warning: the update lock could not be removed.");
            }
            else
            {
                ownedLockContent = null;
            }
            TryDeleteFile(options.AppReadyPath);
            TryDeleteFile(options.ResultPath);
            AppendLog(options.LogPath, "Update completed successfully.");
            reportProgress("Update complete.", 100);
            Thread.Sleep(600);
        }
        catch (Exception error)
        {
            reportProgress("Update failed. Restoring widget...", 90);
            if (options != null)
            {
                AppendLog(options.LogPath, "Update failed: " + error);
                WriteUpdateResult(options, error);
            }

            if (launchedApplication != null)
            {
                KillProcessTree(launchedApplication);
                launchedApplication.Dispose();
                launchedApplication = null;
                if (options != null)
                {
                    try
                    {
                        WaitForRepositoryProcessesToExit(options.ApplicationDirectory, RepositoryExitTimeoutMs);
                    }
                    catch (Exception waitError)
                    {
                        AppendLog(options.LogPath, "Failed to quiesce the unsuccessful launch: " + waitError.Message);
                    }
                }
            }

            bool recovered = false;
            if (options != null && parentExited)
            {
                try
                {
                    reportProgress("Restoring widget...", 94);
                    TryDeleteFile(options.AppReadyPath);
                    using (var application = StartElectron(
                        Path.Combine(options.ApplicationDirectory, "src"),
                        options.AppReadyPath,
                        options.Token,
                        true
                    ))
                    {
                        recovered = WaitForTokenFile(
                            options.AppReadyPath,
                            options.Token,
                            application,
                            RecoveryReadyTimeoutMs
                        );
                        if (!recovered) KillProcessTree(application);
                    }
                    if (recovered)
                    {
                        TryDeleteFile(options.AppReadyPath);
                        AppendLog(options.LogPath, "The widget was restarted in recovery mode.");
                        reportProgress("Widget restored after update failure.", 100);
                        Thread.Sleep(600);
                    }
                }
                catch (Exception recoveryError)
                {
                    AppendLog(options.LogPath, "Recovery restart failed: " + recoveryError);
                }
            }

            if (options != null)
            {
                if (ownedLockContent != null)
                {
                    DeleteOwnedFileWithRetry(
                        GetUpdateLockPath(),
                        ownedLockContent
                    );
                }
                if (!parentExited && ownsPendingMarker)
                {
                    DeleteOwnedFileWithRetry(
                        GetUpdatePendingPath(),
                        ownedPendingContent
                    );
                }
            }
            if (!recovered)
            {
                showFailure(
                    "The application could not be updated.",
                    error.Message + (options == null ? "" : "\n\nLog: " + options.LogPath)
                );
            }
        }
        finally
        {
            if (options != null) TryDeleteFile(options.HandoffReadyPath);
        }
    }

    private static void ApplyUpdate(UpdateOptions options, Action<string, int> reportProgress)
    {
        reportProgress("Checking repository...", 25);
        AppendLog(options.LogPath, "Waiting for repository processes to release their files.");
        WaitForRepositoryProcessesToExit(options.ApplicationDirectory, RepositoryExitTimeoutMs);

        var branch = RequireGitSuccess(options, "branch --show-current").Output.Trim();
        if (!string.Equals(branch, UpdateBranch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Automatic updates require the " + UpdateBranch + " branch.");
        }

        var status = RequireGitSuccess(options, "status --porcelain --untracked-files=all").Output.Trim();
        if (status.Length != 0)
        {
            throw new InvalidOperationException(
                "The working tree changed before the update started.\n\nChanged paths:\n" + status
            );
        }

        var head = RequireGitSuccess(options, "rev-parse HEAD").Output.Trim();
        if (string.Equals(head, options.ExpectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            reportProgress("Applying update files...", 45);
            RequireGitSuccess(options, "cat-file -e " + options.TargetCommit + "^{commit}");
            RequireGitSuccess(
                options,
                "merge-base --is-ancestor " + options.ExpectedCommit + " " + options.TargetCommit
            );
            RequireGitSuccess(options, "merge --ff-only " + options.TargetCommit);
        }
        else if (!string.Equals(head, options.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The current commit changed before the update started.");
        }

        var updatedHead = RequireGitSuccess(options, "rev-parse HEAD").Output.Trim();
        if (!string.Equals(updatedHead, options.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Git did not finish at the expected commit.");
        }

        var projectDirectory = Path.Combine(options.ApplicationDirectory, "src");
        CommandResult installResult = null;
        for (var attempt = 1; attempt <= 3; attempt += 1)
        {
            reportProgress(
                attempt == 1 ? "Installing dependencies..." : "Retrying dependency installation...",
                60 + (attempt - 1) * 5
            );
            installResult = RunShellCommand(projectDirectory, "npm install", DependencyTimeoutMs);
            AppendCommandResult(options.LogPath, "npm install (attempt " + attempt + ")", installResult);
            if (installResult.ExitCode == 0) break;
            Thread.Sleep(attempt * 1000);
        }
        if (installResult == null || installResult.ExitCode != 0)
        {
            throw new InvalidOperationException(GetCommandError(installResult, "Dependency installation failed."));
        }

        reportProgress("Verifying update...", 82);
        var checkResult = RunShellCommand(projectDirectory, "npm run check", CheckTimeoutMs);
        AppendCommandResult(options.LogPath, "npm run check", checkResult);
        if (checkResult.ExitCode != 0)
        {
            throw new InvalidOperationException(GetCommandError(checkResult, "The syntax check failed."));
        }
    }

    private static CommandResult RequireGitSuccess(UpdateOptions options, string arguments)
    {
        var result = RunCommand(
            "git.exe",
            "-C " + QuoteArgument(options.ApplicationDirectory) + " " + arguments,
            options.ApplicationDirectory,
            GitTimeoutMs
        );
        AppendCommandResult(options.LogPath, "git " + arguments, result);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(GetCommandError(result, "Git command failed."));
        }
        return result;
    }

    private static void WaitForProcessExit(int processId, int timeoutMs, string description)
    {
        if (processId <= 0) return;
        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                if (!process.WaitForExit(timeoutMs))
                {
                    throw new TimeoutException("Timed out waiting for the old " + description + " process to exit.");
                }
            }
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
    }

    private static void WaitForRepositoryProcessesToExit(string applicationDirectory, int timeoutMs)
    {
        var root = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var clearChecks = 0;
        while (DateTime.UtcNow < deadline)
        {
            var repositoryProcessFound = false;
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == Process.GetCurrentProcess().Id) continue;
                    var executable = process.MainModule == null ? null : process.MainModule.FileName;
                    if (!string.IsNullOrEmpty(executable) &&
                        Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        repositoryProcessFound = true;
                    }
                }
                catch
                {
                    // Protected system processes are unrelated to this repository.
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (!repositoryProcessFound)
            {
                clearChecks += 1;
                if (clearChecks >= 4) return;
            }
            else
            {
                clearChecks = 0;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException("Repository processes did not release their files in time.");
    }

    private static string AcquireUpdateLock(string applicationDirectory)
    {
        if (IsUpdateInProgress(applicationDirectory))
        {
            throw new InvalidOperationException("Another update is already running.");
        }

        var lockPath = GetUpdateLockPath();
        var process = Process.GetCurrentProcess();
        var startedAt = ToUnixMilliseconds(process.StartTime.ToUniversalTime());
        var lockContent = process.Id + "|" + startedAt;
        using (var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(lockContent);
        }
        return lockContent;
    }

    private static UpdateOptions ParseUpdateOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[index] + ".");
            values[args[index]] = args[index + 1];
        }

        var options = new UpdateOptions
        {
            ApplicationDirectory = NormalizeDirectory(RequireOption(values, "--repo")),
            ParentProcessId = ParseProcessId(RequireOption(values, "--parent-pid")),
            KeeperProcessId = ParseProcessId(RequireOption(values, "--keeper-pid")),
            ExpectedCommit = RequireOption(values, "--expected"),
            TargetCommit = RequireOption(values, "--target"),
            HandoffReadyPath = Path.GetFullPath(RequireOption(values, "--handoff-ready")),
            AppReadyPath = Path.GetFullPath(RequireOption(values, "--app-ready")),
            LogPath = Path.GetFullPath(RequireOption(values, "--log")),
            ResultPath = Path.GetFullPath(RequireOption(values, "--result")),
            Token = RequireOption(values, "--token"),
        };
        if (!Directory.Exists(Path.Combine(options.ApplicationDirectory, ".git")))
        {
            throw new DirectoryNotFoundException("The repository Git directory was not found.");
        }
        if (!IsCommitHash(options.ExpectedCommit) || !IsCommitHash(options.TargetCommit))
        {
            throw new ArgumentException("The updater received an invalid commit hash.");
        }
        if (options.Token.Length < 16) throw new ArgumentException("The updater token is invalid.");
        return options;
    }

    private static ProcessStartInfo CreateUpdaterStartInfo(
        string helper,
        string applicationDirectory,
        int parentProcessId,
        int keeperProcessId,
        string expectedCommit,
        string targetCommit,
        UpdatePaths paths
    )
    {
        var arguments = new StringBuilder();
        AddArgument(arguments, "--update");
        AddOption(arguments, "--repo", applicationDirectory);
        AddOption(arguments, "--parent-pid", parentProcessId.ToString());
        AddOption(arguments, "--keeper-pid", keeperProcessId.ToString());
        AddOption(arguments, "--expected", expectedCommit);
        AddOption(arguments, "--target", targetCommit);
        AddOption(arguments, "--handoff-ready", paths.HandoffReadyPath);
        AddOption(arguments, "--app-ready", paths.AppReadyPath);
        AddOption(arguments, "--log", paths.LogPath);
        AddOption(arguments, "--result", paths.ResultPath);
        AddOption(arguments, "--token", paths.Token);
        return new ProcessStartInfo
        {
            FileName = helper,
            Arguments = arguments.ToString(),
            WorkingDirectory = applicationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
    }

    private static UpdatePaths CreateUpdatePaths()
    {
        var token = Guid.NewGuid().ToString("N");
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "codex-usage-tray"
        );
        Directory.CreateDirectory(userData);
        return new UpdatePaths
        {
            Token = token,
            HandoffReadyPath = Path.Combine(userData, "update-handoff-" + token + ".ready"),
            AppReadyPath = Path.Combine(userData, "update-app-" + token + ".ready"),
            LogPath = Path.Combine(userData, "update.log"),
            ResultPath = Path.Combine(userData, "update-result.json"),
        };
    }

    private static string CopyUpdaterToTemp(string token)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexUsageTray");
        Directory.CreateDirectory(directory);
        var helper = Path.Combine(directory, "updater-" + token + ".exe");
        File.Copy(Application.ExecutablePath, helper, true);
        return helper;
    }

    private static bool WaitForTokenFile(string filePath, string token, Process process, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(filePath) && File.ReadAllText(filePath).Trim() == token) return true;
            }
            catch
            {
                // The writer may still be replacing the ready file.
            }
            if (process != null)
            {
                try
                {
                    if (process.HasExited) return false;
                }
                catch
                {
                    return false;
                }
            }
            Thread.Sleep(50);
        }
        return false;
    }

    private static CommandResult RunShellCommand(string workingDirectory, string command, int timeoutMs)
    {
        return RunCommand(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            "/d /s /c \"" + command + "\"",
            workingDirectory,
            timeoutMs
        );
    }

    private static CommandResult RunCommand(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMs
    )
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var process = new Process { StartInfo = startInfo })
        {
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
            {
                if (eventArgs.Data != null) output.AppendLine(eventArgs.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
            {
                if (eventArgs.Data != null) error.AppendLine(eventArgs.Data);
            };
            if (!process.Start()) throw new InvalidOperationException("Windows did not start " + fileName + ".");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMs))
            {
                KillProcessTree(process);
                throw new TimeoutException(fileName + " did not finish in time.");
            }
            process.WaitForExit();
            return new CommandResult
            {
                ExitCode = process.ExitCode,
                Output = output.ToString(),
                Error = error.ToString(),
            };
        }
    }

    private static void AppendCommandResult(string logPath, string command, CommandResult result)
    {
        AppendLog(
            logPath,
            command + " exited with " + result.ExitCode + ".\n" + result.Output + result.Error
        );
    }

    private static string GetCommandError(CommandResult result, string fallback)
    {
        if (result == null) return fallback;
        var message = (result.Error + result.Output).Trim();
        return message.Length == 0 ? fallback : message;
    }

    private static void AppendLog(string logPath, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            File.AppendAllText(
                logPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine,
                new UTF8Encoding(false)
            );
        }
        catch
        {
            // Updating must not fail only because diagnostics cannot be written.
        }
    }

    private static void WriteUpdateResult(UpdateOptions options, Exception error)
    {
        try
        {
            var message = error.Message + "\n\nLog: " + options.LogPath;
            var json = "{\"success\":false,\"notified\":false,\"message\":\"" +
                EscapeJson(message) + "\"}";
            File.WriteAllText(options.ResultPath, json, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static string EscapeJson(string value)
    {
        var escaped = new StringBuilder();
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': escaped.Append("\\\\"); break;
                case '"': escaped.Append("\\\""); break;
                case '\r': escaped.Append("\\r"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\t': escaped.Append("\\t"); break;
                default:
                    if (character < 32) escaped.Append("\\u" + ((int)character).ToString("x4"));
                    else escaped.Append(character);
                    break;
            }
        }
        return escaped.ToString();
    }

    private static bool DeleteOwnedFileWithRetry(string filePath, string expectedContent)
    {
        if (string.IsNullOrEmpty(expectedContent)) return false;
        for (var attempt = 0; attempt < 20; attempt += 1)
        {
            try
            {
                if (!File.Exists(filePath)) return true;
                if (!string.Equals(File.ReadAllText(filePath), expectedContent, StringComparison.Ordinal)) return false;
                File.Delete(filePath);
                if (!File.Exists(filePath)) return true;
            }
            catch
            {
            }
            Thread.Sleep(100);
        }
        return !File.Exists(filePath);
    }

    private static void TryDeleteFile(string filePath)
    {
        try { File.Delete(filePath); } catch { }
    }

    private static string RequireOption(Dictionary<string, string> options, string name)
    {
        string value;
        if (!options.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Missing updater option " + name + ".");
        }
        return value;
    }

    private static int ParseProcessId(string value)
    {
        int processId;
        if (!int.TryParse(value, out processId) || processId < 0)
        {
            throw new ArgumentException("The updater received an invalid process ID.");
        }
        return processId;
    }

    private static bool IsCommitHash(string value)
    {
        if (value == null || value.Length != 40) return false;
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                (character >= 'a' && character <= 'f') ||
                (character >= 'A' && character <= 'F'))) return false;
        }
        return true;
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length == 0) return "\"\"";

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes += 1;
                continue;
            }
            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
                quoted.Append('"');
                backslashes = 0;
                continue;
            }
            quoted.Append('\\', backslashes);
            backslashes = 0;
            quoted.Append(character);
        }
        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    private static void AddArgument(StringBuilder arguments, string value)
    {
        if (arguments.Length > 0) arguments.Append(' ');
        arguments.Append(value);
    }

    private static void AddOption(StringBuilder arguments, string name, string value)
    {
        AddArgument(arguments, name);
        AddArgument(arguments, QuoteArgument(value));
    }

    private static long ToUnixMilliseconds(DateTime date)
    {
        return (long)(date.ToUniversalTime() - UnixEpoch).TotalMilliseconds;
    }

    private static string NormalizeDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string GetUserDataDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "codex-usage-tray"
        );
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetUpdateLockPath()
    {
        return Path.Combine(GetUserDataDirectory(), UpdateLockFileName);
    }

    private static string GetUpdatePendingPath()
    {
        return Path.Combine(GetUserDataDirectory(), UpdatePendingFileName);
    }

    private static void KillProcessTree(Process process)
    {
        if (process == null) return;
        try
        {
            if (process.HasExited) return;
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "taskkill.exe"
                ),
                Arguments = "/pid " + process.Id + " /t /f",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using (var killer = Process.Start(startInfo))
            {
                if (killer != null) killer.WaitForExit(10000);
            }
            if (!process.WaitForExit(10000))
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch
        {
            try { process.Kill(); } catch { }
        }
    }

    private static void CleanupStaleUpdaterFiles()
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "CodexUsageTray");
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.GetFiles(directory, "updater-*.exe"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) File.Delete(file);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static void ShowError(string message, string detail)
    {
        MessageBox.Show(
            message + (string.IsNullOrEmpty(detail) ? "" : "\n\n" + detail),
            "Codex Usage Tray",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );
    }

    private sealed class UpdateProgressForm : Form
    {
        private readonly Label statusLabel;
        private readonly Label percentageLabel;
        private readonly ProgressBar progressBar;
        private bool finished;

        public UpdateProgressForm()
        {
            Text = "Codex Usage Tray Update";
            Width = 390;
            Height = 155;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = true;
            TopMost = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 1,
                RowCount = 3,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            statusLabel = new Label
            {
                Text = "Preparing update...",
                AutoSize = true,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 0, 0, 10),
            };
            progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 5,
                Style = ProgressBarStyle.Continuous,
                Dock = DockStyle.Top,
                Height = 20,
            };
            percentageLabel = new Label
            {
                Text = "5%",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 8, 0, 0),
            };

            layout.Controls.Add(statusLabel, 0, 0);
            layout.Controls.Add(progressBar, 0, 1);
            layout.Controls.Add(percentageLabel, 0, 2);
            Controls.Add(layout);
        }

        public void ReportProgress(string status, int percent)
        {
            RunOnUiThread(delegate
            {
                var safePercent = Math.Max(0, Math.Min(100, percent));
                statusLabel.Text = status;
                progressBar.Value = safePercent;
                percentageLabel.Text = safePercent + "%";
            }, false);
        }

        public void ShowFailure(string message, string detail)
        {
            RunOnUiThread(delegate
            {
                MessageBox.Show(
                    this,
                    message + (string.IsNullOrEmpty(detail) ? "" : "\n\n" + detail),
                    "Codex Usage Tray",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }, true);
        }

        public void CloseWhenFinished()
        {
            RunOnUiThread(delegate
            {
                finished = true;
                TopMost = false;
                Close();
            }, false);
        }

        protected override void OnFormClosing(FormClosingEventArgs eventArgs)
        {
            if (!finished && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                return;
            }
            base.OnFormClosing(eventArgs);
        }

        private void RunOnUiThread(MethodInvoker action, bool wait)
        {
            try
            {
                if (IsDisposed || Disposing) return;
                if (!InvokeRequired)
                {
                    action();
                    return;
                }
                if (wait) Invoke(action);
                else BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The updater is already closing its progress window.
            }
        }
    }

    private sealed class UpdateOptions
    {
        public string ApplicationDirectory;
        public int ParentProcessId;
        public int KeeperProcessId;
        public string ExpectedCommit;
        public string TargetCommit;
        public string HandoffReadyPath;
        public string AppReadyPath;
        public string LogPath;
        public string ResultPath;
        public string Token;
    }

    private sealed class UpdatePaths
    {
        public string Token;
        public string HandoffReadyPath;
        public string AppReadyPath;
        public string LogPath;
        public string ResultPath;
    }

    private sealed class CommandResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }

    private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string UpdateBranch = "main";
    private const string UpdateLockFileName = "update.lock";
    private const string UpdatePendingFileName = "update.pending";
    private const string LegacyUpdateLockFileName = ".update.lock";
    private const string LegacyUpdatePendingFileName = ".update.pending";
    private const int HandoffTimeoutMs = 10000;
    private const int ParentExitTimeoutMs = 60000;
    private const int RepositoryExitTimeoutMs = 30000;
    private const int AppReadyTimeoutMs = 30000;
    private const int RecoveryReadyTimeoutMs = 15000;
    private const int GitTimeoutMs = 120000;
    private const int DependencyTimeoutMs = 300000;
    private const int CheckTimeoutMs = 60000;

    private static readonly IntPtr HwndTopmost = new IntPtr(-1);
    private static IntPtr pinnedWindow;
    private static int pinnedParentProcessId;
    private static bool raisingPinnedWindow;
    private static bool hideInFullscreenApps;
    private static bool widgetHiddenForFullscreen;
    private static WinEventDelegate winEventCallback;
    private static IntPtr foregroundHook;
    private static IntPtr objectHook;

    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectReorder = 0x8004;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime
    );

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public WindowRect rcMonitor;
        public WindowRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate eventHook,
        uint processId,
        uint threadId,
        uint flags
    );

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr eventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
}
