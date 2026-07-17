using System.Collections.Generic;

namespace CodexUsageTray;

internal sealed record CommandCapture(int ExitCode, string Output, string Error);

internal sealed record UsageDisplay(string FiveHour, string FiveHourReset, string Weekly, string WeeklyReset)
{
    public static readonly UsageDisplay Empty = new("--", "", "--", "");
}

internal sealed record TelemetryBreakdown(string Model, long TotalTokens, int Requests, string Efforts);

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
    IReadOnlyList<TelemetryBreakdown> Breakdown);
