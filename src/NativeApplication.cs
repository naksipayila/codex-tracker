using System;
using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace CodexUsageTray;

internal static class NativeApplication
{
    private const string MutexName = "Local\\CodexUsageTray.Native.SingleInstance";
    private const string ActivationEventName = "Local\\CodexUsageTray.Native.ShowWidget";

    public static void Run(string applicationDirectory, bool isolated = false)
    {
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
    private const string CodexUsageUrl = "https://chatgpt.com/codex/settings/usage";
    private const string LatrixUsageUrl = "https://inference.llai.io/dashboard";
    private readonly System.Windows.Application application;
    private readonly string applicationDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim clientRestartGate = new(1, 1);
    private readonly SemaphoreSlim sourceGate = new(1, 1);
    private readonly NativeSettings settings;
    private readonly UpdateService updates;
    private readonly LatrixApiClient latrix = new();
    private WidgetWindow widget;
    private SettingsPanelWindow settingsPanel;
    private System.Windows.Forms.NotifyIcon tray;
    private Process pinHelper;
    private Process loginProcess;
    private CodexAppServerClient codexClient;
    private PeriodicTimer refreshTimer;
    private bool widgetHiddenByUser;
    private bool widgetHiddenForMetrics;
    private bool quitting;
    private bool loggedIn;
    private bool trayTogglePending;
    private bool openingSettingsPanel;
    private bool settingsPanelClosing;
    private bool settingsPanelRequested;
    private bool latrixConfigured;
    private bool openingLatrixDialog;

    public NativeAppController(System.Windows.Application application, string applicationDirectory)
    {
        this.application = application;
        this.applicationDirectory = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        settings = NativeSettings.Load();
        latrixConfigured = LatrixApiKeyStore.IsConfigured();
        if (settings.UsageProvider == UsageProvider.Latrix && !latrixConfigured)
        {
            settings.UsageProvider = UsageProvider.Codex;
            settings.SaveWidget();
        }
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
        widget.Dragged += left => PositionWidget(left);
        widget.DragCompleted += SaveWidgetPosition;
        widget.Closed += (_, _) => Quit();
        widget.SetMetricVisibility(settings.ShowFiveHour, settings.ShowWeekly);
        widget.SetUsageLabels(GetPrimaryUsageLabel(), "W");

        tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? SystemIcons.Application,
            Text = "Codex Tracker",
            Visible = true,
        };
        tray.MouseDown += (_, eventArgs) =>
        {
            trayTogglePending = eventArgs.Button == System.Windows.Forms.MouseButtons.Left;
            if (trayTogglePending) ToggleSettingsPanel();
        };
        tray.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button != System.Windows.Forms.MouseButtons.Left) return;
            trayTogglePending = false;
        };
        UpdateTray();

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

        _ = StartUsageProviderAsync(lifetime.Token);
        refreshTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _ = RunRefreshTimerAsync(lifetime.Token);
        if (settings.UpdateAtStartup && Environment.GetEnvironmentVariable("CODEX_UPDATE_LAUNCH") != "1")
            _ = ScheduleStartupUpdateAsync(lifetime.Token);
    }

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

    private async Task StartCodexAsync(CancellationToken cancellationToken)
    {
        if (settings.UsageProvider != UsageProvider.Codex) return;
        try
        {
            if (!await CodexProcess.IsAvailableAsync(cancellationToken))
                await CodexProcess.InstallAsync(cancellationToken);
            if (settings.UsageProvider != UsageProvider.Codex) return;
            loggedIn = await CodexProcess.IsLoggedInAsync(cancellationToken);
            if (!loggedIn)
            {
                _ = application.Dispatcher.BeginInvoke(UpdateTray);
                return;
            }
            await StartCodexClientAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (settings.UsageProvider != UsageProvider.Codex) return;
            _ = application.Dispatcher.BeginInvoke(() =>
            {
                widget.UpdateUsage(UsageDisplay.Empty);
                System.Windows.MessageBox.Show(
                    error.Message + Environment.NewLine + Environment.NewLine +
                    "Install the Codex CLI with the official PowerShell installer:" + Environment.NewLine +
                    "irm https://chatgpt.com/codex/install.ps1 | iex",
                    "Codex Tracker", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
    }

    private async Task StartCodexClientAsync(CancellationToken cancellationToken)
    {
        if (settings.UsageProvider != UsageProvider.Codex) return;
        var previous = codexClient;
        codexClient = null;
        if (previous != null) await previous.DisposeAsync();
        var client = new CodexAppServerClient();
        client.RateLimitsUpdated += () => _ = RefreshLimitsAsync(lifetime.Token);
        client.Disconnected += () => _ = ReconnectCodexClientAsync(client);
        try
        {
            await client.StartAsync(cancellationToken);
            codexClient = client;
            await RefreshLimitsAsync(cancellationToken);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task ReconnectCodexClientAsync(CodexAppServerClient disconnectedClient)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        try
        {
            while (!lifetime.IsCancellationRequested && !quitting && settings.UsageProvider == UsageProvider.Codex)
            {
                await Task.Delay(retryDelay, lifetime.Token);
                await clientRestartGate.WaitAsync(lifetime.Token);
                try
                {
                    if (quitting || settings.UsageProvider != UsageProvider.Codex ||
                        (codexClient != null && !ReferenceEquals(codexClient, disconnectedClient))) return;
                    try
                    {
                        await StartCodexClientAsync(lifetime.Token);
                        return;
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        codexClient = null;
                    }
                }
                finally
                {
                    clientRestartGate.Release();
                }
                retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            _ = application.Dispatcher.BeginInvoke(() =>
            {
                if (settings.UsageProvider == UsageProvider.Codex) widget.UpdateUsage(UsageDisplay.Empty);
            });
        }
    }

    private async Task RefreshLimitsAsync(CancellationToken cancellationToken)
    {
        if (settings.UsageProvider != UsageProvider.Codex || codexClient == null ||
            !await refreshGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var result = await codexClient.RequestAsync("account/rateLimits/read", null, cancellationToken);
            var display = RateLimitParser.Project(RateLimitParser.Parse(result), TimeZoneInfo.Local);
            _ = application.Dispatcher.BeginInvoke(() =>
            {
                if (settings.UsageProvider == UsageProvider.Codex) widget.UpdateUsage(display);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            _ = application.Dispatcher.BeginInvoke(() =>
            {
                if (settings.UsageProvider == UsageProvider.Codex) widget.UpdateUsage(UsageDisplay.Empty);
            });
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task RunRefreshTimerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await refreshTimer.WaitForNextTickAsync(cancellationToken))
            {
                if (settings.UsageProvider == UsageProvider.Latrix)
                    await RefreshLatrixUsageAsync(cancellationToken);
                else await RefreshLimitsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task StartUsageProviderAsync(CancellationToken cancellationToken)
    {
        if (settings.UsageProvider == UsageProvider.Latrix)
            await RefreshLatrixUsageAsync(cancellationToken);
        else await StartCodexAsync(cancellationToken);
    }

    private async Task RefreshLatrixUsageAsync(CancellationToken cancellationToken)
    {
        if (settings.UsageProvider != UsageProvider.Latrix || !await refreshGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var apiKey = LatrixApiKeyStore.Load();
            if (apiKey == null)
            {
                latrixConfigured = false;
                _ = application.Dispatcher.BeginInvoke(() =>
                {
                    if (settings.UsageProvider == UsageProvider.Latrix) widget.UpdateUsage(UsageDisplay.Empty);
                });
                return;
            }
            var display = await latrix.ReadUsageAsync(apiKey, TimeZoneInfo.Local, cancellationToken);
            _ = application.Dispatcher.BeginInvoke(() =>
            {
                if (settings.UsageProvider == UsageProvider.Latrix) widget.UpdateUsage(display);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            _ = application.Dispatcher.BeginInvoke(() =>
            {
                if (settings.UsageProvider == UsageProvider.Latrix) widget.UpdateUsage(UsageDisplay.Empty);
            });
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task SelectUsageProviderAsync(UsageProvider provider)
    {
        if (provider == UsageProvider.Latrix && !latrixConfigured) return;
        await sourceGate.WaitAsync(lifetime.Token);
        try
        {
            settings.UsageProvider = provider;
            settings.SaveWidget();
            widget.SetUsageLabels(GetPrimaryUsageLabel(), "W");
            widget.UpdateUsage(UsageDisplay.Empty);
            if (provider == UsageProvider.Latrix)
            {
                var previous = codexClient;
                codexClient = null;
                if (previous != null) await previous.DisposeAsync();
                await RefreshLatrixUsageAsync(lifetime.Token);
            }
            else await StartCodexAsync(lifetime.Token);
            _ = application.Dispatcher.BeginInvoke(UpdateTray);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            sourceGate.Release();
        }
    }

    private string GetPrimaryUsageLabel() => settings.UsageProvider == UsageProvider.Latrix ? "6H" : "5H";

    private async Task StartLoginAsync()
    {
        if (loginProcess?.HasExited == false) return;
        try
        {
            loginProcess = CodexProcess.StartLogin();
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(2000, lifetime.Token);
                if (await CodexProcess.IsLoggedInAsync(lifetime.Token))
                {
                    loggedIn = true;
                    if (settings.UsageProvider == UsageProvider.Codex)
                        await StartCodexClientAsync(lifetime.Token);
                    _ = application.Dispatcher.BeginInvoke(UpdateTray);
                    return;
                }
                if (loginProcess.HasExited) return;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _ = application.Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(error.Message,
                "Codex Tracker", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            loginProcess?.Dispose();
            loginProcess = null;
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

    private async void ShowTraySettingsPanel()
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
            if (settings.UsageProvider == UsageProvider.Codex)
            {
                try { loggedIn = await CodexProcess.IsLoggedInAsync(lifetime.Token); } catch { }
            }
            if (quitting || !settingsPanelRequested || settingsPanel != null) return;
            UpdateTray();
            var panel = new SettingsPanelWindow(
                widget.IsVisible,
                loggedIn,
                settings.UpdateAtStartup,
                StartupRegistration.IsEnabled(applicationDirectory),
                settings.HideInFullscreen,
                settings.ShowFiveHour,
                settings.ShowWeekly,
                settings.UsageProvider,
                latrixConfigured,
                updates.RepairNeeded && !updates.IsChecking,
                () =>
                {
                    if (widget.IsVisible) HideWidget();
                    else ShowWidget();
                    settingsPanelRequested = false;
                    CloseSettingsPanel();
                },
                OpenUsageDashboard,
                () => _ = StartLoginAsync(),
                () => _ = updates.CheckAsync(false),
                enabled =>
                {
                    settings.UpdateAtStartup = enabled;
                    settings.SaveUpdatePreference();
                    UpdateTray();
                },
                enabled =>
                {
                    try { StartupRegistration.SetEnabled(applicationDirectory, enabled); }
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
                () =>
                {
                    _ = SelectUsageProviderAsync(UsageProvider.Codex);
                    CloseSettingsPanel();
                },
                () =>
                {
                    _ = SelectUsageProviderAsync(UsageProvider.Latrix);
                    CloseSettingsPanel();
                },
                ShowLatrixApiKeyDialog,
                () =>
                {
                    _ = DisconnectLatrixAsync();
                    CloseSettingsPanel();
                },
                () => trayTogglePending || openingLatrixDialog
            );
            panel.Closing += (_, _) => settingsPanelClosing = true;
            panel.Closed += (_, _) =>
            {
                if (settingsPanel == panel) settingsPanel = null;
                settingsPanelClosing = false;
                settingsPanelRequested = false;
            };
            settingsPanel = panel;
            panel.Show();
            PositionSettingsPanel(panel);
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
        var url = settings.UsageProvider == UsageProvider.Latrix ? LatrixUsageUrl : CodexUsageUrl;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void ShowLatrixApiKeyDialog()
    {
        if (quitting) return;
        var dialog = new LatrixApiKeyWindow();
        if (settingsPanel != null) dialog.Owner = settingsPanel;
        bool? accepted;
        openingLatrixDialog = true;
        try
        {
            accepted = dialog.ShowDialog();
        }
        finally
        {
            openingLatrixDialog = false;
        }
        if (accepted != true) return;
        try
        {
            await latrix.ValidateAsync(dialog.ApiKey, lifetime.Token);
            LatrixApiKeyStore.Save(dialog.ApiKey);
            latrixConfigured = true;
            await SelectUsageProviderAsync(UsageProvider.Latrix);
            CloseSettingsPanel();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show(error.Message, "Codex Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DisconnectLatrixAsync()
    {
        LatrixApiKeyStore.Delete();
        latrixConfigured = false;
        await SelectUsageProviderAsync(UsageProvider.Codex);
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

    private void PositionWidget(double? preferredLeft = null)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var scale = NativeMethods.GetScaleForWindow(widget);
        var bounds = screen.Bounds;
        var workArea = screen.WorkingArea;
        var widthPixels = (int)Math.Round(widget.Width * scale);
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
        if (taskbarHorizontal)
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
            widget.SetAvailableWidth(WidgetWindow.PreferredWidth);
            widthPixels = preferredWidthPixels;
            x = workArea.Left;
            y = bounds.Top + (bounds.Height - heightPixels) * 3 / 10;
        }
        else if (taskbarVertical)
        {
            widget.SetAvailableWidth(WidgetWindow.PreferredWidth);
            widthPixels = preferredWidthPixels;
            x = workArea.Right - widthPixels;
            y = bounds.Top + (bounds.Height - heightPixels) * 3 / 10;
        }
        else widget.SetAvailableWidth(WidgetWindow.PreferredWidth);
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
            FileName = Path.Combine(applicationDirectory, "Codex Tracker.exe"),
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

    private async Task ScheduleStartupUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            await application.Dispatcher.InvokeAsync(async () => await updates.CheckAsync(true));
        }
        catch (OperationCanceledException)
        {
        }
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

    private async void Quit()
    {
        if (quitting) return;
        quitting = true;
        lifetime.Cancel();
        refreshTimer?.Dispose();
        StopPinning();
        try { if (loginProcess?.HasExited == false) loginProcess.Kill(true); } catch { }
        loginProcess?.Dispose();
        var trayMenu = tray?.ContextMenuStrip;
        if (tray != null)
        {
            tray.ContextMenuStrip = null;
            DisposeContextMenuWhenClosed(trayMenu);
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }
        if (codexClient != null) await codexClient.DisposeAsync();
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
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        refreshTimer?.Dispose();
    }
}

internal sealed class SettingsPanelWindow : Window
{
    private bool closing;

    public SettingsPanelWindow(
        bool widgetVisible,
        bool loggedIn,
        bool updateAtStartup,
        bool launchAtStartup,
        bool hideInFullscreen,
        bool showFiveHour,
        bool showWeekly,
        UsageProvider usageProvider,
        bool latrixConfigured,
        bool canRepair,
        Action toggleWidget,
        Action openDashboard,
        Action signIn,
        Action repairUpdate,
        Action<bool> setUpdateAtStartup,
        Action<bool> setLaunchAtStartup,
        Action<bool> setHideInFullscreen,
        Action<bool> setShowFiveHour,
        Action<bool> setShowWeekly,
        Action selectCodexUsage,
        Action selectLatrixUsage,
        Action connectLatrix,
        Action disconnectLatrix,
        Func<bool> ignoreDeactivation
    )
    {
        Width = 360;
        SizeToContent = System.Windows.SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = MediaBrushes.Transparent;
        AllowsTransparency = true;

        var body = new StackPanel();
        body.Children.Add(CreateSectionLabel("Quick actions"));
        body.Children.Add(CreateButton(widgetVisible ? "Hide widget" : "Show widget", toggleWidget));
        body.Children.Add(CreateButton("Open Codex usage dashboard", openDashboard));
        if (canRepair) body.Children.Add(CreateButton("Repair update", repairUpdate));
        body.Children.Add(CreateSeparator());

        body.Children.Add(CreateSectionLabel("Usage source"));
        body.Children.Add(CreateButton(usageProvider == UsageProvider.Codex ? "Codex usage (active)" : "Use Codex usage",
            selectCodexUsage, isSelected: usageProvider == UsageProvider.Codex));
        if (latrixConfigured)
            body.Children.Add(CreateButton(usageProvider == UsageProvider.Latrix
                ? "Latrix usage (active)" : "Use Latrix usage", selectLatrixUsage,
                isSelected: usageProvider == UsageProvider.Latrix));
        body.Children.Add(CreateSeparator());

        body.Children.Add(CreateSectionLabel("Connections"));
        if (!loggedIn) body.Children.Add(CreateButton("Sign in to Codex", signIn));
        if (latrixConfigured) body.Children.Add(CreateButton("Disconnect Latrix API", disconnectLatrix, isDanger: true));
        else body.Children.Add(CreateButton("Connect Latrix API", connectLatrix));
        body.Children.Add(CreateSeparator());

        body.Children.Add(CreateSectionLabel("Preferences"));
        body.Children.Add(CreateToggle("Check update at startup", updateAtStartup, setUpdateAtStartup));
        body.Children.Add(CreateToggle("Launch at Windows startup", launchAtStartup, setLaunchAtStartup));
        body.Children.Add(CreateToggle("Hide in fullscreen apps", hideInFullscreen, setHideInFullscreen));
        body.Children.Add(CreateToggle("Show " + (usageProvider == UsageProvider.Latrix ? "6H" : "5H") + " usage",
            showFiveHour, setShowFiveHour));
        body.Children.Add(CreateToggle("Show weekly usage", showWeekly, setShowWeekly));

        Content = new Border
        {
            Background = new MediaSolidColorBrush(MediaColor.FromRgb(0x16, 0x20, 0x2d)),
            BorderBrush = new MediaSolidColorBrush(MediaColor.FromRgb(0x3e, 0x57, 0x72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 8,
                Opacity = 0.45,
                Color = MediaColor.FromRgb(0, 0, 0),
            },
            Child = body,
        };
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) =>
        {
            if (!closing && !ignoreDeactivation()) Close();
        };
    }

    private static Button CreateButton(string text, Action action, bool isDanger = false, bool isSelected = false)
    {
        var background = new MediaSolidColorBrush(isDanger
            ? MediaColor.FromRgb(0x3c, 0x2a, 0x35)
            : isSelected ? MediaColor.FromRgb(0x1d, 0x46, 0x5d) : MediaColor.FromRgb(0x22, 0x31, 0x44));
        var hoverBackground = new MediaSolidColorBrush(isDanger
            ? MediaColor.FromRgb(0x50, 0x31, 0x3c)
            : isSelected ? MediaColor.FromRgb(0x25, 0x58, 0x72) : MediaColor.FromRgb(0x2b, 0x40, 0x57));
        var border = new MediaSolidColorBrush(isDanger
            ? MediaColor.FromRgb(0x79, 0x48, 0x55)
            : isSelected ? MediaColor.FromRgb(0x4d, 0x85, 0xa5) : MediaColor.FromRgb(0x38, 0x50, 0x68));
        var hoverBorder = new MediaSolidColorBrush(isDanger
            ? MediaColor.FromRgb(0x9b, 0x5a, 0x68)
            : isSelected ? MediaColor.FromRgb(0x65, 0xa5, 0xc8) : MediaColor.FromRgb(0x4d, 0x72, 0x94));
        var button = new Button
        {
            Content = text,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(11, 7, 11, 7),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = new MediaSolidColorBrush(isDanger
                ? MediaColor.FromRgb(0xff, 0xbe, 0xb8)
                : MediaColor.FromRgb(0xe0, 0xea, 0xf7)),
            FontFamily = new MediaFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var template = new ControlTemplate(typeof(Button));
        var buttonBorder = new FrameworkElementFactory(typeof(Border));
        buttonBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        buttonBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        buttonBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
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
        button.Style = style;
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock CreateSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Foreground = new MediaSolidColorBrush(MediaColor.FromRgb(0x91, 0xab, 0xc4)),
            FontFamily = new MediaFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(1, 0, 0, 4),
        };
    }

    private static CheckBox CreateToggle(string text, bool value, Action<bool> changed)
    {
        var toggle = new CheckBox
        {
            Content = text,
            IsChecked = value,
            Foreground = new MediaSolidColorBrush(MediaColor.FromRgb(0xc8, 0xd9, 0xea)),
            FontFamily = new MediaFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            Margin = new Thickness(0, 5, 0, 5),
        };
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }

    private static Border CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            Background = new MediaSolidColorBrush(MediaColor.FromRgb(0x36, 0x49, 0x5f)),
            Margin = new Thickness(0, 9, 0, 9),
        };
    }

}

internal sealed class LatrixApiKeyWindow : Window
{
    private readonly PasswordBox apiKey;

    public string ApiKey => apiKey.Password;

    public LatrixApiKeyWindow()
    {
        Title = "Connect Latrix API";
        Width = 380;
        SizeToContent = System.Windows.SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        var message = new TextBlock
        {
            Text = "Enter a Latrix API key to read your usage limits.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new MediaSolidColorBrush(MediaColor.FromRgb(0x2d, 0x3a, 0x4a)),
            Margin = new Thickness(0, 0, 0, 10),
        };
        apiKey = new PasswordBox
        {
            MinWidth = 300,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var error = new TextBlock
        {
            Foreground = new MediaSolidColorBrush(MediaColor.FromRgb(0xa9, 0x1f, 0x2d)),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
        var connect = new Button { Content = "Connect", IsDefault = true, Padding = new Thickness(12, 5, 12, 5) };
        connect.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(apiKey.Password))
            {
                DialogResult = true;
                return;
            }
            error.Text = "An API key is required.";
            error.Visibility = Visibility.Visible;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(connect);

        var body = new StackPanel();
        body.Children.Add(message);
        body.Children.Add(apiKey);
        body.Children.Add(error);
        body.Children.Add(actions);
        Content = new Border
        {
            Padding = new Thickness(18),
            Child = body,
        };
        Loaded += (_, _) => apiKey.Focus();
    }
}

internal static class StartupRegistration
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexUsageTray";

    public static bool IsEnabled(string applicationDirectory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(string applicationDirectory, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true)
            ?? throw new InvalidOperationException("The Windows startup registry key could not be opened.");
        if (enabled)
            key.SetValue(ValueName, "\"" + Path.Combine(applicationDirectory, "Codex Tracker.exe") + "\"",
                RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);

        var legacyShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Codex Usage Tray.lnk");
        try { File.Delete(legacyShortcut); } catch { }
    }
}
