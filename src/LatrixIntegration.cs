using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsageTray;

internal static class OpenCodeConfig
{
    private const string ConfigFileName = "opencode.json";
    private const string JsoncConfigFileName = "opencode.jsonc";
    private const string LegacyKeyFileName = "latrix-api-key.dat";

    public static string LoadApiKey()
    {
        foreach (var path in GetConfigPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                if (!TryGetApiKey(document.RootElement, out var apiKey)) continue;
                return apiKey;
            }
            catch
            {
            }
        }
        return null;
    }

    public static void RemoveLegacyStoredKey()
    {
        try { File.Delete(Path.Combine(NativeSettings.GetUserDataDirectory(), LegacyKeyFileName)); } catch { }
    }

    private static bool TryGetApiKey(JsonElement root, out string apiKey)
    {
        apiKey = null;
        if (!root.TryGetProperty("provider", out var providers) || providers.ValueKind != JsonValueKind.Object ||
            !providers.TryGetProperty("latrix", out var latrix) || latrix.ValueKind != JsonValueKind.Object ||
            !latrix.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object ||
            !options.TryGetProperty("apiKey", out var value) || value.ValueKind != JsonValueKind.String)
            return false;

        apiKey = value.GetString()?.Trim();
        return !string.IsNullOrWhiteSpace(apiKey);
    }

    private static IEnumerable<string> GetConfigPaths()
    {
        var configDirectory = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configDirectory))
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        yield return Path.Combine(configDirectory, "opencode", ConfigFileName);
        yield return Path.Combine(configDirectory, "opencode", JsoncConfigFileName);

        var roamingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingDirectory))
        {
            yield return Path.Combine(roamingDirectory, "opencode", ConfigFileName);
            yield return Path.Combine(roamingDirectory, "opencode", JsoncConfigFileName);
        }
    }
}

internal sealed class LatrixApiClient
{
    private static readonly HttpClient SharedClient = new()
    {
        BaseAddress = new Uri("https://inference.llai.io/"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly HttpClient client;

    public LatrixApiClient(HttpClient client = null)
    {
        this.client = client ?? SharedClient;
    }

    public async Task ValidateAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        _ = await GetJsonAsync("api/me", apiKey, cancellationToken);
    }

    public async Task<LatrixIdentity> ReadIdentityAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync("api/me", apiKey, cancellationToken);
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object ||
            !user.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw new InvalidDataException("Latrix identity response did not contain a user id.");
        }
        var name = user.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
            ? nameValue.GetString() : null;
        var role = user.TryGetProperty("role", out var roleValue) && roleValue.ValueKind == JsonValueKind.String
            ? roleValue.GetString() : null;
        return new LatrixIdentity(id.GetString(), string.IsNullOrWhiteSpace(name) ? "You" : name, role ?? "");
    }

    public async Task<UsageDisplay> ReadUsageAsync(string apiKey, TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync("api/window", apiKey, cancellationToken);
        return LatrixUsageParser.Project(response, timeZone);
    }

    public async Task<IReadOnlyList<TelemetryPerson>> ReadTelemetryAsync(string apiKey, int days,
        CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync($"api/telemetry?days={Math.Clamp(days, 1, 30)}", apiKey, cancellationToken);
        return LatrixTelemetryParser.Project(response);
    }

    public async Task<IReadOnlyList<LatrixActiveUser>> ReadActiveAsync(string apiKey,
        CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync("api/active", apiKey, cancellationToken, true);
        return LatrixActiveParser.Project(response);
    }

    private async Task<JsonElement> GetJsonAsync(string path, string apiKey, CancellationToken cancellationToken,
        bool bypassCache = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("A Latrix API key is required.");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (bypassCache)
        {
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("The Latrix API key was rejected.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Latrix API request failed ({(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        return document.RootElement.Clone();
    }
}

internal static class LatrixActiveParser
{
    public static IReadOnlyList<LatrixActiveUser> Project(JsonElement result)
    {
        if (!result.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Latrix active response did not contain active users.");

        var projected = new List<LatrixActiveUser>();
        foreach (var user in active.EnumerateArray())
        {
            var provider = GetString(user, "provider", "");
            projected.Add(new LatrixActiveUser(
                GetString(user, "userId", ""), GetString(user, "name", "Unknown"),
                GetString(user, "model", ""), provider == "self_hosted" ? "self-hosted" : "codex",
                GetString(user, "effort", ""), GetLong(user, "elapsedMs")));
        }
        return projected;
    }

    private static string GetString(JsonElement value, string name, string fallback) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? fallback : fallback;

    private static long GetLong(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetInt64(out var number) ? number : 0;
}

internal static class LatrixTelemetryParser
{
    public static IReadOnlyList<TelemetryPerson> Project(JsonElement result)
    {
        if (!result.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Latrix telemetry response did not contain users.");

        var projected = new List<TelemetryPerson>();
        foreach (var user in users.EnumerateArray())
        {
            var breakdown = new List<TelemetryBreakdown>();
            if (user.TryGetProperty("breakdown", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var efforts = new List<string>();
                    var effortItems = new List<TelemetryEffort>();
                    if (item.TryGetProperty("efforts", out var effortEntries) && effortEntries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var effort in effortEntries.EnumerateArray())
                        {
                            // The dashboard represents the model default as a null effort.
                            var name = GetString(effort, "effort", "default");
                            var requests = GetInt(effort, "requests");
                            efforts.Add($"{name}: {requests}");
                            effortItems.Add(new TelemetryEffort(name, requests));
                        }
                    }
                    breakdown.Add(new TelemetryBreakdown(
                        GetString(item, "provider", ""), GetString(item, "model", "Unknown"),
                        GetLong(item, "totalTokens"), GetInt(item, "requests"), string.Join(", ", efforts),
                        effortItems));
                }
            }
            var lastActiveRaw = GetString(user, "lastActive", "");
            projected.Add(new TelemetryPerson(
                GetString(user, "userId", GetString(user, "name", "Unknown")),
                GetString(user, "name", "Unknown"), GetString(user, "role", ""),
                GetBool(user, "online"), GetInt(user, "requests"), GetLong(user, "inputTokens"),
                GetLong(user, "cachedTokens"), GetLong(user, "outputTokens"), GetLong(user, "reasoningTokens"),
                GetLong(user, "totalTokens"), GetInt(user, "models"), GetInt(user, "errors"),
                GetDouble(user, "avgLatencyMs"), FormatLastActive(lastActiveRaw), ParseLastActive(lastActiveRaw),
                breakdown));
        }
        return projected;
    }

    private static string GetString(JsonElement value, string name, string fallback) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? fallback : fallback;

    private static int GetInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetInt32(out var number) ? number : 0;

    private static long GetLong(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetInt64(out var number) ? number : 0;

    private static double GetDouble(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetDouble(out var number) ? number : 0;

    private static bool GetBool(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True && item.GetBoolean();

    private static DateTimeOffset? ParseLastActive(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var active) ? active : null;

    private static string FormatLastActive(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var active)) return value;
        var age = DateTimeOffset.UtcNow - active;
        if (age.TotalSeconds < 60) return "now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}

internal static class LatrixUsageParser
{
    public static UsageDisplay Project(JsonElement result, TimeZoneInfo timeZone)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Latrix usage response was not an object.");
        var bucket = GetNumber(result, "bucketPercent");
        var capacity = GetNumber(result, "capacityPercent");
        var estimatedBucket = GetNumber(result, "bucketPercentEstimated");
        var weeklyUsed = GetNumber(result, "weeklyUsedPercent");
        var primary = bucket.HasValue && capacity is > 0
            ? FormatPercent(Math.Min(
                bucket.Value / capacity.Value * 100,
                estimatedBucket.GetValueOrDefault(bucket.Value) / capacity.Value * 100))
            : "--";
        var weekly = weeklyUsed.HasValue ? FormatPercent(100 - weeklyUsed.Value) : "--";
        return new UsageDisplay(
            primary,
            FormatReset(result, "slotEndsAt", false, timeZone),
            weekly,
            FormatReset(result, "weeklyResetsAt", true, timeZone)
        );
    }

    private static double? GetNumber(JsonElement result, string name) =>
        result.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;

    private static string FormatPercent(double value) =>
        Math.Round(Math.Clamp(value, 0, 100), 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string FormatReset(JsonElement result, string name, bool includeDate, TimeZoneInfo timeZone)
    {
        if (!result.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var reset))
        {
            return "";
        }
        var local = TimeZoneInfo.ConvertTime(reset, timeZone);
        return includeDate
            ? local.ToString("dd MMM HH:mm", CultureInfo.GetCultureInfo("tr-TR"))
            : local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
