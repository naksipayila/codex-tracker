using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CodexUsageTray;

internal sealed class TelemetryGraphWindow : Window
{
    private readonly DispatcherTimer refreshTimer;

    public TelemetryGraphWindow(string title, IReadOnlyList<TelemetrySnapshot> snapshots,
        Func<TelemetrySnapshot, double> selector)
        : this(title, "Live", () => snapshots, selector)
    {
    }

    public TelemetryGraphWindow(string title, string period, Func<IReadOnlyList<TelemetrySnapshot>> snapshots,
        Func<TelemetrySnapshot, double> selector)
    {
        Title = $"Codex Tracker - {title}";
        Width = 820;
        Height = 500;
        MinWidth = 620;
        MinHeight = 380;
        Background = new SolidColorBrush(Theme.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Foreground = new SolidColorBrush(Theme.TextPrimary),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        var subtitle = new TextBlock
        {
            Text = $"{period}  /  live snapshots collected while Codex Tracker is running",
            Foreground = new SolidColorBrush(Theme.TextSecondary),
            FontSize = 11,
        };
        Grid.SetRow(subtitle, 2);
        root.Children.Add(subtitle);
        var chart = new TelemetryGraphCanvas(snapshots, selector);
        Grid.SetRow(chart, 4);
        root.Children.Add(chart);
        Content = root;

        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        refreshTimer.Tick += (_, _) => chart.Refresh();
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
    }
}

internal sealed class TelemetryGraphCanvas : FrameworkElement
{
    private readonly Func<IReadOnlyList<TelemetrySnapshot>> snapshots;
    private readonly Func<TelemetrySnapshot, double> selector;

    public TelemetryGraphCanvas(IReadOnlyList<TelemetrySnapshot> snapshots, Func<TelemetrySnapshot, double> selector)
        : this(() => snapshots, selector)
    {
    }

    public TelemetryGraphCanvas(Func<IReadOnlyList<TelemetrySnapshot>> snapshots, Func<TelemetrySnapshot, double> selector)
    {
        this.snapshots = snapshots;
        this.selector = selector;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var area = new Rect(64, 18, Math.Max(1, ActualWidth - 84), Math.Max(1, ActualHeight - 62));
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Theme.Surface),
            new Pen(new SolidColorBrush(Theme.Border), 1), area, 12, 12);
        var currentSnapshots = snapshots();
        var values = currentSnapshots.Select(selector).ToArray();
        if (values.Length == 0)
        {
            DrawText(drawingContext, "Waiting for telemetry samples...", new Point(area.Left + 20, area.Top + 22),
                Theme.TextSecondary, 12);
            return;
        }

        var min = values.Min();
        var max = values.Max();
        var range = Math.Max(1, max - min);
        var plot = new Rect(area.Left + 54, area.Top + 30, Math.Max(1, area.Width - 72), Math.Max(1, area.Height - 72));
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + i * plot.Height / 4;
            drawingContext.DrawLine(new Pen(new SolidColorBrush(Theme.Border), 1),
                new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(drawingContext, FormatValue(max - i * range / 4), new Point(area.Left + 12, y - 7), Theme.TextMuted, 10);
        }
        var points = values.Select((value, index) => new Point(
            values.Length == 1 ? plot.Left : plot.Left + index * plot.Width / (values.Length - 1),
            plot.Bottom - (value - min) / range * plot.Height)).ToArray();
        var fill = new StreamGeometry();
        using (var context = fill.Open())
        {
            context.BeginFigure(new Point(points[0].X, plot.Bottom), true, true);
            context.PolyLineTo(points, true, true);
            context.LineTo(new Point(points[^1].X, plot.Bottom), true, true);
        }
        drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(35, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B)), null, fill);
        for (var i = 1; i < points.Length; i++)
            drawingContext.DrawLine(new Pen(new SolidColorBrush(Theme.Accent), 2.5), points[i - 1], points[i]);
        drawingContext.DrawEllipse(new SolidColorBrush(Theme.Accent), new Pen(new SolidColorBrush(Theme.Surface), 2), points[^1], 5, 5);
        DrawText(drawingContext, FormatValue(values[^1]), new Point(area.Left + 16, area.Top + 14), Theme.TextPrimary, 22);
        DrawText(drawingContext, "CURRENT VALUE", new Point(area.Left + 16, area.Bottom - 24), Theme.TextMuted, 9);
        DrawText(drawingContext, FormatTime(currentSnapshots[0].CapturedAtUtc), new Point(plot.Left, area.Bottom - 24), Theme.TextSecondary, 10);
        var lastTime = FormatTime(currentSnapshots[^1].CapturedAtUtc);
        DrawText(drawingContext, lastTime, new Point(plot.Right - 70, area.Bottom - 24), Theme.TextSecondary, 10);
    }

    private static string FormatValue(double value) => value >= 1000
        ? value.ToString("N0", CultureInfo.CurrentCulture)
        : value.ToString("0.#", CultureInfo.CurrentCulture);

    private static string FormatTime(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    private static void DrawText(DrawingContext context, string text, Point origin, Color color, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, new SolidColorBrush(color), 1);
        context.DrawText(formatted, origin);
    }
}
