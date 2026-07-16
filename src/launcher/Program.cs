using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using CodexUsageTray;

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

        if (args.Length > 0 && (args[0] == "--update" || args[0] == "--package-update"))
        {
            RunUpdater(args);
            return;
        }

        if (args.Length == 3 && args[0] == "--native-smoke-test")
        {
            Environment.SetEnvironmentVariable("CODEX_UPDATE_LAUNCH", "1");
            Environment.SetEnvironmentVariable("CODEX_UPDATE_READY_FILE", Path.GetFullPath(args[1]));
            Environment.SetEnvironmentVariable("CODEX_UPDATE_TOKEN", args[2]);
            Environment.SetEnvironmentVariable("CODEX_USAGE_TRAY_SMOKE_EXIT", "1");
            CodexUsageTray.NativeApplication.Run(
                NormalizeDirectory(AppDomain.CurrentDomain.BaseDirectory),
                true
            );
            return;
        }

        if (args.Length > 0 && args[0] == "--self-test")
        {
            Environment.ExitCode = RunSelfTest(args);
            return;
        }

        if (args.Length > 0 && args[0] == "--run-contained")
        {
            Environment.ExitCode = RunContainedCommand(args);
            return;
        }

        var applicationDirectory = NormalizeDirectory(AppDomain.CurrentDomain.BaseDirectory);
        var launchedByUpdater = Environment.GetEnvironmentVariable("CODEX_UPDATE_LAUNCH") == "1";
        if (launchedByUpdater)
        {
            CodexUsageTray.NativeApplication.Run(applicationDirectory);
            return;
        }
        if (!launchedByUpdater && IsUpdateInProgress(applicationDirectory)) return;
        CleanupStaleUpdaterFiles(applicationDirectory);

        var pendingPath = GetUpdatePendingPath(applicationDirectory);
        if (!File.Exists(pendingPath))
        {
            var legacyPendingPath = Path.Combine(applicationDirectory, LegacyUpdatePendingFileName);
            if (File.Exists(legacyPendingPath)) pendingPath = legacyPendingPath;
            else
            {
                legacyPendingPath = GetLegacyUpdatePendingPath();
                if (File.Exists(legacyPendingPath)) pendingPath = legacyPendingPath;
            }
        }
        if (File.Exists(pendingPath))
        {
            if (HasRepositoryProcess(applicationDirectory))
            {
                CodexUsageTray.NativeApplication.Run(applicationDirectory);
                return;
            }
            StartPendingUpdate(applicationDirectory, pendingPath);
            return;
        }

        try
        {
            CodexUsageTray.NativeApplication.Run(applicationDirectory);
        }
        catch (Exception error)
        {
            ShowError("The widget could not be started.", error.Message);
        }
    }

    private static int RunSelfTest(string[] args)
    {
        try
        {
            if (args.Length != 4 || args[1] != UpdaterProtocolVersion || args[3].Length < 16) return 2;
            var applicationDirectory = NormalizeDirectory(AppDomain.CurrentDomain.BaseDirectory);
            if (!File.Exists(Path.Combine(applicationDirectory, "src", "CodexUsageTray.csproj")))
            {
                var workingDirectory = NormalizeDirectory(Environment.CurrentDirectory);
                if (File.Exists(Path.Combine(workingDirectory, "src", "CodexUsageTray.csproj")))
                    applicationDirectory = workingDirectory;
            }
            var sourceAvailable = File.Exists(Path.Combine(applicationDirectory, "src", "CodexUsageTray.csproj"));
            if (sourceAvailable)
            {
                foreach (var relativePath in NativeBuildManifest.RequiredFiles)
                {
                    if (!File.Exists(Path.Combine(applicationDirectory,
                        relativePath.Replace('/', Path.DirectorySeparatorChar)))) return 3;
                }
            }
            var buildHash = GetEmbeddedLauncherBuildHash();
            if (buildHash.Length != 64) return 5;
            WriteTextAtomically(Path.GetFullPath(args[2]), args[3] + "|" + buildHash);
            return 0;
        }
        catch
        {
            return 4;
        }
    }

    private static string GetEmbeddedLauncherBuildHash()
    {
        var attribute = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
            Assembly.GetExecutingAssembly(),
            typeof(AssemblyInformationalVersionAttribute)
        );
        const string prefix = "build-";
        return attribute != null && attribute.InformationalVersion.StartsWith(prefix, StringComparison.Ordinal)
            ? attribute.InformationalVersion.Substring(prefix.Length)
            : "";
    }

    private static int RunContainedCommand(string[] args)
    {
        string errorPath = null;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length) return ContainedCommandFailureExitCode;
                values[args[index]] = args[index + 1];
            }
            var gatePath = Path.GetFullPath(RequireOption(values, "--gate"));
            var token = RequireOption(values, "--token");
            var outputPath = Path.GetFullPath(RequireOption(values, "--stdout"));
            errorPath = Path.GetFullPath(RequireOption(values, "--stderr"));
            var fileName = DecodeBase64Option(values, "--file");
            var arguments = DecodeBase64Option(values, "--arguments");
            var workingDirectory = DecodeBase64Option(values, "--working-directory");
            if (token.Length < 16 || !WaitForGate(gatePath, token, CommandGateTimeoutMs))
            {
                throw new InvalidOperationException("The contained command gate was not opened.");
            }

            var output = new CommandOutputCapture();
            var error = new CommandOutputCapture();
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
            int exitCode;
            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += output.OnDataReceived;
                process.ErrorDataReceived += error.OnDataReceived;
                if (!process.Start()) throw new InvalidOperationException("Windows did not start " + fileName + ".");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                exitCode = process.ExitCode;
                if (!WaitForOutputDrain(output, error, CommandOutputDrainTimeoutMs))
                {
                    try { process.CancelOutputRead(); } catch { }
                    try { process.CancelErrorRead(); } catch { }
                }
            }
            WriteTextAtomically(outputPath, output.GetText());
            WriteTextAtomically(errorPath, error.GetText());
            return exitCode;
        }
        catch (Exception error)
        {
            if (!string.IsNullOrEmpty(errorPath))
            {
                try { WriteTextAtomically(errorPath, error.ToString()); } catch { }
            }
            return ContainedCommandFailureExitCode;
        }
    }

    private static string DecodeBase64Option(Dictionary<string, string> values, string name)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(RequireOption(values, name)));
    }

    private static bool WaitForGate(string gatePath, string token, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(gatePath) && File.ReadAllText(gatePath).Trim() == token) return true;
            }
            catch
            {
            }
            Thread.Sleep(20);
        }
        return false;
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
        if (hideInFullscreenApps && IsForegroundWindowCoveringWidget())
        {
            if (!widgetHiddenForFullscreen)
            {
                ShowWindow(pinnedWindow, SwHide);
                widgetHiddenForFullscreen = true;
            }
            return;
        }

        ShowWindow(pinnedWindow, SwShowNoActivate);
        widgetHiddenForFullscreen = false;
        RaisePinnedWindow();
    }

    private static bool IsForegroundWindowCoveringWidget()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || foregroundWindow == pinnedWindow ||
            !IsWindowVisible(foregroundWindow) || IsTaskbarWindow(foregroundWindow) ||
            IsShellOverlayWindow(foregroundWindow))
        {
            return false;
        }

        WindowRect foregroundRect;
        WindowRect widgetRect;
        return TryGetVisibleWindowRect(foregroundWindow, out foregroundRect) &&
            GetWindowRect(pinnedWindow, out widgetRect) &&
            foregroundRect.Left < widgetRect.Right &&
            foregroundRect.Right > widgetRect.Left &&
            foregroundRect.Top < widgetRect.Bottom &&
            foregroundRect.Bottom > widgetRect.Top;
    }

    private static bool IsShellOverlayWindow(IntPtr windowHandle)
    {
        uint processId;
        GetWindowThreadProcessId(windowHandle, out processId);
        if (processId == 0) return false;
        try
        {
            using (var process = Process.GetProcessById((int)processId))
            {
                return string.Equals(process.ProcessName, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(process.ProcessName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(process.ProcessName, "SearchHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(process.ProcessName, "SearchApp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(process.ProcessName, "SearchUI", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetVisibleWindowRect(IntPtr window, out WindowRect rect)
    {
        if (DwmGetWindowAttribute(
                window,
                DwmwaExtendedFrameBounds,
                out rect,
                Marshal.SizeOf(typeof(WindowRect))) == 0)
        {
            return true;
        }
        return GetWindowRect(window, out rect);
    }

    private static void RaisePinnedWindow()
    {
        if (raisingPinnedWindow || !IsWindow(pinnedWindow)) return;
        raisingPinnedWindow = true;
        try
        {
            // The shell taskbar itself is topmost, so the overlay must share that band to remain visible.
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
        return IsUpdateLockActive(GetUpdateLockPath(applicationDirectory)) ||
            IsUpdateLockActive(GetLegacyUpdateLockPath()) ||
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

    private static string GetLauncherBuildHash(string applicationDirectory)
    {
        return NativeBuildManifest.ComputeHash(applicationDirectory);
    }

    private static Process StartApplication(
        string applicationDirectory,
        string readyFile,
        string token,
        bool recovery
    )
    {
        var executable = Path.Combine(applicationDirectory, "CodexTracker.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The native application is missing.", executable);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = applicationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.EnvironmentVariables.Remove("CODEX_UPDATE_LAUNCH");
        startInfo.EnvironmentVariables.Remove("CODEX_UPDATE_READY_FILE");
        startInfo.EnvironmentVariables.Remove("CODEX_UPDATE_TOKEN");
        startInfo.EnvironmentVariables.Remove("CODEX_UPDATE_RECOVERY");
        if (recovery)
        {
            startInfo.EnvironmentVariables["CODEX_UPDATE_RECOVERY"] = "1";
            startInfo.EnvironmentVariables["CODEX_UPDATE_LAUNCH"] = "1";
        }
        if (!string.IsNullOrEmpty(readyFile) && !string.IsNullOrEmpty(token))
        {
            startInfo.EnvironmentVariables["CODEX_UPDATE_LAUNCH"] = "1";
            startInfo.EnvironmentVariables["CODEX_UPDATE_READY_FILE"] = readyFile;
            startInfo.EnvironmentVariables["CODEX_UPDATE_TOKEN"] = token;
        }

        var process = Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Windows did not start the native application.");
        return process;
    }

    private static void StartPendingUpdate(string applicationDirectory, string pendingPath)
    {
        Process updater = null;
        try
        {
            var pending = ReadPendingUpdateState(pendingPath, applicationDirectory);
            if (pending.Phase == "rolled-back" || pending.Phase == "complete")
            {
                var valid = pending.Mode == "package"
                    ? IsInstalledExecutableHash(applicationDirectory,
                        pending.Phase == "complete" ? pending.PackageSha256 : pending.ExpectedExecutableSha256)
                    : IsRepositoryAtCommit(
                        applicationDirectory,
                        pending.Phase == "complete" ? pending.TargetCommit : pending.ExpectedCommit
                    );
                if (!valid)
                {
                    throw new InvalidOperationException("The completed update marker does not match the installed application.");
                }
                TryDeleteFile(pendingPath);
                if (pending.Mode == "package")
                {
                    TryDeleteFile(pending.PackagePath);
                    TryDeleteFile(Path.Combine(applicationDirectory, ".CodexTracker.exe.update-backup"));
                }
                StartApplication(applicationDirectory, null, null, pending.Phase == "rolled-back");
                return;
            }

            var paths = CreateUpdatePaths(applicationDirectory);
            var helper = CopyUpdaterToTemp(paths.Token);
            var startInfo = pending.Mode == "package"
                ? CreatePackageUpdaterStartInfo(
                    helper,
                    applicationDirectory,
                    Process.GetCurrentProcess().Id,
                    0,
                    pending.ExpectedExecutableSha256,
                    pending.TargetVersion,
                    pending.PackagePath,
                    pending.PackageSha256,
                    pending.Phase,
                    paths
                )
                : CreateUpdaterStartInfo(
                    helper,
                    applicationDirectory,
                    Process.GetCurrentProcess().Id,
                    0,
                    pending.ExpectedCommit,
                    pending.TargetCommit,
                    pending.Phase,
                    paths
                );
            updater = Process.Start(startInfo);
            if (updater == null || !WaitForTokenFile(
                paths.HandoffReadyPath,
                paths.Token,
                updater,
                HandoffTimeoutMs,
                0
            ))
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
                if (!updateStillRunning) StartApplication(applicationDirectory, null, null, true);
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
                var worker = new Thread(delegate ()
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

            ownedLockContent = AcquireUpdateLock(options.ApplicationDirectory);
            TryDeleteFile(options.ResultPath);
            AppendLog(options.LogPath, "Updater started for " + options.ExpectedCommit + " -> " + options.TargetCommit + ".");
            ownedPendingContent = options.Mode == "package"
                ? WritePackagePendingUpdateState(options, options.ResumePhase == "rollback" ? "rollback" : "prepared")
                : WritePendingUpdateState(options, options.ResumePhase == "rollback" ? "rollback" : "prepared");
            ownsPendingMarker = true;
            File.WriteAllText(options.HandoffReadyPath, options.Token, new UTF8Encoding(false));
            AppendLog(options.LogPath, "Handoff ready; waiting for the old application to exit.");

            reportProgress("Closing current widget...", 15);
            WaitForProcessExit(options.ParentProcessId, ParentExitTimeoutMs, "application");
            parentExited = true;
            WaitForProcessExit(options.KeeperProcessId, ParentExitTimeoutMs, "widget pinning helper");
            TryDeleteFile(options.HandoffReadyPath);
            WaitForRepositoryProcessesToExit(options.ApplicationDirectory, RepositoryExitTimeoutMs);

            if (options.ResumePhase == "rollback")
            {
                var recovered = options.Mode == "package"
                    ? RollbackPackageAndRestart(options, reportProgress, out ownedPendingContent)
                    : RollbackAndRestart(options, reportProgress, out ownedPendingContent);
                if (!recovered)
                {
                    throw new InvalidOperationException("The interrupted rollback could not be resumed.");
                }
                DeleteOwnedFileWithRetry(GetUpdatePendingPath(options.ApplicationDirectory), ownedPendingContent);
                if (options.Mode != "package")
                {
                    DeleteOwnedFileWithRetry(
                        GetLegacyUpdatePendingPath(),
                        options.ExpectedCommit + "|" + options.TargetCommit
                    );
                }
                if (DeleteOwnedFileWithRetry(
                    GetUpdateLockPath(options.ApplicationDirectory),
                    ownedLockContent
                ))
                {
                    ownedLockContent = null;
                }
                CleanupPackageFiles(options);
                reportProgress("Previous version restored.", 100);
                Thread.Sleep(600);
                return;
            }

            ownedPendingContent = options.Mode == "package"
                ? ApplyPackageUpdate(options, reportProgress)
                : ApplyUpdate(options, reportProgress);
            TryDeleteFile(options.AppReadyPath);
            reportProgress("Restarting widget...", 90);
            launchedApplication = StartApplication(
                options.ApplicationDirectory,
                options.AppReadyPath,
                options.Token,
                false
            );
            if (!WaitForTokenFile(
                options.AppReadyPath,
                options.Token,
                launchedApplication,
                AppReadyTimeoutMs,
                AppStabilityWindowMs
            ))
            {
                throw new InvalidOperationException("The updated widget did not report that it was ready.");
            }
            launchedApplication.Dispose();
            launchedApplication = null;

            AppendLog(options.LogPath, "The updated widget reported ready.");
            reportProgress("Finishing update...", 96);
            ownedPendingContent = options.Mode == "package"
                ? WritePackagePendingUpdateState(options, "complete")
                : WritePendingUpdateState(options, "complete");
            if (!DeleteOwnedFileWithRetry(
                GetUpdatePendingPath(options.ApplicationDirectory),
                ownedPendingContent
            ))
            {
                AppendLog(options.LogPath, "Warning: the pending update marker could not be removed.");
            }
            else
            {
                ownsPendingMarker = false;
            }
            if (options.Mode != "package")
            {
                DeleteOwnedFileWithRetry(
                    GetLegacyUpdatePendingPath(),
                    options.ExpectedCommit + "|" + options.TargetCommit
                );
                DeleteOwnedFileWithRetry(
                    Path.Combine(options.ApplicationDirectory, LegacyUpdatePendingFileName),
                    options.ExpectedCommit + "|" + options.TargetCommit
                );
            }
            if (!DeleteOwnedFileWithRetry(
                GetUpdateLockPath(options.ApplicationDirectory),
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
            CleanupPackageFiles(options);
            AppendLog(options.LogPath, "Update completed successfully.");
            reportProgress("Update complete.", 100);
            Thread.Sleep(600);
        }
        catch (Exception error)
        {
            reportProgress("Update failed. Restoring widget...", 90);
            if (options != null && ownedLockContent != null)
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
                    recovered = options.Mode == "package"
                        ? RollbackPackageAndRestart(options, reportProgress, out ownedPendingContent)
                        : RollbackAndRestart(options, reportProgress, out ownedPendingContent);
                    if (recovered)
                    {
                        if (!DeleteOwnedFileWithRetry(
                            GetUpdatePendingPath(options.ApplicationDirectory),
                            ownedPendingContent
                        ))
                        {
                            AppendLog(options.LogPath, "Warning: the rolled-back pending marker could not be removed.");
                        }
                        if (options.Mode != "package")
                        {
                            DeleteOwnedFileWithRetry(
                                GetLegacyUpdatePendingPath(),
                                options.ExpectedCommit + "|" + options.TargetCommit
                            );
                            DeleteOwnedFileWithRetry(
                                Path.Combine(options.ApplicationDirectory, LegacyUpdatePendingFileName),
                                options.ExpectedCommit + "|" + options.TargetCommit
                            );
                        }
                        CleanupPackageFiles(options);
                        reportProgress("Previous version restored.", 100);
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
                var pendingRemoved = parentExited || !ownsPendingMarker;
                if (!parentExited && ownsPendingMarker)
                {
                    pendingRemoved = DeleteOwnedFileWithRetry(
                        GetUpdatePendingPath(options.ApplicationDirectory),
                        ownedPendingContent
                    );
                }
                if (ownedLockContent != null && pendingRemoved)
                {
                    DeleteOwnedFileWithRetry(
                        GetUpdateLockPath(options.ApplicationDirectory),
                        ownedLockContent
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
            if (options != null)
            {
                TryDeleteFile(options.HandoffReadyPath);
                TryDeleteFile(options.AppReadyPath);
            }
        }
    }

    private static bool RollbackAndRestart(
        UpdateOptions options,
        Action<string, int> reportProgress,
        out string pendingContent
    )
    {
        reportProgress("Restoring previous version...", 92);
        pendingContent = WritePendingUpdateState(options, "rollback");
        WaitForRepositoryProcessesToExit(options.ApplicationDirectory, RepositoryExitTimeoutMs);

        var branch = RequireGitSuccess(options, "branch --show-current").Output.Trim();
        if (!string.Equals(branch, UpdateBranch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The previous version cannot be restored from another branch.");
        }
        var status = RequireGitSuccess(options, "status --porcelain --untracked-files=all").Output.Trim();
        if (status.Length != 0)
        {
            throw new InvalidOperationException(
                "The previous version was not restored because repository changes were detected and preserved.\n\nChanged paths:\n" + status
            );
        }

        var head = RequireGitSuccess(options, "rev-parse HEAD").Output.Trim();
        if (string.Equals(head, options.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            RequireGitSuccess(options, "cat-file -e " + options.ExpectedCommit + "^{commit}");
            RequireGitSuccess(
                options,
                "merge-base --is-ancestor " + options.ExpectedCommit + " " + options.TargetCommit
            );
            RequireGitSuccess(options, "reset --keep " + options.ExpectedCommit);
        }
        else if (!string.Equals(head, options.ExpectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The repository moved outside the update transaction; it was not reset.");
        }

        var restoredHead = RequireGitSuccess(options, "rev-parse HEAD").Output.Trim();
        if (!string.Equals(restoredHead, options.ExpectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Git did not restore the previous commit.");
        }
        ValidateLauncher(options);

        var finalStatus = RequireGitSuccess(options, "status --porcelain --untracked-files=all").Output.Trim();
        if (finalStatus.Length != 0)
        {
            throw new InvalidOperationException("The restored repository is not clean.\n\nChanged paths:\n" + finalStatus);
        }

        pendingContent = WritePendingUpdateState(options, "rolled-back");
        reportProgress("Restarting previous version...", 97);
        TryDeleteFile(options.AppReadyPath);
        using (var application = StartApplication(
            options.ApplicationDirectory,
            options.AppReadyPath,
            options.Token,
            true
        ))
        {
            if (!WaitForTokenFile(
                options.AppReadyPath,
                options.Token,
                application,
                RecoveryReadyTimeoutMs,
                AppStabilityWindowMs
            ))
            {
                KillProcessTree(application);
                throw new InvalidOperationException("The restored widget did not remain ready.");
            }
        }
        TryDeleteFile(options.AppReadyPath);
        AppendLog(options.LogPath, "The previous version was restored and reported ready.");
        return true;
    }

    private static string ApplyUpdate(UpdateOptions options, Action<string, int> reportProgress)
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
            WritePendingUpdateState(options, "merged");
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
        ValidateLauncher(options);

        reportProgress("Verifying native update...", 82);
        var finalStatus = RequireGitSuccess(options, "status --porcelain --untracked-files=all").Output.Trim();
        if (finalStatus.Length != 0)
        {
            throw new InvalidOperationException(
                "The update changed tracked or untracked repository files.\n\nChanged paths:\n" + finalStatus
            );
        }
        ValidateLauncher(options);
        return WritePendingUpdateState(options, "verified");
    }

    private static string ApplyPackageUpdate(UpdateOptions options, Action<string, int> reportProgress)
    {
        reportProgress("Checking release package...", 25);
        WaitForRepositoryProcessesToExit(options.ApplicationDirectory, RepositoryExitTimeoutMs);
        var launcher = Path.Combine(options.ApplicationDirectory, "CodexTracker.exe");
        if (!File.Exists(launcher)) throw new FileNotFoundException("The installed launcher is missing.", launcher);
        if (!File.Exists(options.PackagePath)) throw new FileNotFoundException("The downloaded release package is missing.", options.PackagePath);
        if (!string.Equals(ComputeFileSha256(options.PackagePath), options.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded release failed SHA-256 verification.");
        }

        var currentHash = ComputeFileSha256(launcher);
        if (!string.Equals(currentHash, options.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(currentHash, options.ExpectedExecutableSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The installed executable changed before the update started.");
            reportProgress("Applying update files...", 45);
            var replacement = Path.Combine(options.ApplicationDirectory, ".CodexTracker.exe." + options.Token + ".new");
            var backup = GetPackageBackupPath(options);
            TryDeleteFile(replacement);
            TryDeleteFile(backup);
            File.Copy(options.PackagePath, replacement, true);
            File.Replace(replacement, launcher, backup, true);
            WritePackagePendingUpdateState(options, "replaced");
        }

        if (!string.Equals(ComputeFileSha256(launcher), options.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The installed executable does not match the release package.");
        }
        ValidatePackageLauncher(options, launcher);
        reportProgress("Verifying native update...", 82);
        return WritePackagePendingUpdateState(options, "verified");
    }

    private static bool RollbackPackageAndRestart(
        UpdateOptions options,
        Action<string, int> reportProgress,
        out string pendingContent
    )
    {
        reportProgress("Restoring previous version...", 92);
        pendingContent = WritePackagePendingUpdateState(options, "rollback");
        WaitForRepositoryProcessesToExit(options.ApplicationDirectory, RepositoryExitTimeoutMs);
        var launcher = Path.Combine(options.ApplicationDirectory, "CodexTracker.exe");
        var backup = GetPackageBackupPath(options);
        if (File.Exists(backup))
        {
            var restore = Path.Combine(options.ApplicationDirectory, ".CodexTracker.exe." + options.Token + ".restore");
            TryDeleteFile(restore);
            File.Copy(backup, restore, true);
            File.Replace(restore, launcher, null, true);
        }
        if (!File.Exists(launcher) ||
            !string.Equals(ComputeFileSha256(launcher), options.ExpectedExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The previous executable could not be restored.");
        }
        ValidatePackageLauncher(options, launcher);
        pendingContent = WritePackagePendingUpdateState(options, "rolled-back");
        reportProgress("Restarting previous version...", 97);
        TryDeleteFile(options.AppReadyPath);
        using (var application = StartApplication(
            options.ApplicationDirectory,
            options.AppReadyPath,
            options.Token,
            true
        ))
        {
            if (!WaitForTokenFile(
                options.AppReadyPath,
                options.Token,
                application,
                RecoveryReadyTimeoutMs,
                AppStabilityWindowMs
            ))
            {
                KillProcessTree(application);
                throw new InvalidOperationException("The restored widget did not remain ready.");
            }
        }
        TryDeleteFile(options.AppReadyPath);
        AppendLog(options.LogPath, "The previous version was restored and reported ready.");
        return true;
    }

    private static void ValidatePackageLauncher(UpdateOptions options, string launcher)
    {
        var token = Guid.NewGuid().ToString("N");
        var readyPath = Path.Combine(options.StateDirectory, "launcher-self-test-" + token + ".ready");
        TryDeleteFile(readyPath);
        try
        {
            var result = RunCommand(
                launcher,
                "--self-test " + UpdaterProtocolVersion + " " + QuoteArgument(readyPath) + " " + token,
                options.ApplicationDirectory,
                LauncherSelfTestTimeoutMs
            );
            AppendCommandResult(options.LogPath, launcher + " --self-test", result);
            var response = File.Exists(readyPath) ? File.ReadAllText(readyPath).Trim() : "";
            var separator = response.IndexOf('|');
            if (result.ExitCode != 0 || separator <= 0 ||
                !string.Equals(response.Substring(0, separator), token, StringComparison.Ordinal) ||
                response.Length - separator - 1 != 64)
            {
                throw new InvalidOperationException("The release launcher failed its compatibility self-test.");
            }
        }
        finally
        {
            TryDeleteFile(readyPath);
        }
    }

    private static bool IsRepositoryAtCommit(string applicationDirectory, string expectedCommit)
    {
        try
        {
            var result = RunCommand(
                "git.exe",
                "-C " + QuoteArgument(applicationDirectory) + " rev-parse HEAD",
                applicationDirectory,
                GitTimeoutMs
            );
            return result.ExitCode == 0 && string.Equals(
                result.Output.Trim(),
                expectedCommit,
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInstalledExecutableHash(string applicationDirectory, string expectedHash)
    {
        try
        {
            var launcher = Path.Combine(applicationDirectory, "CodexTracker.exe");
            return File.Exists(launcher) && string.Equals(
                ComputeFileSha256(launcher),
                expectedHash,
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch
        {
            return false;
        }
    }

    private static string GetPackageBackupPath(UpdateOptions options)
    {
        return Path.Combine(options.ApplicationDirectory, ".CodexTracker.exe.update-backup");
    }

    private static string ComputeFileSha256(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha256 = SHA256.Create())
        {
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    private static void CleanupPackageFiles(UpdateOptions options)
    {
        if (options.Mode != "package") return;
        TryDeleteFile(options.PackagePath);
        TryDeleteFile(GetPackageBackupPath(options));
        TryDeleteFile(Path.Combine(options.ApplicationDirectory, ".CodexTracker.exe." + options.Token + ".new"));
        TryDeleteFile(Path.Combine(options.ApplicationDirectory, ".CodexTracker.exe." + options.Token + ".restore"));
    }

    private static void ValidateLauncher(UpdateOptions options)
    {
        var launcher = Path.Combine(options.ApplicationDirectory, "CodexTracker.exe");
        if (!File.Exists(launcher)) throw new FileNotFoundException("The updated launcher is missing.", launcher);
        var token = Guid.NewGuid().ToString("N");
        var readyPath = Path.Combine(options.StateDirectory, "launcher-self-test-" + token + ".ready");
        TryDeleteFile(readyPath);
        try
        {
            var result = RunCommand(
                launcher,
                "--self-test " + UpdaterProtocolVersion + " " + QuoteArgument(readyPath) + " " + token,
                options.ApplicationDirectory,
                LauncherSelfTestTimeoutMs
            );
            AppendCommandResult(options.LogPath, "CodexTracker.exe --self-test", result);
            var response = File.Exists(readyPath) ? File.ReadAllText(readyPath).Trim() : "";
            var expectedResponse = token + "|" + GetLauncherBuildHash(options.ApplicationDirectory);
            if (result.ExitCode != 0 || !string.Equals(response, expectedResponse, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The updated launcher failed its compatibility self-test.");
            }
        }
        finally
        {
            TryDeleteFile(readyPath);
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

    private static bool HasRepositoryProcess(string applicationDirectory)
    {
        var root = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Process.GetCurrentProcess().Id) continue;
                var executable = process.MainModule == null ? null : process.MainModule.FileName;
                if (!string.IsNullOrEmpty(executable) &&
                    Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private static string AcquireUpdateLock(string applicationDirectory)
    {
        if (IsUpdateInProgress(applicationDirectory))
        {
            throw new InvalidOperationException("Another update is already running.");
        }

        var lockPath = GetUpdateLockPath(applicationDirectory);
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
        var packageMode = args.Length > 0 && args[0] == "--package-update";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[index] + ".");
            values[args[index]] = args[index + 1];
        }

        var options = new UpdateOptions
        {
            ApplicationDirectory = NormalizeDirectory(RequireOption(values, "--repo")),
            StateDirectory = Path.GetFullPath(RequireOption(values, "--state-dir")),
            ParentProcessId = ParseProcessId(RequireOption(values, "--parent-pid")),
            KeeperProcessId = ParseProcessId(RequireOption(values, "--keeper-pid")),
            HandoffReadyPath = Path.GetFullPath(RequireOption(values, "--handoff-ready")),
            AppReadyPath = Path.GetFullPath(RequireOption(values, "--app-ready")),
            LogPath = Path.GetFullPath(RequireOption(values, "--log")),
            ResultPath = Path.GetFullPath(RequireOption(values, "--result")),
            Token = RequireOption(values, "--token"),
            Mode = packageMode ? "package" : "git",
        };
        string resumePhase;
        options.ResumePhase = values.TryGetValue("--resume-phase", out resumePhase)
            ? resumePhase
            : "prepared";
        if (options.Token.Length < 16) throw new ArgumentException("The updater token is invalid.");
        if (options.Mode == "package")
        {
            options.PackagePath = Path.GetFullPath(RequireOption(values, "--package"));
            options.PackageSha256 = RequireOption(values, "--package-sha256").ToLowerInvariant();
            options.ExpectedExecutableSha256 = RequireOption(values, "--expected-exe-sha256").ToLowerInvariant();
            options.TargetVersion = RequireOption(values, "--target-version");
            Version packageVersion;
            if (!IsSha256(options.PackageSha256) || !IsSha256(options.ExpectedExecutableSha256) ||
                !Version.TryParse(options.TargetVersion, out packageVersion) || packageVersion <= new Version(0, 0))
            {
                throw new ArgumentException("The package updater received invalid release metadata.");
            }
            if (options.ResumePhase != "prepared" && options.ResumePhase != "replaced" &&
                options.ResumePhase != "verified" && options.ResumePhase != "rollback")
            {
                throw new ArgumentException("The package updater resume phase is invalid.");
            }
            var expectedPackageDirectory = NormalizeDirectory(GetUpdateStateDirectory(options.ApplicationDirectory));
            if (!NormalizeDirectory(Path.GetDirectoryName(options.PackagePath))
                .Equals(expectedPackageDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The package path does not belong to the repository state directory.");
            }
        }
        else
        {
            options.ExpectedCommit = RequireOption(values, "--expected");
            options.TargetCommit = RequireOption(values, "--target");
            if (!Directory.Exists(Path.Combine(options.ApplicationDirectory, ".git")))
            {
                throw new DirectoryNotFoundException("The repository Git directory was not found.");
            }
            if (!IsCommitHash(options.ExpectedCommit) || !IsCommitHash(options.TargetCommit))
            {
                throw new ArgumentException("The updater received an invalid commit hash.");
            }
            if (options.ResumePhase != "legacy" && options.ResumePhase != "prepared" &&
                options.ResumePhase != "merged" &&
                options.ResumePhase != "verified" && options.ResumePhase != "launching" &&
                options.ResumePhase != "rollback")
            {
                throw new ArgumentException("The updater resume phase is invalid.");
            }
        }
        var expectedStateDirectory = GetUpdateStateDirectory(options.ApplicationDirectory);
        if (!string.Equals(
            NormalizeDirectory(options.StateDirectory),
            NormalizeDirectory(expectedStateDirectory),
            StringComparison.OrdinalIgnoreCase
        ))
        {
            throw new ArgumentException("The updater state directory does not match the repository.");
        }
        return options;
    }

    private static ProcessStartInfo CreateUpdaterStartInfo(
        string helper,
        string applicationDirectory,
        int parentProcessId,
        int keeperProcessId,
        string expectedCommit,
        string targetCommit,
        string resumePhase,
        UpdatePaths paths
    )
    {
        var arguments = new StringBuilder();
        AddArgument(arguments, "--update");
        AddOption(arguments, "--repo", applicationDirectory);
        AddOption(arguments, "--state-dir", paths.StateDirectory);
        AddOption(arguments, "--parent-pid", parentProcessId.ToString());
        AddOption(arguments, "--keeper-pid", keeperProcessId.ToString());
        AddOption(arguments, "--expected", expectedCommit);
        AddOption(arguments, "--target", targetCommit);
        AddOption(arguments, "--resume-phase", resumePhase);
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

    private static ProcessStartInfo CreatePackageUpdaterStartInfo(
        string helper,
        string applicationDirectory,
        int parentProcessId,
        int keeperProcessId,
        string expectedExecutableSha256,
        string targetVersion,
        string packagePath,
        string packageSha256,
        string resumePhase,
        UpdatePaths paths
    )
    {
        var arguments = new StringBuilder();
        AddArgument(arguments, "--package-update");
        AddOption(arguments, "--repo", applicationDirectory);
        AddOption(arguments, "--state-dir", paths.StateDirectory);
        AddOption(arguments, "--parent-pid", parentProcessId.ToString());
        AddOption(arguments, "--keeper-pid", keeperProcessId.ToString());
        AddOption(arguments, "--package", packagePath);
        AddOption(arguments, "--package-sha256", packageSha256);
        AddOption(arguments, "--expected-exe-sha256", expectedExecutableSha256);
        AddOption(arguments, "--target-version", targetVersion);
        AddOption(arguments, "--resume-phase", resumePhase);
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

    private static UpdatePaths CreateUpdatePaths(string applicationDirectory)
    {
        var token = Guid.NewGuid().ToString("N");
        var stateDirectory = GetUpdateStateDirectory(applicationDirectory);
        return new UpdatePaths
        {
            StateDirectory = stateDirectory,
            Token = token,
            HandoffReadyPath = Path.Combine(stateDirectory, "update-handoff-" + token + ".ready"),
            AppReadyPath = Path.Combine(stateDirectory, "update-app-" + token + ".ready"),
            LogPath = Path.Combine(stateDirectory, "update.log"),
            ResultPath = Path.Combine(stateDirectory, "update-result.json"),
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

    private static bool WaitForTokenFile(
        string filePath,
        string token,
        Process process,
        int timeoutMs,
        int stabilityWindowMs
    )
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive(process)) return false;
            try
            {
                if (File.Exists(filePath) && File.ReadAllText(filePath).Trim() == token)
                {
                    return stabilityWindowMs <= 0 || WaitForProcessStability(process, stabilityWindowMs);
                }
            }
            catch
            {
                // The writer may still be replacing the ready file.
            }
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool WaitForProcessStability(Process process, int stabilityWindowMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(stabilityWindowMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive(process)) return false;
            Thread.Sleep(100);
        }
        return IsProcessAlive(process);
    }

    private static bool IsProcessAlive(Process process)
    {
        if (process == null) return true;
        try
        {
            process.Refresh();
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static CommandResult RunCommand(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMs
    )
    {
        var commandDirectory = Path.Combine(Path.GetTempPath(), "CodexUsageTray", "commands");
        Directory.CreateDirectory(commandDirectory);
        var token = Guid.NewGuid().ToString("N");
        var gatePath = Path.Combine(commandDirectory, token + ".gate");
        var outputPath = Path.Combine(commandDirectory, token + ".stdout");
        var errorPath = Path.Combine(commandDirectory, token + ".stderr");
        var wrapperArguments = new StringBuilder();
        AddArgument(wrapperArguments, "--run-contained");
        AddOption(wrapperArguments, "--gate", gatePath);
        AddOption(wrapperArguments, "--token", token);
        AddOption(wrapperArguments, "--stdout", outputPath);
        AddOption(wrapperArguments, "--stderr", errorPath);
        AddOption(wrapperArguments, "--file", Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName)));
        AddOption(wrapperArguments, "--arguments", Convert.ToBase64String(Encoding.UTF8.GetBytes(arguments)));
        AddOption(
            wrapperArguments,
            "--working-directory",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory))
        );
        var startInfo = new ProcessStartInfo
        {
            FileName = Application.ExecutablePath,
            Arguments = wrapperArguments.ToString(),
            WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using (var process = new Process { StartInfo = startInfo })
            using (var job = new KillOnCloseJob())
            {
                if (!process.Start()) throw new InvalidOperationException("Windows did not start the contained command wrapper.");
                try
                {
                    job.Assign(process);
                }
                catch
                {
                    job.Dispose();
                    KillProcessTree(process);
                    throw;
                }
                WriteTextAtomically(gatePath, token);
                var exited = process.WaitForExit(timeoutMs);
                var exitCode = exited ? process.ExitCode : -1;
                job.Dispose();
                if (!exited && !process.WaitForExit(CommandCleanupTimeoutMs))
                {
                    KillProcessTree(process);
                }
                if (!exited) throw new TimeoutException(fileName + " did not finish in time.");
                return new CommandResult
                {
                    ExitCode = exitCode,
                    Output = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "",
                    Error = File.Exists(errorPath)
                        ? File.ReadAllText(errorPath)
                        : exitCode == ContainedCommandFailureExitCode
                            ? "The contained command wrapper failed before producing diagnostics."
                            : "",
                };
            }
        }
        finally
        {
            TryDeleteFile(gatePath);
            TryDeleteFile(outputPath);
            TryDeleteFile(errorPath);
        }
    }

    private static bool WaitForOutputDrain(
        CommandOutputCapture output,
        CommandOutputCapture error,
        int timeoutMs
    )
    {
        var stopwatch = Stopwatch.StartNew();
        while ((!output.IsComplete || !error.IsComplete) && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(10);
        }
        return output.IsComplete && error.IsComplete;
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

    private static PendingUpdateState ReadPendingUpdateState(
        string pendingPath,
        string applicationDirectory
    )
    {
        var content = File.ReadAllText(pendingPath).Trim();
        var parts = content.Split('|');
        if (parts.Length == 2 && IsCommitHash(parts[0]) && IsCommitHash(parts[1]))
        {
            return new PendingUpdateState
            {
                ExpectedCommit = parts[0],
                TargetCommit = parts[1],
                Phase = "legacy",
            };
        }
        var versionTwo = parts.Length == 5 && parts[0] == "v2";
        var versionThree = parts.Length == 6 && parts[0] == PendingStateVersion;
        var versionFour = parts.Length == 9 && parts[0] == PackagePendingStateVersion;
        if (!versionTwo && !versionThree && !versionFour)
        {
            throw new InvalidDataException("The pending update marker is invalid.");
        }

        string recordedDirectory;
        try
        {
            recordedDirectory = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        }
        catch (Exception error)
        {
            throw new InvalidDataException("The pending update repository identity is invalid.", error);
        }
        if (!string.Equals(
            NormalizeDirectory(recordedDirectory),
            NormalizeDirectory(applicationDirectory),
            StringComparison.OrdinalIgnoreCase
        ))
        {
            throw new InvalidDataException("The pending update belongs to another repository.");
        }
        if (versionFour)
        {
            Version packageVersion;
            if (parts[2] != "package" || !IsSha256(parts[3]) || !IsSha256(parts[6]) ||
                !Version.TryParse(parts[4], out packageVersion) || packageVersion <= new Version(0, 0) ||
                string.IsNullOrWhiteSpace(parts[5]) || string.IsNullOrWhiteSpace(parts[8]))
            {
                throw new InvalidDataException("The package update marker is invalid.");
            }
            string packagePath;
            try
            {
                packagePath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[7]));
            }
            catch (Exception error)
            {
                throw new InvalidDataException("The package update path is invalid.", error);
            }
            var expectedPackageDirectory = NormalizeDirectory(GetUpdateStateDirectory(applicationDirectory));
            if (!string.Equals(
                NormalizeDirectory(Path.GetDirectoryName(packagePath)),
                expectedPackageDirectory,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new InvalidDataException("The package update belongs to another state directory.");
            }
            return new PendingUpdateState
            {
                Mode = "package",
                ExpectedExecutableSha256 = parts[3],
                TargetVersion = parts[4],
                PackageSha256 = parts[6],
                PackagePath = packagePath,
                Phase = parts[8],
            };
        }

        var token = versionThree ? parts[4] : "legacy-v2";
        var phase = versionThree ? parts[5] : parts[4];
        if (!IsCommitHash(parts[2]) || !IsCommitHash(parts[3]) ||
            string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(phase))
        {
            throw new InvalidDataException("The pending update marker is invalid.");
        }
        return new PendingUpdateState
        {
            Mode = "git",
            ExpectedCommit = parts[2],
            TargetCommit = parts[3],
            Phase = phase,
        };
    }

    private static string WritePendingUpdateState(UpdateOptions options, string phase)
    {
        var content = PendingStateVersion + "|" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(options.ApplicationDirectory)) + "|" +
            options.ExpectedCommit + "|" + options.TargetCommit + "|" + options.Token + "|" + phase;
        WriteTextAtomically(GetUpdatePendingPath(options.ApplicationDirectory), content);
        return content;
    }

    private static string WritePackagePendingUpdateState(UpdateOptions options, string phase)
    {
        var content = PackagePendingStateVersion + "|" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(options.ApplicationDirectory)) + "|package|" +
            options.ExpectedExecutableSha256 + "|" + options.TargetVersion + "|" + options.Token + "|" +
            options.PackageSha256 + "|" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(options.PackagePath)) + "|" + phase;
        WriteTextAtomically(GetUpdatePendingPath(options.ApplicationDirectory), content);
        return content;
    }

    private static void WriteTextAtomically(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        Directory.CreateDirectory(directory);
        var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            if (File.Exists(filePath)) File.Replace(temporaryPath, filePath, null);
            else File.Move(temporaryPath, filePath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
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

    private static bool IsSha256(string value)
    {
        if (value == null || value.Length != 64) return false;
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

    private static string GetUpdateStateBaseDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA");
        var directory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageTray"
            )
            : Path.GetFullPath(overrideDirectory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetRepositoryId(string applicationDirectory)
    {
        var normalized = NormalizeDirectory(applicationDirectory);
        var identity = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            identity.Append(character >= 'A' && character <= 'Z'
                ? (char)(character + ('a' - 'A'))
                : character);
        }
        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString()));
            var value = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            return value.Substring(0, 16);
        }
    }

    private static string GetUpdateStateDirectory(string applicationDirectory)
    {
        var directory = Path.Combine(
            GetUpdateStateBaseDirectory(),
            UpdateStateDirectoryName,
            GetRepositoryId(applicationDirectory)
        );
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetUpdateLockPath(string applicationDirectory)
    {
        return Path.Combine(GetUpdateStateDirectory(applicationDirectory), UpdateLockFileName);
    }

    private static string GetUpdatePendingPath(string applicationDirectory)
    {
        return Path.Combine(GetUpdateStateDirectory(applicationDirectory), UpdatePendingFileName);
    }

    private static string GetLegacyUserDataDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA");
        if (!string.IsNullOrWhiteSpace(overrideDirectory)) return Path.GetFullPath(overrideDirectory);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "codex-usage-tray"
        );
    }

    private static string GetLegacyUpdateLockPath()
    {
        return Path.Combine(GetLegacyUserDataDirectory(), UpdateLockFileName);
    }

    private static string GetLegacyUpdatePendingPath()
    {
        return Path.Combine(GetLegacyUserDataDirectory(), UpdatePendingFileName);
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

    private static void CleanupStaleUpdaterFiles(string applicationDirectory)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "CodexUsageTray");
            if (Directory.Exists(directory))
            {
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
            var stateDirectory = GetUpdateStateDirectory(applicationDirectory);
            var logPath = Path.Combine(stateDirectory, "update.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > MaximumUpdateLogBytes)
            {
                var previousLogPath = Path.Combine(stateDirectory, "update.previous.log");
                TryDeleteFile(previousLogPath);
                File.Move(logPath, previousLogPath);
            }
            foreach (var pattern in new[] { "update-handoff-*.ready", "update-app-*.ready", "*.tmp" })
            {
                foreach (var file in Directory.GetFiles(stateDirectory, pattern))
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
            var commandDirectory = Path.Combine(directory, "commands");
            if (Directory.Exists(commandDirectory))
            {
                foreach (var file in Directory.GetFiles(commandDirectory))
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
        }
        catch
        {
        }
    }

    private static void ShowError(string message, string detail)
    {
        MessageBox.Show(
            message + (string.IsNullOrEmpty(detail) ? "" : "\n\n" + detail),
            "Codex Tracker",
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
            Text = "Codex Tracker Update";
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
                    "Codex Tracker",
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

        private void RunOnUiThread(System.Windows.Forms.MethodInvoker action, bool wait)
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
        public string StateDirectory;
        public int ParentProcessId;
        public int KeeperProcessId;
        public string Mode;
        public string ExpectedCommit;
        public string TargetCommit;
        public string ExpectedExecutableSha256;
        public string TargetVersion;
        public string PackagePath;
        public string PackageSha256;
        public string HandoffReadyPath;
        public string AppReadyPath;
        public string LogPath;
        public string ResultPath;
        public string Token;
        public string ResumePhase;
    }

    private sealed class PendingUpdateState
    {
        public string Mode;
        public string ExpectedCommit;
        public string TargetCommit;
        public string ExpectedExecutableSha256;
        public string TargetVersion;
        public string PackageSha256;
        public string PackagePath;
        public string Phase;
    }

    private sealed class UpdatePaths
    {
        public string StateDirectory;
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

    private sealed class CommandOutputCapture
    {
        private readonly StringBuilder value = new StringBuilder();
        private int complete;

        public void OnDataReceived(object sender, DataReceivedEventArgs eventArgs)
        {
            if (eventArgs.Data == null)
            {
                Interlocked.Exchange(ref complete, 1);
                return;
            }
            lock (value)
            {
                value.AppendLine(eventArgs.Data);
            }
        }

        public bool IsComplete
        {
            get { return Interlocked.CompareExchange(ref complete, 0, 0) != 0; }
        }

        public string GetText()
        {
            lock (value)
            {
                return value.ToString();
            }
        }
    }

    private sealed class KillOnCloseJob : IDisposable
    {
        private readonly SafeJobHandle handle;

        public KillOnCloseJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            var error = Marshal.GetLastWin32Error();
            if (handle == null || handle.IsInvalid)
            {
                if (handle != null) handle.Dispose();
                throw new Win32Exception(error, "Could not create the updater process job.");
            }

            var information = new JobObjectExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation))
            ))
            {
                error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Could not configure the updater process job.");
            }
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not contain " + process.StartInfo.FileName + " in the updater process job."
                );
            }
        }

        public void Dispose()
        {
            handle.Dispose();
        }
    }

    private sealed class SafeJobHandle : SafeHandle
    {
        private SafeJobHandle() : base(IntPtr.Zero, true) { }

        public override bool IsInvalid
        {
            get { return handle == IntPtr.Zero || handle == new IntPtr(-1); }
        }

        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string UpdaterProtocolVersion = "3";
    private const string PendingStateVersion = "v3";
    private const string PackagePendingStateVersion = "v4";
    private const string UpdateBranch = "main";
    private const string UpdateStateDirectoryName = "updates";
    private const string UpdateLockFileName = "update.lock";
    private const string UpdatePendingFileName = "update.pending";
    private const string LegacyUpdateLockFileName = ".update.lock";
    private const string LegacyUpdatePendingFileName = ".update.pending";
    private const int HandoffTimeoutMs = 10000;
    private const int ParentExitTimeoutMs = 60000;
    private const int RepositoryExitTimeoutMs = 30000;
    private const int AppReadyTimeoutMs = 60000;
    private const int RecoveryReadyTimeoutMs = 60000;
    private const int AppStabilityWindowMs = 3000;
    private const int LauncherSelfTestTimeoutMs = 10000;
    private const int GitTimeoutMs = 120000;
    private const int CommandCleanupTimeoutMs = 5000;
    private const int CommandOutputDrainTimeoutMs = 5000;
    private const int CommandGateTimeoutMs = 10000;
    private const int ContainedCommandFailureExitCode = 253;
    private const long MaximumUpdateLogBytes = 4L * 1024L * 1024L;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

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
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hWnd,
        int attribute,
        out WindowRect value,
        int valueSize
    );

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
