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
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 1120;
        MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowActivated = true;
        Background = Theme.BgBrush;
        WindowStyle = WindowStyle.SingleBorderWindow;
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
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
