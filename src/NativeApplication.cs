using System;
using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Media;
using MediaBrushes = System.Windows.Media.Brushes;

namespace CodexUsageTray;

internal static class NativeApplication
{
    private const string MutexName = "Local\\CodexUsageTray.Native.SingleInstance";
    private const string ActivationEventName = "Local\\CodexUsageTray.Native.ShowWidget";

    public static void Run(string applicationDirectory, bool isolated = false)
    {
        // Avoid WPF render-thread failures on incompatible or remote display drivers.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var instanceSuffix = isolated ? "." + Environment.ProcessId : "";
        using var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName + instanceSuffix);
        using var mutex = new Mutex(true, MutexName + instanceSuffix, out var ownsMutex);
        if (!ownsMutex)
        {
            activationEvent.Set();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.DispatcherUnhandledException += (_, eventArgs) =>
        {
            System.Windows.MessageBox.Show(eventArgs.Exception.Message, "Codex Tracker",
                MessageBoxButton.OK, MessageBoxImage.Error);
            eventArgs.Handled = true;
        };

        NativeAppController controller = null;
        application.Startup += (_, _) =>
        {
            try
            {
                controller = new NativeAppController(application, applicationDirectory);
                controller.Initialize();
                _ = Task.Run(() =>
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        if (!activationEvent.WaitOne(500)) continue;
                        _ = application.Dispatcher.BeginInvoke(controller.ShowWidget);
                    }
                }, cancellation.Token);
            }
            catch (Exception error)
            {
                controller?.Dispose();
                System.Windows.MessageBox.Show(error.Message, "Codex Tracker",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                application.Shutdown(-1);
            }
        };
        application.Exit += (_, _) => cancellation.Cancel();
        application.Run();
        cancellation.Cancel();
        controller?.Dispose();
    }
}

internal sealed class NativeAppController : IDisposable
{
    private const string LatrixUsageUrl = "https://inference.llai.io/dashboard";
    private readonly System.Windows.Application application;
    private readonly string applicationDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly NativeSettings settings;
    private readonly UpdateService updates;
    private readonly LatrixApiClient latrix = new();
    private readonly SemaphoreSlim activeRefreshGate = new(1, 1);
    private WidgetWindow widget;
    private SettingsPanelWindow settingsPanel;
    private TelemetryWindow telemetryWindow;
    private System.Windows.Forms.NotifyIcon tray;
    private Process pinHelper;
    private PeriodicTimer refreshTimer;
    private PeriodicTimer activeRefreshTimer;
    private bool widgetHiddenByUser;
    private bool widgetHiddenForMetrics;
    private bool quitting;
    private bool trayTogglePending;
    private bool openingSettingsPanel;
    private bool settingsPanelClosing;
    private bool settingsPanelRequested;

    public NativeAppController(System.Windows.Application application, string applicationDirectory)
    {
        this.application = application;
        this.applicationDirectory = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        settings = NativeSettings.Load();
        OpenCodeConfig.RemoveLegacyStoredKey();
        updates = new UpdateService(
            this.applicationDirectory,
            () => pinHelper?.HasExited == false ? pinHelper.Id : 0,
            PrepareForUpdate,
            () => application.Dispatcher.BeginInvoke(UpdateTray),
            lifetime.Token);
    }

    public void Initialize()
    {
        widget = new WidgetWindow();
        widget.Dragged += left => PositionWidget(left, false);
        widget.DragCompleted += () =>
        {
            PositionWidget(widget.Left);
            SaveWidgetPosition();
        };
        widget.Closed += (_, _) => Quit();
        widget.SetMetricVisibility(settings.ShowFiveHour, settings.ShowWeekly);
        widget.SetUsageLabels("6H", "W");

        tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Codex Tracker",
            Visible = true,
        };
        tray.MouseDown += (_, eventArgs) =>
        {
            trayTogglePending = eventArgs.Button == System.Windows.Forms.MouseButtons.Left;
        };
        tray.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button != System.Windows.Forms.MouseButtons.Left) return;
            var shouldToggle = trayTogglePending;
            if (!shouldToggle)
            {
                trayTogglePending = false;
                return;
            }
            _ = application.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                try { ToggleSettingsPanel(); }
                finally { trayTogglePending = false; }
            }));
        };
        UpdateTray();
        StartupRegistration.SetEnabled(applicationDirectory, settings.LaunchAtStartup);

        if (settings.ShowFiveHour || settings.ShowWeekly)
        {
            widget.Show();
            PositionWidget();
            widget.Reveal(true);
            RestartPinning();
        }
        else widgetHiddenForMetrics = true;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SignalUpdateReady();
        if (Environment.GetEnvironmentVariable("CODEX_USAGE_TRAY_SMOKE_EXIT") == "1")
        {
            _ = application.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(500);
                Quit();
            });
            return;
        }
        ShowUpdateResult();

        _ = RefreshLatrixUsageAsync(lifetime.Token);
        refreshTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _ = RunRefreshTimerAsync(lifetime.Token);
        _ = RefreshActiveUsersAsync(lifetime.Token);
        activeRefreshTimer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        _ = RunActiveRefreshTimerAsync(lifetime.Token);
    }

    private static Icon CreateTrayIcon()
    {
        using var source = Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? (Icon)SystemIcons.Application.Clone();
        using var sourceBitmap = source.ToBitmap();
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawImage(sourceBitmap, new Rectangle(2, 2, 28, 28));
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void ShowWidget()
    {
        if (quitting) return;
        if (!settings.ShowFiveHour && !settings.ShowWeekly)
        {
            widgetHiddenForMetrics = true;
            UpdateTray();
            return;
        }
        widgetHiddenByUser = false;
        widgetHiddenForMetrics = false;
        widget.SetMetricVisibility(settings.ShowFiveHour, settings.ShowWeekly);
        if (!widget.IsVisible) widget.Show();
        PositionWidget();
        RestartPinning();
        UpdateTray();
    }

    private async Task RunRefreshTimerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await refreshTimer.WaitForNextTickAsync(cancellationToken))
                await RefreshLatrixUsageAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RunActiveRefreshTimerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await activeRefreshTimer.WaitForNextTickAsync(cancellationToken))
                await RefreshActiveUsersAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshLatrixUsageAsync(CancellationToken cancellationToken)
    {
        if (!await refreshGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var apiKey = OpenCodeConfig.LoadApiKey();
            if (apiKey == null)
            {
                _ = application.Dispatcher.BeginInvoke(() =>
                {
                    widget.UpdateUsage(UsageDisplay.Empty);
                    widget.ClearOnlineUsers();
                });
                return;
            }

            try
            {
                var display = await latrix.ReadUsageAsync(apiKey, TimeZoneInfo.Local, cancellationToken);
                _ = application.Dispatcher.BeginInvoke(() => widget.UpdateUsage(display));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _ = application.Dispatcher.BeginInvoke(() => widget.UpdateUsage(UsageDisplay.Empty));
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            _ = application.Dispatcher.BeginInvoke(() => widget.UpdateUsage(UsageDisplay.Empty));
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task RefreshActiveUsersAsync(CancellationToken cancellationToken)
    {
        if (!await activeRefreshGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var apiKey = OpenCodeConfig.LoadApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _ = application.Dispatcher.BeginInvoke(widget.ClearOnlineUsers);
                return;
            }

            try
            {
                var users = await latrix.ReadActiveAsync(apiKey, cancellationToken);
                _ = application.Dispatcher.BeginInvoke(() => widget.UpdateOnlineUsers(users));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _ = application.Dispatcher.BeginInvoke(widget.ClearOnlineUsers);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            _ = application.Dispatcher.BeginInvoke(widget.ClearOnlineUsers);
        }
        finally
        {
            activeRefreshGate.Release();
        }
    }

    private void UpdateTray()
    {
        if (tray == null || quitting) return;
        var menu = CreateTrayMenu();
        var previous = tray.ContextMenuStrip;
        tray.ContextMenuStrip = menu;
        DisposeContextMenuWhenClosed(previous);
    }

    private System.Windows.Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = false };
        ConfigureContextMenuWindow(menu);
        menu.Items.Add("Quit", null, (_, _) => Quit());
        return menu;
    }

    private void ShowTraySettingsPanel()
    {
        if (quitting || openingSettingsPanel || !settingsPanelRequested) return;
        if (settingsPanel != null)
        {
            if (!settingsPanelClosing) settingsPanel.Activate();
            return;
        }
        openingSettingsPanel = true;
        try
        {
            if (quitting || !settingsPanelRequested || settingsPanel != null) return;
            var panel = new SettingsPanelWindow(
                widget.IsVisible,
                StartupRegistration.IsEnabled(applicationDirectory),
                settings.HideInFullscreen,
                settings.ShowFiveHour,
                 settings.ShowWeekly,
                 typeof(NativeAppController).Assembly.GetName().Version?.ToString(3) ?? "unknown",
                 updates.RepairNeeded && !updates.IsChecking,
                 () =>
                {
                    if (widget.IsVisible) HideWidget();
                    else ShowWidget();
                    settingsPanelRequested = false;
                    CloseSettingsPanel();
                 },
                 OpenUsageDashboard,
                 OpenTelemetryWindow,
                 CheckForUpdates,
                 enabled =>
                 {
                     try
                     {
                         StartupRegistration.SetEnabled(applicationDirectory, enabled);
                         settings.LaunchAtStartup = enabled;
                         settings.SaveWidget();
                     }
                     catch (Exception error)
                     {
                         System.Windows.MessageBox.Show(error.Message, "Codex Tracker",
                             MessageBoxButton.OK, MessageBoxImage.Error);
                     }
                 },
                enabled =>
                {
                    settings.HideInFullscreen = enabled;
                    settings.SaveWidget();
                    RestartPinning();
                    UpdateTray();
                },
                enabled =>
                {
                    settings.ShowFiveHour = enabled;
                    settings.SaveWidget();
                    ApplyMetricVisibility();
                },
                enabled =>
                {
                    settings.ShowWeekly = enabled;
                    settings.SaveWidget();
                    ApplyMetricVisibility();
                },
                () => trayTogglePending
            );
            panel.Closing += (_, _) => settingsPanelClosing = true;
            panel.Closed += (_, _) =>
            {
                if (settingsPanel == panel) settingsPanel = null;
                settingsPanelClosing = false;
                settingsPanelRequested = false;
            };
            settingsPanel = panel;
            panel.Opacity = 0;
            panel.Show();
            PositionSettingsPanel(panel);
            panel.RevealFromRight();
            panel.Activate();
        }
        finally
        {
            openingSettingsPanel = false;
        }
    }

    private void ToggleSettingsPanel()
    {
        settingsPanelRequested = !settingsPanelRequested;
        if (settingsPanelRequested) ShowTraySettingsPanel();
        else CloseSettingsPanel();
    }

    private static void PositionSettingsPanel(Window panel)
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var workArea = screen.WorkingArea;
        var scale = NativeMethods.GetScaleForWindow(panel);
        var widthPixels = panel.Width * scale;
        var heightPixels = panel.Height * scale;
        const double margin = 12;
        var left = workArea.Right - widthPixels - margin;
        var top = workArea.Bottom - heightPixels - margin;
        panel.Left = left / scale;
        panel.Top = top / scale;
    }

    private void CloseSettingsPanel()
    {
        settingsPanelRequested = false;
        if (settingsPanel != null && !settingsPanelClosing) settingsPanel.Close();
    }

    private async void CheckForUpdates()
    {
        CloseSettingsPanel();
        if (!await updates.CheckAsync(false))
        {
            System.Windows.MessageBox.Show("An update check is already in progress.", "Codex Tracker",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static void ConfigureContextMenuWindow(System.Windows.Forms.ContextMenuStrip menu)
    {
        menu.HandleCreated += (_, _) =>
        {
            var style = NativeMethods.GetWindowLongPtr(menu.Handle, NativeMethods.GwlExStyle).ToInt64();
            style = (style | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExAppWindow;
            NativeMethods.SetWindowLongPtr(menu.Handle, NativeMethods.GwlExStyle, new IntPtr(style));
        };
    }

    private void DisposeContextMenuWhenClosed(System.Windows.Forms.ContextMenuStrip menu)
    {
        if (menu == null || menu.IsDisposed) return;
        if (!menu.Visible)
        {
            menu.Dispose();
            return;
        }
        menu.Closed += (_, _) => ScheduleContextMenuDisposal(menu);
    }

    private void ScheduleContextMenuDisposal(System.Windows.Forms.ContextMenuStrip menu)
    {
        if (application.Dispatcher.HasShutdownStarted) return;
        _ = application.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!menu.IsDisposed) menu.Dispose();
        }));
    }

    private void OpenUsageDashboard()
    {
        Process.Start(new ProcessStartInfo(LatrixUsageUrl) { UseShellExecute = true });
    }

    private void OpenTelemetryWindow()
    {
        CloseSettingsPanel();
        if (telemetryWindow != null)
        {
            telemetryWindow.BringToFront();
            return;
        }
        telemetryWindow = new TelemetryWindow(latrix, OpenCodeConfig.LoadApiKey());
        telemetryWindow.Closed += (_, _) => telemetryWindow = null;
        telemetryWindow.Show();
        telemetryWindow.BringToFront();
    }

    private void HideWidget()
    {
        widgetHiddenByUser = true;
        StopPinning();
        widget.Hide();
        UpdateTray();
    }

    private void ApplyMetricVisibility()
    {
        widget.SetMetricVisibility(settings.ShowFiveHour, settings.ShowWeekly);
        if (settings.ShowFiveHour || settings.ShowWeekly)
        {
            if (!widgetHiddenForMetrics) return;
            widgetHiddenForMetrics = false;
            if (!widgetHiddenByUser) ShowWidget();
            else UpdateTray();
            return;
        }

        widgetHiddenForMetrics = true;
        if (widget.IsVisible)
        {
            StopPinning();
            widget.Hide();
        }
        UpdateTray();
    }

    private void PositionWidget(double? preferredLeft = null, bool resolveTaskbarPlacement = true)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var scale = NativeMethods.GetScaleForWindow(widget);
        var bounds = screen.Bounds;
        var workArea = screen.WorkingArea;
        var heightPixels = (int)Math.Round(widget.Height * scale);
        var taskbarHorizontal = workArea.Height < bounds.Height;
        var taskbarVertical = workArea.Width < bounds.Width;
        var taskbarSize = taskbarHorizontal ? bounds.Height - workArea.Height : bounds.Width - workArea.Width;
        if (taskbarSize <= 0)
        {
            taskbarHorizontal = true;
            taskbarVertical = false;
            taskbarSize = Math.Max(40, (int)Math.Round(48 * scale));
        }
        if (taskbarHorizontal)
        {
            heightPixels = Math.Clamp(
                (int)Math.Round(taskbarSize * 0.7),
                (int)Math.Round(28 * scale),
                (int)Math.Round(34 * scale));
            widget.Height = heightPixels / scale;
        }

        var preferredWidthPixels = (int)Math.Round(WidgetWindow.PreferredWidth * scale);
        var maxX = bounds.Right - preferredWidthPixels;
        var requestedX = preferredLeft.HasValue
            ? (int)Math.Round(preferredLeft.Value * scale)
            : bounds.Left + (int)Math.Round((bounds.Width - preferredWidthPixels) * Math.Clamp(settings.XRatio, 0, 1));
        var x = Math.Clamp(requestedX, bounds.Left, Math.Max(bounds.Left, maxX));
        var y = bounds.Bottom - taskbarSize + (taskbarSize - heightPixels) / 2;
        if (taskbarHorizontal && workArea.Top > bounds.Top)
            y = bounds.Top + (taskbarSize - heightPixels) / 2;
        if (taskbarHorizontal && resolveTaskbarPlacement)
        {
            var placement = NativeMethods.FindTaskbarWidgetPlacement(
                requestedX,
                bounds.Left,
                bounds.Right,
                y,
                y + heightPixels,
                preferredWidthPixels
            );
            widget.SetAvailableWidth(placement.Width / scale);
            x = placement.Left;
        }
        else if (taskbarVertical && workArea.Left > bounds.Left)
        {
            if (resolveTaskbarPlacement) widget.SetAvailableWidth(WidgetWindow.PreferredWidth);
            x = workArea.Left;
            y = bounds.Top + (bounds.Height - heightPixels) * 3 / 10;
        }
        else if (taskbarVertical)
        {
            if (resolveTaskbarPlacement) widget.SetAvailableWidth(WidgetWindow.PreferredWidth);
            x = workArea.Right - preferredWidthPixels;
            y = bounds.Top + (bounds.Height - heightPixels) * 3 / 10;
        }
        else if (resolveTaskbarPlacement) widget.SetAvailableWidth(WidgetWindow.PreferredWidth);
        widget.Left = x / scale;
        widget.Top = y / scale;
    }

    private void SaveWidgetPosition()
    {
        if (widget == null) return;
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var scale = NativeMethods.GetScaleForWindow(widget);
        var preferredWidthPixels = WidgetWindow.PreferredWidth * scale;
        var usableWidth = Math.Max(1, screen.Bounds.Width - preferredWidthPixels);
        settings.XRatio = Math.Clamp((widget.Left * scale - screen.Bounds.Left) / usableWidth, 0, 1);
        settings.SaveWidget();
    }

    private void RestartPinning()
    {
        StopPinning();
        if (quitting || widgetHiddenByUser || !widget.IsVisible) return;
        var handle = new WindowInteropHelper(widget).Handle;
        if (handle == IntPtr.Zero) return;
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(applicationDirectory, "CodexTracker.exe"),
            WorkingDirectory = applicationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--pin-hwnd");
        startInfo.ArgumentList.Add(handle.ToInt64().ToString());
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        if (settings.HideInFullscreen) startInfo.ArgumentList.Add("--hide-in-fullscreen");
        try { pinHelper = Process.Start(startInfo); } catch { pinHelper = null; }
    }

    private void StopPinning()
    {
        var helper = pinHelper;
        pinHelper = null;
        if (helper != null)
        {
            try
            {
                if (!helper.HasExited) helper.Kill(true);
                helper.WaitForExit(2000);
            }
            catch
            {
            }
            helper.Dispose();
        }
    }

    private void OnDisplaySettingsChanged(object sender, EventArgs eventArgs)
    {
        application.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(150);
            PositionWidget();
            RestartPinning();
        });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode != PowerModes.Resume) return;
        application.Dispatcher.BeginInvoke(async () =>
        {
            for (var attempt = 0; attempt < 8 && !quitting; attempt++)
            {
                await Task.Delay(500);
                PositionWidget();
            }
            if (!widgetHiddenByUser)
            {
                RestartPinning();
            }
        });
    }

    private void ShowUpdateResult()
    {
        var result = updates.ReadResult();
        if (result == null) return;
        System.Windows.MessageBox.Show(result.Value.Message, "Codex Tracker", MessageBoxButton.OK,
            result.Value.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void SignalUpdateReady()
    {
        if (Environment.GetEnvironmentVariable("CODEX_UPDATE_LAUNCH") != "1") return;
        var path = Environment.GetEnvironmentVariable("CODEX_UPDATE_READY_FILE");
        var token = Environment.GetEnvironmentVariable("CODEX_UPDATE_TOKEN");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(token)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, token);
        }
        catch
        {
        }
    }

    private void PrepareForUpdate()
    {
        StopPinning();
        Quit();
    }

    private void Quit()
    {
        if (quitting) return;
        quitting = true;
        lifetime.Cancel();
        refreshTimer?.Dispose();
        activeRefreshTimer?.Dispose();
        telemetryWindow?.Close();
        StopPinning();
        var trayMenu = tray?.ContextMenuStrip;
        if (tray != null)
        {
            tray.ContextMenuStrip = null;
            DisposeContextMenuWhenClosed(trayMenu);
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }
        application.Shutdown();
    }

        public void Dispose()
    {
        if (!quitting)
        {
            quitting = true;
            lifetime.Cancel();
            StopPinning();
            var trayMenu = tray?.ContextMenuStrip;
            if (tray != null) tray.ContextMenuStrip = null;
            DisposeContextMenuWhenClosed(trayMenu);
            tray?.Dispose();
            tray = null;
        }
        lifetime.Dispose();
        activeRefreshGate.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        refreshTimer?.Dispose();
        activeRefreshTimer?.Dispose();
        telemetryWindow?.Close();
    }
}

internal sealed class SettingsPanelWindow : Window
{
    private const double OpenSlideDistance = 28;
    private bool closing;

    public SettingsPanelWindow(
        bool widgetVisible,
        bool launchAtStartup,
        bool hideInFullscreen,
        bool showFiveHour,
        bool showWeekly,
        string currentVersion,
        bool canRepair,
        Action toggleWidget,
        Action openDashboard,
        Action openTelemetry,
        Action checkUpdate,
        Action<bool> setLaunchAtStartup,
        Action<bool> setHideInFullscreen,
        Action<bool> setShowFiveHour,
        Action<bool> setShowWeekly,
        Func<bool> ignoreDeactivation
    )
    {
        Width = 392;
        SizeToContent = System.Windows.SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = MediaBrushes.Transparent;
        AllowsTransparency = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var body = new StackPanel { Width = 356 };

        var updateInfo = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        updateInfo.Children.Add(CreateTextAction("Check for updates", checkUpdate));
        var versionText = new TextBlock
        {
            Text = "Version: " + currentVersion,
            Foreground = Theme.TextMutedBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeSmall,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        updateInfo.Children.Add(versionText);
        body.Children.Add(CreateSectionLabel("Quick actions"));
        var quickActions = new StackPanel();
        quickActions.Children.Add(CreateButton(widgetVisible ? "Hide widget" : "Show widget", toggleWidget));
        quickActions.Children.Add(CreateButton("Open Latrix usage dashboard", openDashboard));
        quickActions.Children.Add(CreateButton("Open telemetry window", openTelemetry));
        if (canRepair) quickActions.Children.Add(CreateButton("Repair update", checkUpdate));
        body.Children.Add(CreateCard(quickActions));

        body.Children.Add(CreateSeparator());

        body.Children.Add(CreateSectionLabel("Preferences"));
        var prefs = new StackPanel();
        prefs.Children.Add(CreateToggle("Launch at Windows startup", launchAtStartup, setLaunchAtStartup));
        prefs.Children.Add(CreateToggle("Hide in fullscreen apps", hideInFullscreen, setHideInFullscreen));
        prefs.Children.Add(CreateToggle("Show 6H usage", showFiveHour, setShowFiveHour));
        prefs.Children.Add(CreateToggle("Show weekly usage", showWeekly, setShowWeekly));
        body.Children.Add(CreateCard(prefs));

        updateInfo.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(updateInfo);

        static Border CreateCard(StackPanel content)
        {
            content.Margin = new Thickness(0, 2, 0, 0);
            return new Border
            {
                Background = Theme.SurfaceBrush,
                BorderBrush = Theme.BorderBrush,
                BorderThickness = new Thickness(1),
                 CornerRadius = Theme.RadiusCard,
                Padding = new Thickness(14, 12, 14, 8),
                Child = content,
            };
        }

        Content = new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.RadiusXLarge,
            Padding = new Thickness(18),
            Effect = Theme.PanelShadow(),
            Child = body,
        };
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) =>
        {
            if (!closing && !ignoreDeactivation()) Close();
        };
    }

    public void RevealFromRight()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            return;
        }

        var targetLeft = Left;
        Opacity = 0;
        Left = targetLeft + OpenSlideDistance;
        BeginAnimation(LeftProperty, new DoubleAnimation
        {
            From = targetLeft + OpenSlideDistance,
            To = targetLeft,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static Button CreateButton(string text, Action action, bool accent = false)
    {
        var background = accent
            ? Theme.AccentBrush
            : Theme.ButtonNormalBrush;
        var hoverBackground = accent
            ? Theme.AccentHoverBrush
            : Theme.ButtonHoverBrush;
        var pressedBackground = accent
            ? Theme.AccentPressedBrush
            : Theme.ButtonPressedBrush;
        var border = accent
            ? Theme.AccentBrush
            : Theme.ButtonBorderBrush;
        var hoverBorder = accent
            ? Theme.AccentHoverBrush
            : Theme.ButtonBorderHoverBrush;
        var button = new Button
        {
            Content = text,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 9, 12, 9),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = Theme.TextPrimaryBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeBody,
            FontWeight = accent ? Theme.FontWeightSemibold : Theme.FontWeightNormal,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var template = new ControlTemplate(typeof(Button));
        var buttonBorder = new FrameworkElementFactory(typeof(Border));
        buttonBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        buttonBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        buttonBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        buttonBorder.SetValue(Border.CornerRadiusProperty, Theme.RadiusLarge);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        content.SetValue(ContentPresenter.ContentTemplateSelectorProperty,
            new TemplateBindingExtension(ContentControl.ContentTemplateSelectorProperty));
        content.SetValue(ContentPresenter.ContentStringFormatProperty,
            new TemplateBindingExtension(ContentControl.ContentStringFormatProperty));
        content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty,
            new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        buttonBorder.AppendChild(content);
        template.VisualTree = buttonBorder;

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hoverBackground));
        hoverTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, hoverBorder));
        style.Triggers.Add(hoverTrigger);
        var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, pressedBackground));
        style.Triggers.Add(pressedTrigger);
        var focusTrigger = new Trigger { Property = Button.IsKeyboardFocusedProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, hoverBorder));
        style.Triggers.Add(focusTrigger);
        button.Style = style;
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock CreateSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Foreground = Theme.TextMutedBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeCaption,
            FontWeight = Theme.FontWeightSemibold,
            Margin = new Thickness(2, 0, 0, 7),
        };
    }

    private static Button CreateTextAction(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Background = MediaBrushes.Transparent,
            BorderBrush = MediaBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = Theme.AccentBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeSmall,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
        };
        var template = new ControlTemplate(typeof(Button));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        template.VisualTree = content;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.AccentBrush));
        var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Theme.AccentHoverBrush));
        style.Triggers.Add(hoverTrigger);
        button.Style = style;
        button.Click += (_, _) => action();
        return button;
    }

    private static CheckBox CreateToggle(string text, bool value, Action<bool> changed)
    {
        var toggle = new CheckBox
        {
            Content = text,
            IsChecked = value,
            Foreground = Theme.TextPrimaryBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeBody,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 3, 0, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var template = new ControlTemplate(typeof(CheckBox));
        var row = new FrameworkElementFactory(typeof(Border));
        row.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        row.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        row.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        row.SetValue(Border.CornerRadiusProperty, Theme.RadiusLarge);
        row.SetValue(Border.PaddingProperty, new Thickness(10, 8, 10, 8));

        var content = new FrameworkElementFactory(typeof(StackPanel));
        content.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "Track";
        track.SetValue(FrameworkElement.WidthProperty, 32d);
        track.SetValue(FrameworkElement.HeightProperty, 18d);
        track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        track.SetValue(Border.BackgroundProperty, Theme.ButtonNormalBrush);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));

        var knob = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        knob.Name = "Knob";
        knob.SetValue(FrameworkElement.WidthProperty, 12d);
        knob.SetValue(FrameworkElement.HeightProperty, 12d);
        knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        knob.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        knob.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
        knob.SetValue(System.Windows.Shapes.Shape.FillProperty,
            Theme.TextMutedBrush);
        track.AppendChild(knob);
        content.AppendChild(track);

        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        label.SetValue(ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        label.SetValue(ContentPresenter.ContentStringFormatProperty,
            new TemplateBindingExtension(ContentControl.ContentStringFormatProperty));
        label.SetValue(ContentPresenter.MarginProperty, new Thickness(10, 0, 0, 0));
        label.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.AppendChild(label);
        row.AppendChild(content);
        template.VisualTree = row;

        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Theme.ElevatedBrush));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.BorderBrush));
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty,
            Theme.ButtonHoverBrush));
        hoverTrigger.Setters.Add(new Setter(Control.BorderBrushProperty,
            Theme.BorderStrongBrush));
        style.Triggers.Add(hoverTrigger);
        var checkedTrigger = new Trigger
        {
            Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            Value = true,
        };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            Theme.AccentBrush, "Track"));
        checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Shape.FillProperty,
            Theme.TextPrimaryBrush, "Knob"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 0), "Knob"));
        template.Triggers.Add(checkedTrigger);
        toggle.Style = style;
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }

    private static Border CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            Background = Theme.BorderBrush,
            Margin = new Thickness(0, 12, 0, 12),
        };
    }
}
