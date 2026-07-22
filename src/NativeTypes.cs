using System;
using System.Collections.Generic;

namespace CodexUsageTray;

internal sealed record CommandCapture(int ExitCode, string Output, string Error);

internal sealed record UsageDisplay(string FiveHour, string FiveHourReset, string Weekly, string WeeklyReset)
{
    public static readonly UsageDisplay Empty = new("--", "", "--", "");
}

internal sealed record LatrixIdentity(string UserId, string Name, string Role);

internal sealed record TelemetryEffort(string Effort, int Requests);

internal sealed record TelemetryBreakdown(string Provider, string Model, long TotalTokens, int Requests, string Efforts,
    IReadOnlyList<TelemetryEffort> EffortItems = null);

internal sealed record OnlineActiveModel(string Provider, string Model, long DeltaTokens, int DeltaRequests,
    string Effort = null);

internal sealed record OnlineActivitySnapshot(OnlineActiveModel Model, DateTime DetectedAtUtc);

internal sealed record TelemetrySnapshot(DateTimeOffset CapturedAtUtc, long TotalTokens, int Requests, int ActiveUsers);

internal sealed record LatrixActiveUser(string UserId, string Name, string Model, string Provider, string Effort,
    long ElapsedMs);

internal sealed record TelemetryPerson(
    string UserId,
    string Name,
    string Role,
    bool Online,
    int Requests,
    long InputTokens,
    long CachedTokens,
    long OutputTokens,
    long ReasoningTokens,
    long TotalTokens,
    int Models,
    int Errors,
    double AverageLatencyMs,
    string LastActive,
    DateTimeOffset? LastActiveUtc,
    IReadOnlyList<TelemetryBreakdown> Breakdown);
