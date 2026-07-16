using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsageTray;

internal sealed record CodexCommand(string FileName, bool UsesCommandProcessor);
internal sealed record CommandCapture(int ExitCode, string Output, string Error);
internal sealed record RateLimitWindow(string LimitId, int WindowDurationMinutes, double UsedPercent, long? ResetsAt);
internal sealed record UsageDisplay(string FiveHour, string FiveHourReset, string Weekly, string WeeklyReset)
{
    public static readonly UsageDisplay Empty = new("--", "", "--", "");
}

internal static class CodexBinaryLocator
{
    public static CodexCommand Find()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_BINARY");
        if (!string.IsNullOrWhiteSpace(configured)) return Create(configured);

        return FindStandalone() ?? new CodexCommand("codex.exe", false);
    }

    public static CodexCommand FindStandalone()
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Path.GetFullPath(configuredHome);
        var standalone = Path.Combine(codexHome, "packages", "standalone");
        foreach (var currentCandidate in new[]
        {
            Path.Combine(standalone, "current", "bin", "codex.exe"),
            Path.Combine(standalone, "current", "codex.exe"),
        })
        {
            if (File.Exists(currentCandidate)) return new CodexCommand(currentCandidate, false);
        }

        var releases = Path.Combine(standalone, "releases");
        try
        {
            foreach (var release in Directory.GetDirectories(releases)
                .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(release, "bin", "codex.exe"),
                    Path.Combine(release, "codex.exe"),
                })
                {
                    if (File.Exists(candidate)) return new CodexCommand(candidate, false);
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static CodexCommand Create(string path) =>
        new(path, path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
}

internal static class CodexProcess
{
    internal const string OfficialInstallerUrl = "https://chatgpt.com/codex/install.ps1";

    public static ProcessStartInfo CreateStartInfo(
        CodexCommand command,
        IEnumerable<string> arguments,
        bool redirect,
        bool visible)
    {
        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = !visible,
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = redirect,
        };
        if (command.UsesCommandProcessor)
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildCommandLine(command.FileName, argumentList));
        }
        else
        {
            startInfo.FileName = command.FileName;
            foreach (var argument in argumentList) startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    public static async Task<CommandCapture> CaptureAsync(
        CodexCommand command,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string> environment = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo(command, arguments, true, false);
        if (environment != null)
        {
            foreach (var value in environment) startInfo.Environment[value.Key] = value.Value;
        }
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Windows did not start {command.FileName}.");
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
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
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new TimeoutException($"{command.FileName} did not finish within {timeout.TotalSeconds:0} seconds.");
        }
        return new CommandCapture(process.ExitCode, await outputTask, await errorTask);
    }

    public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await CaptureAsync(CodexBinaryLocator.Find(), ["--version"], TimeSpan.FromSeconds(15), cancellationToken);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public static async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        var invocation = GetInstallerInvocation();
        var result = await CaptureAsync(
            invocation.Command,
            invocation.Arguments,
            TimeSpan.FromMinutes(10),
            cancellationToken,
            new Dictionary<string, string> { ["CODEX_NON_INTERACTIVE"] = "1" });
        if (result.ExitCode != 0)
        {
            var details = (result.Error + Environment.NewLine + result.Output).Trim();
            if (!details.Contains("Cannot replace older standalone install without confirmation", StringComparison.Ordinal) ||
                await RunInteractiveInstallerAsync(invocation, cancellationToken) != 0)
                throw new InvalidOperationException(details);
        }
        var standalone = CodexBinaryLocator.FindStandalone()
            ?? throw new InvalidOperationException("The official installer did not create a standalone Codex release.");
        var probe = await CaptureAsync(standalone, ["--version"], TimeSpan.FromSeconds(30), cancellationToken);
        if (probe.ExitCode != 0)
            throw new InvalidOperationException("The installed standalone Codex executable failed its version check.");
    }

    internal static (CodexCommand Command, string[] Arguments) GetInstallerInvocation()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return (
            new CodexCommand(powershell, false),
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                "$ProgressPreference='SilentlyContinue'; Invoke-RestMethod -UseBasicParsing '" +
                    OfficialInstallerUrl + "' | Invoke-Expression",
            ]
        );
    }

    private static async Task<int> RunInteractiveInstallerAsync(
        (CodexCommand Command, string[] Arguments) invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo(invocation.Command, GetInteractiveInstallerArguments(invocation.Arguments), false, true);
        startInfo.Environment.Remove("CODEX_NON_INTERACTIVE");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Windows did not start the interactive Codex installer.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
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
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new TimeoutException("The interactive Codex installer did not finish within 10 minutes.");
        }
    }

    internal static string[] GetInteractiveInstallerArguments(IEnumerable<string> arguments) =>
        arguments.Where(argument => !string.Equals(argument, "-NonInteractive", StringComparison.OrdinalIgnoreCase)).ToArray();

    public static async Task<bool> IsLoggedInAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await CaptureAsync(CodexBinaryLocator.Find(), ["login", "status"],
                TimeSpan.FromSeconds(20), cancellationToken);
            return (result.Output + result.Error).Contains("Logged in", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public static Process StartLogin()
    {
        var startInfo = CreateLoginStartInfo(CodexBinaryLocator.Find());
        var process = Process.Start(startInfo);
        return process ?? throw new InvalidOperationException("Windows did not start Codex login.");
    }

    internal static ProcessStartInfo CreateLoginStartInfo(CodexCommand command)
    {
        if (!command.UsesCommandProcessor)
        {
            var direct = new ProcessStartInfo
            {
                FileName = command.FileName,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            direct.ArgumentList.Add("login");
            return direct;
        }

        var commandLine = BuildCommandLine(command.FileName, ["login"]);
        return new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /s /c \"" + commandLine + "\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
    }

    private static string BuildCommandLine(string fileName, IEnumerable<string> arguments)
    {
        return string.Join(" ", new[] { Quote(fileName) }.Concat(arguments.Select(Quote)));
    }

    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(character => char.IsWhiteSpace(character) || character is '"' or '&' or '|' or '<' or '>'))
            return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process process;
    private int nextId;
    private Task stdoutTask;
    private Task stderrTask;
    private int disposing;

    public event Action RateLimitsUpdated;
    public event Action Disconnected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        process = new Process
        {
            StartInfo = CodexProcess.CreateStartInfo(CodexBinaryLocator.Find(), ["app-server"], true, false),
            EnableRaisingEvents = true,
        };
        process.Exited += (_, _) =>
        {
            if (Volatile.Read(ref disposing) != 0) return;
            FailPending(new InvalidOperationException($"Codex app-server stopped ({SafeExitCode(process)})."));
            Disconnected?.Invoke();
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Windows did not start Codex app-server.");
        process.StandardInput.AutoFlush = true;
        process.StandardInput.NewLine = "\r\n";
        stdoutTask = ReadOutputAsync(lifetime.Token);
        stderrTask = process.StandardError.ReadToEndAsync(lifetime.Token);

        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "codex_usage_tray", title = "Codex Tracker", version = "1.0.0" },
            capabilities = (object)null,
        }, cancellationToken);
        await NotifyAsync("initialized", new { }, cancellationToken);
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposing) != 0 || process == null || process.HasExited)
            throw new InvalidOperationException("Codex app-server is not running.");
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Could not reserve a Codex request ID.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var registration = timeout.Token.Register(() =>
        {
            if (pending.TryRemove(id, out var request)) request.TrySetCanceled(timeout.Token);
        });
        try
        {
            await WriteAsync(new { method, id, @params = parameters }, timeout.Token);
            return await completion.Task;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref disposing) != 0) throw new OperationCanceledException("Codex app-server is stopping.");
            var json = JsonSerializer.Serialize(message);
            await process.StandardInput.WriteAsync(json.AsMemory(), cancellationToken);
            await process.StandardInput.WriteAsync("\r\n".AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null) break;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id) &&
                        pending.TryRemove(id, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            var message = error.TryGetProperty("message", out var detail)
                                ? detail.GetString() : "Codex app-server request failed.";
                            completion.TrySetException(new InvalidOperationException(message));
                        }
                        else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                        else completion.TrySetResult(default);
                    }
                    else if (root.TryGetProperty("method", out var method) &&
                        method.GetString() == "account/rateLimits/updated")
                    {
                        RateLimitsUpdated?.Invoke();
                    }
                }
                catch (JsonException)
                {
                    // Diagnostics can be interleaved with protocol output.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            FailPending(error);
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var pair in pending)
            if (pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) != 0) return;
        lifetime.Cancel();
        FailPending(new OperationCanceledException("Codex app-server stopped."));
        try
        {
            if (process != null && !process.HasExited) process.Kill(true);
        }
        catch
        {
        }
        try
        {
            if (process != null && !process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch
        {
        }
        var streams = Task.WhenAll(stdoutTask ?? Task.CompletedTask, stderrTask ?? Task.CompletedTask);
        try { await Task.WhenAny(streams, Task.Delay(2000)); } catch { }
        process?.Dispose();
    }

    private static string SafeExitCode(Process value)
    {
        try { return value.HasExited ? value.ExitCode.ToString(CultureInfo.InvariantCulture) : "unknown"; }
        catch { return "unknown"; }
    }
}

internal static class RateLimitParser
{
    public static IReadOnlyList<RateLimitWindow> Parse(JsonElement result)
    {
        var snapshots = new List<(JsonElement Snapshot, string FallbackId)>();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object)
        {
            snapshots.AddRange(byId.EnumerateObject().Select(property => (property.Value, property.Name)));
        }
        else if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimits", out var limits))
        {
            snapshots.Add((limits, "codex"));
        }

        var windows = new List<RateLimitWindow>();
        foreach (var entry in snapshots)
        {
            var snapshot = entry.Snapshot;
            if (snapshot.ValueKind != JsonValueKind.Object) continue;
            var limitId = GetString(snapshot, "limitId") ?? entry.FallbackId;
            AddWindow(snapshot, "primary", limitId, windows);
            AddWindow(snapshot, "secondary", limitId, windows);
        }
        return windows;
    }

    public static UsageDisplay Project(IReadOnlyList<RateLimitWindow> limits, TimeZoneInfo timeZone)
    {
        var codex = limits.Where(limit => limit.LimitId == "codex").ToArray();
        var fiveHour = codex.FirstOrDefault(limit => limit.WindowDurationMinutes == 300);
        var weekly = codex.FirstOrDefault(limit => limit.WindowDurationMinutes == 10080);
        return new UsageDisplay(
            FormatPercent(fiveHour),
            FormatReset(fiveHour, false, timeZone),
            FormatPercent(weekly),
            FormatReset(weekly, true, timeZone));
    }

    private static void AddWindow(JsonElement snapshot, string name, string limitId, List<RateLimitWindow> windows)
    {
        if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return;
        if (!window.TryGetProperty("windowDurationMins", out var duration) || !duration.TryGetInt32(out var minutes)) return;
        var used = window.TryGetProperty("usedPercent", out var percent) && percent.TryGetDouble(out var value) ? value : 0;
        long? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var reset) && reset.TryGetInt64(out var timestamp)) resetsAt = timestamp;
        windows.Add(new RateLimitWindow(limitId, minutes, used, resetsAt));
    }

    private static string FormatPercent(RateLimitWindow limit)
    {
        if (limit == null) return "--";
        var remaining = Math.Clamp(100 - limit.UsedPercent, 0, 100);
        return Math.Round(remaining, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatReset(RateLimitWindow limit, bool includeDate, TimeZoneInfo timeZone)
    {
        if (limit?.ResetsAt == null) return "";
        var timestamp = limit.ResetsAt.Value < 10_000_000_000 ? limit.ResetsAt.Value * 1000 : limit.ResetsAt.Value;
        DateTimeOffset date;
        try { date = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), timeZone); }
        catch { return ""; }
        return includeDate
            ? date.ToString("dd MMM HH:mm", CultureInfo.GetCultureInfo("tr-TR"))
            : date.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
