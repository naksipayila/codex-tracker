using System;
using System.Windows;
using System.Windows.Media;

namespace CodexUsageTray;

internal sealed class TelemetryWindow : Window
{
    private readonly TelemetryPanel telemetry;

    public TelemetryWindow(LatrixApiClient latrix, string apiKey)
    {
        Title = "Codex Tracker Telemetry";
        Width = 1220;
        Height = 680;
        MinWidth = 900;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x19, 0x26));
        WindowStyle = WindowStyle.SingleBorderWindow;
        telemetry = new TelemetryPanel(latrix, apiKey) { Margin = new Thickness(22) };
        Content = telemetry;
        Closed += (_, _) => telemetry.Dispose();
    }
}
