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

    public async Task<UsageDisplay> ReadUsageAsync(string apiKey, TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync("api/window", apiKey, cancellationToken);
        return LatrixUsageParser.Project(response, timeZone);
    }

    private async Task<JsonElement> GetJsonAsync(string path, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("A Latrix API key is required.");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
