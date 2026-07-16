namespace CodexUsageTray;

internal sealed record CommandCapture(int ExitCode, string Output, string Error);

internal sealed record UsageDisplay(string FiveHour, string FiveHourReset, string Weekly, string WeeklyReset)
{
    public static readonly UsageDisplay Empty = new("--", "", "--", "");
}
