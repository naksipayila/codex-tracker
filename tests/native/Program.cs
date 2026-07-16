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
        if (args.Length > 0 && args[0] == "app-server") return RunFakeAppServer();
        var tests = new (string Name, Action Run)[]
        {
            ("rateLimitsByLimitId", ParseRateLimitsById),
            ("legacy rateLimits", ParseLegacyRateLimits),
            ("usage clamping", ClampUsage),
            ("Latrix usage projection", ProjectLatrixUsage),
            ("Latrix API authorization", AuthorizeLatrixApi),
            ("configured Codex command", ResolveConfiguredCodexCommand),
            ("standalone current selection", SelectStandaloneCurrent),
            ("visible Codex login command", BuildVisibleLoginCommand),
            ("PowerShell Codex installer", BuildPowerShellInstaller),
            ("cancelled process is not started", RejectCancelledProcess),
            ("app-server CRLF protocol", TestAppServerProtocol),
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

    private static void ParseRateLimitsById()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":41.5,"windowDurationMins":300,"resetsAt":1784118600},"secondary":{"usedPercent":27,"windowDurationMins":10080,"resetsAt":1784118600000}}}}
            """);
        var limits = RateLimitParser.Parse(document.RootElement);
        Equal(2, limits.Count, "window count");
        var display = RateLimitParser.Project(limits, TimeZoneInfo.Utc);
        Equal("59%", display.FiveHour, "five-hour remaining");
        Equal("73%", display.Weekly, "weekly remaining");
        Equal("12:30", display.FiveHourReset, "five-hour reset");
        Equal("15 Tem 12:30", display.WeeklyReset, "weekly reset");
    }

    private static void ParseLegacyRateLimits()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300}}}
            """);
        var limits = RateLimitParser.Parse(document.RootElement);
        Equal(1, limits.Count, "legacy window count");
        Equal("90%", RateLimitParser.Project(limits, TimeZoneInfo.Utc).FiveHour, "legacy remaining");
    }

    private static void ClampUsage()
    {
        var limits = new[]
        {
            new RateLimitWindow("codex", 300, -20, null),
            new RateLimitWindow("codex", 10080, 140, null),
        };
        var display = RateLimitParser.Project(limits, TimeZoneInfo.Utc);
        Equal("100%", display.FiveHour, "upper clamp");
        Equal("0%", display.Weekly, "lower clamp");
    }

    private static void ProjectLatrixUsage()
    {
        using var document = JsonDocument.Parse("""
            {"bucketPercent":40,"capacityPercent":80,"slotEndsAt":"2026-07-16T12:30:00Z","weeklyUsedPercent":26,"weeklyResetsAt":"2026-07-17T08:45:00Z"}
            """);
        var display = LatrixUsageParser.Project(document.RootElement, TimeZoneInfo.Utc);
        Equal("50%", display.FiveHour, "Latrix six-hour remaining");
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
                ? "{\"bucketPercent\":25,\"capacityPercent\":50,\"weeklyUsedPercent\":10}"
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
        Equal("50%", display.FiveHour, "Latrix authorized usage");
        Equal(2, requests.Count, "Latrix request count");
        Equal("/api/me", requests[0].Path, "Latrix validation path");
        Equal("/api/window", requests[1].Path, "Latrix usage path");
        Equal("Bearer unit-test-key", requests[0].Authorization, "Latrix validation authorization");
        Equal("Bearer unit-test-key", requests[1].Authorization, "Latrix usage authorization");
    }

    private static void ResolveConfiguredCodexCommand()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_BINARY");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_BINARY", @"C:\Program Files\Codex\codex.cmd");
            var command = CodexBinaryLocator.Find();
            Equal(@"C:\Program Files\Codex\codex.cmd", command.FileName, "configured path");
            Equal(true, command.UsesCommandProcessor, "command processor selection");
            var startInfo = CodexProcess.CreateStartInfo(command, ["login", "status"], true, false);
            Equal("/d", startInfo.ArgumentList[0], "cmd /d");
            Equal("/s", startInfo.ArgumentList[1], "cmd /s");
            Equal("/c", startInfo.ArgumentList[2], "cmd /c");
            if (!startInfo.ArgumentList[3].Contains("codex.cmd", StringComparison.Ordinal))
                throw new InvalidOperationException("The command line did not contain the configured Codex command.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_BINARY", previous);
        }
    }

    private static void RejectCancelledProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            CodexProcess.CaptureAsync(new CodexCommand("must-not-start.exe", false), [],
                TimeSpan.FromSeconds(1), cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("A cancelled process request unexpectedly completed.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void SelectStandaloneCurrent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexUsageTray-CodexHome-" + Guid.NewGuid().ToString("N"));
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var current = Path.Combine(directory, "packages", "standalone", "current", "bin", "codex.exe");
            var obsolete = Path.Combine(directory, "packages", "standalone", "releases",
                "0.999.0-x86_64-pc-windows-msvc", "bin", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            Directory.CreateDirectory(Path.GetDirectoryName(obsolete)!);
            File.WriteAllBytes(current, [0x4d, 0x5a]);
            File.WriteAllBytes(obsolete, [0x4d, 0x5a]);
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            Equal(current, CodexBinaryLocator.FindStandalone().FileName, "standalone current path");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static void BuildVisibleLoginCommand()
    {
        var executable = @"C:\Program Files\OpenAI Codex\codex.exe";
        var direct = CodexProcess.CreateLoginStartInfo(new CodexCommand(executable, false));
        Equal(executable, direct.FileName, "direct login executable");
        Equal("login", direct.ArgumentList[0], "direct login argument");
        var command = CodexProcess.CreateLoginStartInfo(new CodexCommand(@"C:\Program Files\OpenAI Codex\codex.cmd", true));
        if (!command.Arguments.Contains("\"\"C:\\Program Files\\OpenAI Codex\\codex.cmd\" login\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The visible command-wrapper login was not quoted for cmd.exe /s /c.");
    }

    private static void BuildPowerShellInstaller()
    {
        var invocation = CodexProcess.GetInstallerInvocation();
        if (!invocation.Command.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Codex installer did not use Windows PowerShell.");
        if (!invocation.Arguments.Contains("-NoProfile") || !invocation.Arguments.Contains("-NonInteractive"))
            throw new InvalidOperationException("The Codex installer is missing noninteractive PowerShell safeguards.");
        if (CodexProcess.GetInteractiveInstallerArguments(invocation.Arguments).Contains("-NonInteractive"))
            throw new InvalidOperationException("The interactive installer fallback still disables PowerShell prompts.");
        var command = invocation.Arguments[^1];
        if (!command.Contains(CodexProcess.OfficialInstallerUrl, StringComparison.Ordinal) ||
            command.Contains("npm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Codex installer invocation is not standalone-only.");
    }

    private static void TestAppServerProtocol()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_BINARY");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_BINARY", Environment.ProcessPath);
            var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new CodexAppServerClient();
            client.RateLimitsUpdated += () => updated.TrySetResult();
            client.StartAsync().GetAwaiter().GetResult();
            var result = client.RequestAsync("account/rateLimits/read", null).GetAwaiter().GetResult();
            var display = RateLimitParser.Project(RateLimitParser.Parse(result), TimeZoneInfo.Utc);
            Equal("75%", display.FiveHour, "fake app-server remaining");
            if (!updated.Task.Wait(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("The app-server update notification was not observed.");
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_BINARY", previous);
        }
    }

    private static int RunFakeAppServer()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            Console.Error.WriteLine("fake app-server diagnostic");
            var initialize = ReadCrlfLine(input) ?? throw new InvalidOperationException("Missing initialize request.");
            using (var message = JsonDocument.Parse(initialize))
            {
                var id = message.RootElement.GetProperty("id").GetInt32();
                WriteProtocolLine(output, $"{{\"id\":{id},\"result\":{{}}}}");
            }
            var initialized = ReadCrlfLine(input) ?? throw new InvalidOperationException("Missing initialized notification.");
            using (var message = JsonDocument.Parse(initialized))
                Equal("initialized", message.RootElement.GetProperty("method").GetString(), "initialized method");

            var request = ReadCrlfLine(input) ?? throw new InvalidOperationException("Missing rate-limit request.");
            using (var message = JsonDocument.Parse(request))
            {
                Equal("account/rateLimits/read", message.RootElement.GetProperty("method").GetString(), "rate-limit method");
                Equal(JsonValueKind.Null, message.RootElement.GetProperty("params").ValueKind, "rate-limit params");
                var id = message.RootElement.GetProperty("id").GetInt32();
                WriteProtocolLine(output,
                    $"{{\"id\":{id},\"result\":{{\"rateLimitsByLimitId\":{{\"codex\":{{\"primary\":{{\"usedPercent\":25,\"windowDurationMins\":300}}}}}}}}}}");
            }
            WriteProtocolLine(output, "{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
            Thread.Sleep(200);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 9;
        }
    }

    private static string ReadCrlfLine(Stream input)
    {
        using var buffer = new MemoryStream();
        var previous = -1;
        while (true)
        {
            var value = input.ReadByte();
            if (value < 0) return buffer.Length == 0 ? null : throw new InvalidDataException("Protocol line ended without CRLF.");
            if (value == '\n')
            {
                if (previous != '\r') throw new InvalidDataException("Protocol line was not CRLF-delimited.");
                var bytes = buffer.ToArray();
                return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
            }
            buffer.WriteByte((byte)value);
            previous = value;
        }
    }

    private static void WriteProtocolLine(Stream output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\r\n");
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private static void PersistSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexUsageTray-NativeTests-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA");
        try
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA", directory);
            File.WriteAllText(Path.Combine(directory, "widget-position.json"),
                "{\"xRatio\":1.5,\"hideInFullscreen\":false}");
            File.WriteAllText(Path.Combine(directory, "update-preferences.json"), "{\"updateAtStartup\":false}");
            var settings = NativeSettings.Load();
            Equal(1d, settings.XRatio, "ratio clamp");
            Equal(false, settings.HideInFullscreen, "fullscreen load");
            Equal(true, settings.ShowFiveHour, "five-hour visibility default");
            Equal(true, settings.ShowWeekly, "weekly visibility default");
            Equal(UsageProvider.Codex, settings.UsageProvider, "usage provider default");
            Equal(false, settings.UpdateAtStartup, "update preference load");
            settings.XRatio = 0.42;
            settings.ShowFiveHour = false;
            settings.ShowWeekly = true;
            settings.UsageProvider = UsageProvider.Latrix;
            settings.SaveWidget();
            settings.UpdateAtStartup = true;
            settings.SaveUpdatePreference();
            using var widget = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "widget-position.json")));
            Equal(0.42, widget.RootElement.GetProperty("xRatio").GetDouble(), "saved ratio");
            Equal(false, widget.RootElement.GetProperty("showFiveHour").GetBoolean(), "saved five-hour visibility");
            Equal(true, widget.RootElement.GetProperty("showWeekly").GetBoolean(), "saved weekly visibility");
            Equal("Latrix", widget.RootElement.GetProperty("usageProvider").GetString(), "saved usage provider");
            LatrixApiKeyStore.Save("unit-test-key");
            Equal("unit-test-key", LatrixApiKeyStore.Load(), "saved Latrix key");
            if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "latrix-api-key.dat"))).Contains("unit-test-key"))
                throw new InvalidOperationException("The Latrix key was stored without encryption.");
            LatrixApiKeyStore.Delete();
            Equal(null, LatrixApiKeyStore.Load(), "deleted Latrix key");
            using var update = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "update-preferences.json")));
            Equal(true, update.RootElement.GetProperty("updateAtStartup").GetBoolean(), "saved update preference");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_USAGE_TRAY_USER_DATA", previous);
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static void TestSettingsPanelActions()
    {
        var toggleWidget = 0;
        var openDashboard = 0;
        var signIn = 0;
        var repairUpdate = 0;
        var selectCodexUsage = 0;
        var connectLatrix = 0;
        var selectLatrixUsage = 0;
        var disconnectLatrix = 0;
        bool? updateAtStartup = null;
        bool? launchAtStartup = null;
        bool? hideInFullscreen = null;
        bool? showFiveHour = null;
        bool? showWeekly = null;
        var panel = new SettingsPanelWindow(
            true,
            false,
            true,
            false,
            true,
            true,
            false,
            UsageProvider.Codex,
            false,
            true,
            () => toggleWidget += 1,
            () => openDashboard += 1,
            () => signIn += 1,
            () => repairUpdate += 1,
            value => updateAtStartup = value,
            value => launchAtStartup = value,
            value => hideInFullscreen = value,
            value => showFiveHour = value,
            value => showWeekly = value,
            () => selectCodexUsage += 1,
            () => { },
            () => connectLatrix += 1,
            () => { },
            () => false
        );
        var body = (StackPanel)((Border)panel.Content).Child;
        if (body.Children.OfType<TextBlock>().Any(text => text.Text == "Codex Tracker"))
            throw new InvalidOperationException("The settings panel title is still present.");
        Equal("QUICK ACTIONS|USAGE SOURCE|CONNECTIONS|PREFERENCES",
            string.Join("|", body.Children.OfType<TextBlock>().Select(text => text.Text)), "settings section labels");
        var separators = body.Children.OfType<Border>().ToArray();
        Equal(3, separators.Length, "settings section separators");
        foreach (var separator in separators)
            Equal(1d, separator.Height, "settings separator height");
        var hideButton = body.Children.OfType<Button>().Single(button => button.Content as string == "Hide widget");
        Equal(DependencyProperty.UnsetValue, hideButton.ReadLocalValue(Control.BackgroundProperty), "button background style");
        Equal(DependencyProperty.UnsetValue, hideButton.ReadLocalValue(Control.BorderBrushProperty), "button border style");
        Click(body, "Hide widget");
        Click(body, "Open Codex usage dashboard");
        Click(body, "Sign in to Codex");
        Click(body, "Repair update");
        var activeCodexButton = body.Children.OfType<Button>().Single(button => button.Content as string == "Codex usage (active)");
        Equal(FontWeights.SemiBold, activeCodexButton.FontWeight, "active Codex usage style");
        Click(body, "Codex usage (active)");
        Click(body, "Connect Latrix API");
        Equal(1, toggleWidget, "hide widget action");
        Equal(1, openDashboard, "dashboard action");
        Equal(1, signIn, "sign-in action");
        Equal(1, repairUpdate, "repair action");
        Equal(1, selectCodexUsage, "select Codex usage action");
        Equal(1, connectLatrix, "connect Latrix action");
        if (body.Children.OfType<Button>().Any(button => button.Content as string == "Refresh usage"))
            throw new InvalidOperationException("The manual refresh action is still present.");

        Toggle(body, "Check update at startup", false);
        Toggle(body, "Launch at Windows startup", true);
        Toggle(body, "Hide in fullscreen apps", false);
        Toggle(body, "Show 5H usage", false);
        Toggle(body, "Show weekly usage", true);
        Equal(false, updateAtStartup, "update preference action");
        Equal(true, launchAtStartup, "startup preference action");
        Equal(false, hideInFullscreen, "fullscreen preference action");
        Equal(false, showFiveHour, "five-hour visibility action");
        Equal(true, showWeekly, "weekly visibility action");

        var showWidget = 0;
        var hiddenPanel = new SettingsPanelWindow(
            false,
            true,
            false,
            false,
            false,
            true,
            true,
            UsageProvider.Latrix,
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
            _ => { },
            () => { },
            () => selectLatrixUsage += 1,
            () => { },
            () => disconnectLatrix += 1,
            () => false
        );
        var hiddenBody = (StackPanel)((Border)hiddenPanel.Content).Child;
        Click(hiddenBody, "Show widget");
        var activeLatrixButton = hiddenBody.Children.OfType<Button>()
            .Single(button => button.Content as string == "Latrix usage (active)");
        Equal(FontWeights.SemiBold, activeLatrixButton.FontWeight, "active Latrix usage style");
        Click(hiddenBody, "Latrix usage (active)");
        Click(hiddenBody, "Disconnect Latrix API");
        Equal(1, showWidget, "show widget action");
        Equal(1, selectLatrixUsage, "select Latrix usage action");
        Equal(1, disconnectLatrix, "disconnect Latrix action");
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
