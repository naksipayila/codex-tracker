using CodexUsageTray;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Latrix usage projection", ProjectLatrixUsage),
            ("Latrix API authorization", AuthorizeLatrixApi),
            ("atomic settings persistence", PersistSettings),
            ("settings panel actions", TestSettingsPanelActions),
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
        var requests = new List<(string Path, string Authorization)>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization!.ToString()));
            var json = request.RequestUri!.AbsolutePath == "/api/window"
                ? "{\"bucketPercent\":25,\"capacityPercent\":50,\"bucketPercentEstimated\":20,\"weeklyUsedPercent\":10}"
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
        Equal("40%", display.FiveHour, "Latrix authorized usage");
        Equal(2, requests.Count, "Latrix request count");
        Equal("/api/me", requests[0].Path, "Latrix validation path");
        Equal("/api/window", requests[1].Path, "Latrix usage path");
        Equal("Bearer unit-test-key", requests[0].Authorization, "Latrix validation authorization");
        Equal("Bearer unit-test-key", requests[1].Authorization, "Latrix usage authorization");
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
            Equal(false, settings.HideInFullscreen, "fullscreen load");
            Equal(true, settings.ShowFiveHour, "five-hour visibility default");
            Equal(true, settings.ShowWeekly, "weekly visibility default");
            settings.XRatio = 0.42;
            settings.ShowFiveHour = false;
            settings.ShowWeekly = true;
            settings.SaveWidget();
            using var widget = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "widget-position.json")));
            Equal(0.42, widget.RootElement.GetProperty("xRatio").GetDouble(), "saved ratio");
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
        Equal(14d, root.CornerRadius.TopLeft, "settings panel corner radius");
        Equal("QUICK ACTIONS", ((TextBlock)body.Children[0]).Text, "settings first section label");
        Equal("QUICK ACTIONS|PREFERENCES",
            string.Join("|", body.Children.OfType<TextBlock>()
                .Where(text => text.Text == text.Text.ToUpperInvariant())
                .Select(text => text.Text)), "settings section labels");
        var separators = body.Children.OfType<Border>().Where(border => border.Height == 1).ToArray();
        Equal(1, separators.Length, "settings section separators");
        foreach (var separator in separators)
            Equal(1d, separator.Height, "settings separator height");
        var hideButton = body.Children.OfType<Button>().Single(button => button.Content as string == "Hide widget");
        Equal(DependencyProperty.UnsetValue, hideButton.ReadLocalValue(Control.BackgroundProperty), "button background style");
        Equal(DependencyProperty.UnsetValue, hideButton.ReadLocalValue(Control.BorderBrushProperty), "button border style");
        Click(body, "Hide widget");
        Click(body, "Open Latrix usage dashboard");
        Click(body, "Repair update");
        Equal(1, toggleWidget, "hide widget action");
        Equal(1, openDashboard, "dashboard action");
        Equal(1, repairUpdate, "repair action");
        if (body.Children.OfType<Button>().Any(button =>
            (button.Content as string)?.Contains("Codex", StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidOperationException("The settings panel still exposes a Codex action.");
        if (body.Children.OfType<Button>().Any(button =>
            (button.Content as string)?.Contains("usage", StringComparison.OrdinalIgnoreCase) == true &&
            button.Content as string != "Open Latrix usage dashboard"))
            throw new InvalidOperationException("The settings panel still exposes a usage source selector.");
        if (body.Children.OfType<Button>().Any(button => button.Content as string == "Refresh usage"))
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
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => false
        );
        var hiddenBody = (StackPanel)((Border)hiddenPanel.Content).Child;
        Click(hiddenBody, "Show widget");
        Equal(1, showWidget, "show widget action");
        if (hiddenBody.Children.OfType<Button>().Any(button =>
            (button.Content as string)?.Contains("Codex", StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidOperationException("The settings panel still exposes a Codex action.");
        if (hiddenBody.Children.OfType<Button>().Any(button =>
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

    private static void TestWidgetMetricVisibility()
    {
        var widget = new WidgetWindow();
        var usageGrid = (Grid)((Grid)widget.Content).Children[0];
        var fiveHour = (StackPanel)usageGrid.Children[0];
        var divider = (Border)usageGrid.Children[1];
        var weekly = (StackPanel)usageGrid.Children[2];

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

        widget.SetMetricVisibility(false, false);
        Equal(Visibility.Collapsed, fiveHour.Visibility, "hidden all five-hour metric");
        Equal(Visibility.Collapsed, weekly.Visibility, "hidden all weekly metric");
    }

    private static void Click(StackPanel body, string text)
    {
        var button = body.Children.OfType<Button>().Single(candidate => candidate.Content as string == text);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Toggle(StackPanel body, string text, bool value)
    {
        var toggle = body.Children.OfType<CheckBox>().Single(candidate => candidate.Content as string == text);
        toggle.IsChecked = value;
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
