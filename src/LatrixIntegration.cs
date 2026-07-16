using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsageTray;

internal static class LatrixApiKeyStore
{
    private const string FileName = "latrix-api-key.dat";

    public static bool IsConfigured() => Load() != null;

    public static string Load()
    {
        try
        {
            var encrypted = File.ReadAllBytes(GetPath());
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var key = Encoding.UTF8.GetString(plain).Trim();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Enter a Latrix API key.");
        var plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        byte[] encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var path = GetPath();
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, encrypted);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
        finally
        {
            Array.Clear(plain, 0, plain.Length);
            if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
        }
    }

    public static void Delete()
    {
        try { File.Delete(GetPath()); } catch { }
    }

    private static string GetPath() => Path.Combine(NativeSettings.GetUserDataDirectory(), FileName);
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
        var weeklyUsed = GetNumber(result, "weeklyUsedPercent");
        var primary = bucket.HasValue && capacity is > 0
            ? FormatPercent(bucket.Value / capacity.Value * 100)
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
        Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

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
