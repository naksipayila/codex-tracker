using CodexUsageTray;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Latrix usage projection", ProjectLatrixUsage),
            ("Latrix telemetry projection", ProjectLatrixTelemetry),
            ("Latrix active projection", ProjectLatrixActive),
            ("Latrix API authorization", AuthorizeLatrixApi),
            ("atomic settings persistence", PersistSettings),
            ("settings panel actions", TestSettingsPanelActions),
            ("update download progress", TestUpdateDownloadProgress),
            ("telemetry dashboard layout", TestTelemetryDashboardLayout),
            ("widget metric visibility", TestWidgetMetricVisibility),
            ("taskbar widget placement", TestTaskbarWidgetPlacement),
        };
        foreach (var test in tests)
        {
            test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"Native unit tests passed: {tests.Length}");
        return 0;
    }

    private static void ProjectLatrixUsage()
    {
        using var document = JsonDocument.Parse("""
            {"bucketPercent":60,"capacityPercent":80,"bucketPercentEstimated":51.25,"slotEndsAt":"2026-07-16T12:30:00Z","weeklyUsedPercent":26,"weeklyResetsAt":"2026-07-17T08:45:00Z"}
            """);
        var display = LatrixUsageParser.Project(document.RootElement, TimeZoneInfo.Utc);
        Equal("64.06%", display.FiveHour, "Latrix six-hour remaining");
        Equal("74%", display.Weekly, "Latrix weekly remaining");
        Equal("12:30", display.FiveHourReset, "Latrix six-hour reset");
        Equal("17 Tem 08:45", display.WeeklyReset, "Latrix weekly reset");
    }

    private static void AuthorizeLatrixApi()
    {
        var requests = new List<(string Path, string Authorization, bool NoCache, bool NoStore)>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization!.ToString(),
                request.Headers.CacheControl?.NoCache == true, request.Headers.CacheControl?.NoStore == true));
            var json = request.RequestUri!.AbsolutePath == "/api/window"
                ? "{\"bucketPercent\":25,\"capacityPercent\":50,\"bucketPercentEstimated\":20,\"weeklyUsedPercent\":10}"
                : request.RequestUri!.AbsolutePath == "/api/active"
                    ? "{\"active\":[{\"userId\":\"u1\",\"name\":\"Ali\",\"model\":\"gpt-5.6-luna\",\"provider\":\"codex\",\"effort\":\"medium\",\"elapsedMs\":19000}]}"
                : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://inference.llai.io/") };
        var client = new LatrixApiClient(http);
        client.ValidateAsync("unit-test-key").GetAwaiter().GetResult();
        var display = client.ReadUsageAsync("unit-test-key", TimeZoneInfo.Utc).GetAwaiter().GetResult();
        var active = client.ReadActiveAsync("unit-test-key").GetAwaiter().GetResult();
        Equal("40%", display.FiveHour, "Latrix authorized usage");
        Equal(3, requests.Count, "Latrix request count");
        Equal("/api/me", requests[0].Path, "Latrix validation path");
        Equal("/api/window", requests[1].Path, "Latrix usage path");
        Equal("/api/active", requests[2].Path, "Latrix active path");
        Equal("Bearer unit-test-key", requests[0].Authorization, "Latrix validation authorization");
        Equal("Bearer unit-test-key", requests[1].Authorization, "Latrix usage authorization");
        Equal("Bearer unit-test-key", requests[2].Authorization, "Latrix active authorization");
        Equal(true, requests[2].NoCache, "Latrix active no-cache request");
        Equal(true, requests[2].NoStore, "Latrix active no-store request");
        Equal("medium", active[0].Effort, "Latrix active effort");
    }

    private static void ProjectLatrixTelemetry()
    {
        using var document = JsonDocument.Parse("""
            {"users":[{"userId":"u1","name":"Ali Taha Yapışkan","role":"ADMIN","online":true,"requests":12,"inputTokens":1000000,"cachedTokens":250000,"outputTokens":50000,"reasoningTokens":12500,"totalTokens":1062500,"models":2,"errors":1,"avgLatencyMs":13100,"lastActive":"2026-07-17T10:00:00Z","breakdown":[{"model":"gpt-5","totalTokens":900000,"requests":10,"efforts":[{"effort":null,"requests":4}]}]},{"userId":"u2","name":"Latrix","role":"ADMIN","online":false,"requests":0,"inputTokens":null,"cachedTokens":null,"outputTokens":null,"reasoningTokens":null,"totalTokens":null,"models":null,"errors":null,"avgLatencyMs":null,"lastActive":null,"breakdown":null}]}
            """);
        var people = LatrixTelemetryParser.Project(document.RootElement);
        Equal(2, people.Count, "telemetry person count");
        Equal("Ali Taha Yapışkan", people[0].Name, "telemetry person name");
        Equal(1, people[0].Breakdown.Count, "telemetry breakdown count");
        Equal("default: 4", people[0].Breakdown[0].Efforts, "telemetry default effort summary");
        Equal(0L, people[1].TotalTokens, "null telemetry total");
        Equal("", people[1].LastActive, "null telemetry last active");
    }

    private static void ProjectLatrixActive()
    {
        using var document = JsonDocument.Parse("""
            {"active":[{"userId":"u1","name":"Ali Taha Yapışkan","model":"gpt-5.6-luna","provider":"codex","effort":"medium","elapsedMs":19000},{"userId":"u2","name":"Latrix","model":null,"provider":"self_hosted","effort":null,"elapsedMs":61000}]}
            """);
        var active = LatrixActiveParser.Project(document.RootElement);
        Equal(2, active.Count, "active user count");
        Equal("Ali Taha Yapışkan", active[0].Name, "active user name");
        Equal("gpt-5.6-luna", active[0].Model, "active model");
        Equal("codex", active[0].Provider, "active provider");
        Equal("medium", active[0].Effort, "active effort");
        Equal(19000L, active[0].ElapsedMs, "active elapsed time");
        Equal("", active[1].Model, "null active model");
        Equal("self-hosted", active[1].Provider, "self-hosted provider");
        Equal("", active[1].Effort, "null active effort");
    }

    private static void PersistSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexUsageTray-NativeTests-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA");
        var previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA", directory);
            var configDirectory = Path.Combine(directory, "config");
            Directory.CreateDirectory(Path.Combine(configDirectory, "opencode"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configDirectory);
            File.WriteAllText(Path.Combine(configDirectory, "opencode", "opencode.json"), """
                {
                  "provider": {
                    "latrix": {
                      "options": { "apiKey": "unit-test-key" }
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(directory, "widget-position.json"),
                "{\"xRatio\":1.5,\"hideInFullscreen\":false}");
            var settings = NativeSettings.Load();
            Equal(1d, settings.XRatio, "ratio clamp");
            Equal(true, settings.LaunchAtStartup, "startup default");
            Equal(false, settings.HideInFullscreen, "fullscreen load");
            Equal(true, settings.ShowFiveHour, "five-hour visibility default");
            Equal(true, settings.ShowWeekly, "weekly visibility default");
            settings.XRatio = 0.42;
            settings.LaunchAtStartup = false;
            settings.ShowFiveHour = false;
            settings.ShowWeekly = true;
            settings.SaveWidget();
            using var widget = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "widget-position.json")));
            Equal(0.42, widget.RootElement.GetProperty("xRatio").GetDouble(), "saved ratio");
            Equal(false, widget.RootElement.GetProperty("launchAtStartup").GetBoolean(), "saved startup preference");
            Equal(false, widget.RootElement.GetProperty("showFiveHour").GetBoolean(), "saved five-hour visibility");
            Equal(true, widget.RootElement.GetProperty("showWeekly").GetBoolean(), "saved weekly visibility");
            if (widget.RootElement.TryGetProperty("usageProvider", out _))
                throw new InvalidOperationException("The removed usage provider setting is still persisted.");
            Equal("unit-test-key", OpenCodeConfig.LoadApiKey(), "OpenCode Latrix key");
            File.WriteAllText(Path.Combine(directory, "latrix-api-key.dat"), "legacy-key");
            OpenCodeConfig.RemoveLegacyStoredKey();
            if (File.Exists(Path.Combine(directory, "latrix-api-key.dat")))
                throw new InvalidOperationException("The legacy Latrix key file was not removed.");
            File.WriteAllText(Path.Combine(directory, "update-preferences.json"), "legacy");
            _ = NativeSettings.Load();
            if (File.Exists(Path.Combine(directory, "update-preferences.json")))
                throw new InvalidOperationException("The removed startup update preference was not cleaned up.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA", previous);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static void TestSettingsPanelActions()
    {
        var toggleWidget = 0;
        var openDashboard = 0;
        var openTelemetry = 0;
        var repairUpdate = 0;
        bool? launchAtStartup = null;
        bool? hideInFullscreen = null;
        bool? showFiveHour = null;
        bool? showWeekly = null;
            var panel = new SettingsPanelWindow(
            true,
            false,
            true,
            true,
            false,
             true,
              () => toggleWidget += 1,
              () => openDashboard += 1,
              () => openTelemetry += 1,
              () => repairUpdate += 1,
            value => launchAtStartup = value,
            value => hideInFullscreen = value,
            value => showFiveHour = value,
            value => showWeekly = value,
            () => false
        );
        var body = (StackPanel)((Border)panel.Content).Child;
        Equal(392d, panel.Width, "settings panel width");
        var root = (Border)panel.Content;
        Equal(12d, root.CornerRadius.TopLeft, "settings panel corner radius");
        Equal(Color.FromRgb(0x14, 0x14, 0x14), ((SolidColorBrush)root.Background).Color, "settings panel dark gray background");
        Equal("QUICK ACTIONS", ((TextBlock)body.Children[0]).Text, "settings first section label");
        Equal("QUICK ACTIONS|PREFERENCES",
            string.Join("|", body.Children.OfType<TextBlock>()
                .Where(text => text.Text == text.Text.ToUpperInvariant())
                .Select(text => text.Text)), "settings section labels");
        var separators = body.Children.OfType<Border>().Where(border => border.Height == 1).ToArray();
        Equal(1, separators.Length, "settings section separators");
        foreach (var separator in separators)
            Equal(1d, separator.Height, "settings separator height");
        var hideButton = FindControls<Button>(body).Single(button => button.Content as string == "Hide widget");
        Equal(DependencyProperty.UnsetValue, hideButton.ReadLocalValue(Control.BackgroundProperty), "button background style");
        Equal(DependencyProperty.UnsetValue, hideButton.ReadLocalValue(Control.BorderBrushProperty), "button border style");
        Click(body, "Hide widget");
        Click(body, "Open Latrix usage dashboard");
        Click(body, "Open telemetry window");
        Click(body, "Repair update");
        Equal(1, toggleWidget, "hide widget action");
        Equal(1, openDashboard, "dashboard action");
        Equal(1, openTelemetry, "telemetry action");
        Equal(1, repairUpdate, "repair action");
        if (FindControls<Button>(body).Any(button =>
            (button.Content as string)?.Contains("Codex", StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidOperationException("The settings panel still exposes a Codex action.");
        if (FindControls<Button>(body).Any(button =>
            (button.Content as string)?.Contains("usage", StringComparison.OrdinalIgnoreCase) == true &&
             button.Content as string != "Open Latrix usage dashboard"))
            throw new InvalidOperationException("The settings panel still exposes a usage source selector.");
        if (FindControls<Button>(body).Any(button => button.Content as string == "Refresh usage"))
            throw new InvalidOperationException("The manual refresh action is still present.");

        Toggle(body, "Launch at Windows startup", true);
        Toggle(body, "Hide in fullscreen apps", false);
        Toggle(body, "Show 6H usage", false);
        Toggle(body, "Show weekly usage", true);
        Equal(true, launchAtStartup, "startup preference action");
        Equal(false, hideInFullscreen, "fullscreen preference action");
        Equal(false, showFiveHour, "five-hour visibility action");
        Equal(true, showWeekly, "weekly visibility action");

        var showWidget = 0;
        var hiddenPanel = new SettingsPanelWindow(
            false,
            false,
            false,
            true,
            true,
            false,
             () => showWidget += 1,
             () => { },
             () => { },
             () => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => false
        );
        var hiddenBody = (StackPanel)((Border)hiddenPanel.Content).Child;
        Click(hiddenBody, "Show widget");
        Equal(1, showWidget, "show widget action");
        if (FindControls<Button>(hiddenBody).Any(button =>
            (button.Content as string)?.Contains("Codex", StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidOperationException("The settings panel still exposes a Codex action.");
        if (FindControls<Button>(hiddenBody).Any(button =>
             (button.Content as string)?.Contains("usage", StringComparison.OrdinalIgnoreCase) == true &&
             button.Content as string != "Open Latrix usage dashboard"))
            throw new InvalidOperationException("The settings panel still exposes a usage source selector.");
    }

    private static void TestTaskbarWidgetPlacement()
    {
        var fullWidth = NativeMethods.CalculateTaskbarWidgetPlacement(
            770,
            0,
            1920,
            340,
            [new HorizontalRange(0, 250), new HorizontalRange(900, 1200), new HorizontalRange(1700, 1920)]
        );
        Equal(560, fullWidth.Left, "nearest full-width taskbar slot");
        Equal(340, fullWidth.Width, "full-width taskbar slot size");

        var constrained = NativeMethods.CalculateTaskbarWidgetPlacement(
            330,
            0,
            600,
            340,
            [new HorizontalRange(0, 240), new HorizontalRange(480, 600)]
        );
        Equal(240, constrained.Left, "constrained taskbar slot left");
        Equal(240, constrained.Width, "constrained taskbar slot size");

        var compact = NativeMethods.CalculateTaskbarWidgetPlacement(
            60,
            0,
            100,
            340,
            [new HorizontalRange(0, 20)]
        );
        Equal(20, compact.Left, "compact taskbar slot left");
        Equal(80, compact.Width, "compact taskbar slot size");
    }

    private static void TestUpdateDownloadProgress()
    {
        var progress = new UpdateDownloadWindow();
        Equal("Downloading update...", progress.StatusText, "update progress initial status");
        Equal(true, progress.IsIndeterminate, "update progress initial mode");

        progress.ReportDownload(5 * 1024 * 1024, 10 * 1024 * 1024);
        Equal(false, progress.IsIndeterminate, "update progress determinate mode");
        Equal(50d, progress.ProgressValue, "update download percentage");
        Equal("5.0 MB / 10.0 MB", progress.DetailsText, "update download byte count");

        progress.ReportVerifying();
        Equal("Verifying update...", progress.StatusText, "update verification status");
        Equal(true, progress.IsIndeterminate, "update verification mode");
    }

    private static void TestTelemetryDashboardLayout()
    {
        using var panel = new TelemetryPanel(new LatrixApiClient(), "");
        Equal(Color.FromRgb(0x0d, 0x0d, 0x0d), ((SolidColorBrush)panel.Background).Color, "telemetry dark gray canvas");
        var root = (Grid)panel.Content;
        Equal(1, root.ColumnDefinitions.Count, "telemetry shell column count");
        Equal(1, root.Children.Count, "telemetry shell child count");
        Equal(1120d, panel.MinWidth, "telemetry dashboard minimum width");
        var dashboard = (Grid)root.Children[0];
        Equal(5, dashboard.RowDefinitions.Count, "telemetry dashboard row count");
        var periodSelector = (Grid)dashboard.Children[0];
        var periodButtons = (StackPanel)periodSelector.Children[1];
        Equal(3, periodButtons.Children.Count, "telemetry period button count");
        Equal("Daily", ((Button)periodButtons.Children[0]).Content, "daily telemetry period");
        Equal("7 days", ((Button)periodButtons.Children[1]).Content, "weekly telemetry period");
        Equal("Monthly", ((Button)periodButtons.Children[2]).Content, "monthly telemetry period");
        var summary = (Grid)dashboard.Children[2];
        Equal(5, summary.ColumnDefinitions.Count, "telemetry summary column count");
        Equal(1, summary.RowDefinitions.Count, "telemetry summary row count");
        Equal(5, summary.Children.Count, "telemetry summary card children");
        Equal(0, Grid.GetColumn(summary.Children[0]), "total tokens summary column");
        Equal(1, Grid.GetColumn(summary.Children[1]), "requests summary column");
        Equal(2, Grid.GetColumn(summary.Children[2]), "active summary column");
        Equal(3, Grid.GetColumn(summary.Children[3]), "errors summary column");
        Equal(4, Grid.GetColumn(summary.Children[4]), "latency summary column");

        var content = (Grid)dashboard.Children[4];
        Equal(3, content.ColumnDefinitions.Count, "telemetry content column count");
        Equal(2, content.Children.Count, "telemetry table and online panel");
        var leftContent = (Grid)content.Children[0];
        Equal(1, leftContent.RowDefinitions.Count, "telemetry left content row count");
        var table = (Border)leftContent.Children[0];
        Equal(2, ((Grid)table.Child).RowDefinitions.Count, "telemetry table structure");
        var header = (Grid)((Grid)table.Child).Children[0];
        Equal("TEAM MEMBER", ((TextBlock)header.Children[0]).Text, "telemetry table header");
        var online = (Border)content.Children[1];
        Equal(2, Grid.GetRowSpan(online), "online panel row span");
        var onlineContent = (Grid)online.Child;
        var onlineTitle = (Grid)onlineContent.Children[0];
        Equal(1, onlineTitle.Children.Count, "online panel title has no count label");
        Equal("ONLINE NOW", ((TextBlock)((StackPanel)onlineTitle.Children[0]).Children[1]).Text, "online panel title");
    }

    private static void TestWidgetMetricVisibility()
    {
        var widget = new WidgetWindow();
        Equal(310d, WidgetWindow.PreferredWidth, "taskbar widget preferred width");
        var root = (Grid)widget.Content;
        var usageGrid = (Grid)root.Children[0];
        var onlineCount = (TextBlock)root.Children[1];
        var fiveHour = (Grid)usageGrid.Children[0];
        var divider = (Border)usageGrid.Children[1];
        var weekly = (Grid)usageGrid.Children[2];
        if (!usageGrid.ColumnDefinitions[0].Width.IsStar || !usageGrid.ColumnDefinitions[2].Width.IsStar)
            throw new InvalidOperationException("taskbar metric columns are not equally flexible");

        widget.SetUsageLabels("6H", "W");
        Equal("6H", ((TextBlock)fiveHour.Children[0]).Text, "Latrix primary label");

        widget.SetMetricVisibility(false, true);
        Equal(Visibility.Collapsed, fiveHour.Visibility, "hidden five-hour metric");
        Equal(Visibility.Collapsed, divider.Visibility, "hidden single-metric divider");
        Equal(Visibility.Visible, weekly.Visibility, "visible weekly metric");

        widget.SetMetricVisibility(true, false);
        Equal(Visibility.Visible, fiveHour.Visibility, "visible five-hour metric");
        Equal(Visibility.Collapsed, divider.Visibility, "hidden five-hour-only divider");
        Equal(Visibility.Collapsed, weekly.Visibility, "hidden weekly metric");

        widget.SetMetricVisibility(true, true);
        Equal(18d, usageGrid.ColumnDefinitions[1].Width.Value, "taskbar metric center gap");
        if (!usageGrid.ColumnDefinitions[0].Width.IsAuto || !usageGrid.ColumnDefinitions[2].Width.IsAuto)
            throw new InvalidOperationException("taskbar content columns are not compact");
        Equal(4d, fiveHour.ColumnDefinitions[1].Width.Value, "taskbar label value gap");
        Equal(4d, fiveHour.ColumnDefinitions[3].Width.Value, "taskbar value reset gap");
        if (!fiveHour.ColumnDefinitions[0].Width.IsAuto || !fiveHour.ColumnDefinitions[2].Width.IsAuto ||
            !fiveHour.ColumnDefinitions[4].Width.IsAuto)
            throw new InvalidOperationException("taskbar metric slots are not compact");
        Equal(HorizontalAlignment.Left, fiveHour.HorizontalAlignment, "taskbar left group alignment");
        Equal(HorizontalAlignment.Left, weekly.HorizontalAlignment, "taskbar right group alignment");

        widget.UpdateOnlineUsers(new[]
        {
            new LatrixActiveUser("u1", "Ali Taha Yapışkan", "gpt-5.6-luna", "codex", "medium", 1000),
            new LatrixActiveUser("u2", "Zeynep", "gpt-5.6-luna", "codex", "medium", 1000),
            new LatrixActiveUser("u3", "Zeynep", "gpt-5.6-luna", "codex", "medium", 1000),
        });
        Equal("2", onlineCount.Text, "online user count");
        var tooltip = (ToolTip)onlineCount.ToolTip;
        Equal(Theme.TextPrimary, ((SolidColorBrush)((TextBlock)tooltip.Content).Foreground).Color, "online tooltip text color");
        Equal("2 kişi online\r\nAli Taha Yapışkan\r\nZeynep", ((TextBlock)tooltip.Content).Text, "online user tooltip");

        widget.ClearOnlineUsers();
        Equal("--", onlineCount.Text, "unavailable online user count");
        Equal("Online bilgisi alınamadı", ((TextBlock)((ToolTip)onlineCount.ToolTip).Content).Text, "unavailable online tooltip");

        widget.SetMetricVisibility(false, false);
        Equal(Visibility.Collapsed, fiveHour.Visibility, "hidden all five-hour metric");
        Equal(Visibility.Collapsed, weekly.Visibility, "hidden all weekly metric");
    }

    private static void Click(StackPanel body, string text)
    {
        var button = FindControls<Button>(body).Single(candidate => candidate.Content as string == text);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Toggle(StackPanel body, string text, bool value)
    {
        var toggle = FindControls<CheckBox>(body).Single(candidate => candidate.Content as string == text);
        toggle.IsChecked = value;
    }

    private static IEnumerable<T> FindControls<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in FindControls<T>(child)) yield return descendant;
        }
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> response;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            this.response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
