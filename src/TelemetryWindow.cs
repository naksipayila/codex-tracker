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
        Width = 1280;
        Height = 760;
        MinWidth = 1000;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowActivated = true;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x0b, 0x0f));
        WindowStyle = WindowStyle.SingleBorderWindow;
        telemetry = new TelemetryPanel(latrix, apiKey);
        Content = telemetry;
        Closed += (_, _) => telemetry.Dispose();
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        Topmost = false;
        Focus();
    }
}
