using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;

namespace CodexUsageTray;

internal sealed class WidgetWindow : Window
{
    // Keep enough room for both weekly reset text and the online count.
    public const double PreferredWidth = 310;
    private static readonly System.Windows.Media.Effects.DropShadowEffect WidgetTextOutline = new()
    {
        BlurRadius = 2,
        ShadowDepth = 0,
        Opacity = 1.0,
        Color = Colors.Black,
    };

    private readonly TextBlock fiveHourValue;
    private readonly TextBlock fiveHourReset;
    private readonly TextBlock weeklyValue;
    private readonly TextBlock weeklyReset;
    private readonly TextBlock fiveHourLabel;
    private readonly TextBlock weeklyLabel;
    private readonly Grid fiveHourMetric;
    private readonly Grid weeklyMetric;
    private readonly Border divider;
    private readonly Grid usageGrid;
    private readonly TextBlock onlineCount;
    private bool showFiveHour = true;
    private bool showWeekly = true;
    private double availableWidth = PreferredWidth;
    private bool dragging;
    private double dragScreenX;
    private double dragWindowLeft;

    public event Action<double> Dragged;
    public event Action DragCompleted;

    public WidgetWindow()
    {
        Width = PreferredWidth;
        Height = 34;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        usageGrid = new Grid { Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.SizeAll };
        usageGrid.ColumnDefinitions.Add(new ColumnDefinition());
        usageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        usageGrid.ColumnDefinitions.Add(new ColumnDefinition());

        fiveHourValue = CreateText("--", Theme.TextSecondary, 0, TextAlignment.Left);
        fiveHourReset = CreateText("", Theme.TextMuted, 0, TextAlignment.Left);
        fiveHourLabel = CreateLabel("5H");
        fiveHourMetric = CreateMetric(fiveHourLabel, fiveHourValue, fiveHourReset);
        Grid.SetColumn(fiveHourMetric, 0);
        usageGrid.Children.Add(fiveHourMetric);

        divider = new Border
        {
            Width = 1,
            Height = 22,
            Margin = new Thickness(8, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(153, Theme.Border.R, Theme.Border.G, Theme.Border.B)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(divider, 1);
        usageGrid.Children.Add(divider);

        weeklyValue = CreateText("--", Theme.TextSecondary, 0, TextAlignment.Left);
        weeklyReset = CreateText("", Theme.TextMuted, 0, TextAlignment.Left);
        weeklyLabel = CreateLabel("W");
        weeklyMetric = CreateMetric(weeklyLabel, weeklyValue, weeklyReset);
        Grid.SetColumn(weeklyMetric, 2);
        usageGrid.Children.Add(weeklyMetric);
        Grid.SetColumn(usageGrid, 0);
        root.Children.Add(usageGrid);

        onlineCount = new TextBlock
        {
            Text = "--",
            Foreground = Theme.SuccessBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeBody,
            FontWeight = Theme.FontWeightBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 0, 14, 0),
            Padding = new Thickness(10, 6, 10, 6),
            ToolTip = CreateOnlineTooltip(Array.Empty<string>(), false),
            Effect = WidgetTextOutline,
        };
        Grid.SetColumn(onlineCount, 1);
        root.Children.Add(onlineCount);

        Content = root;

        PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
        LostMouseCapture += (_, _) => EndDrag();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle,
                new IntPtr(style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate));
            if (HwndSource.FromHwnd(handle) is HwndSource source) source.AddHook(WindowMessageHook);
        };
    }

    public void UpdateUsage(UsageDisplay usage)
    {
        fiveHourValue.Text = usage.FiveHour;
        fiveHourReset.Text = usage.FiveHourReset;
        weeklyValue.Text = usage.Weekly;
        weeklyReset.Text = usage.WeeklyReset;
    }

    public void UpdateOnlineUsers(IReadOnlyList<LatrixActiveUser> users)
    {
        var names = users
            .Select(user => user.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        onlineCount.Text = names.Length.ToString();
        onlineCount.ToolTip = CreateOnlineTooltip(names, true);
    }

    public void ClearOnlineUsers()
    {
        onlineCount.Text = "--";
        onlineCount.ToolTip = CreateOnlineTooltip(Array.Empty<string>(), false);
    }

    public void SetUsageLabels(string primaryLabel, string weeklyUsageLabel)
    {
        fiveHourLabel.Text = primaryLabel;
        weeklyLabel.Text = weeklyUsageLabel;
    }

    public void SetAvailableWidth(double availableWidth)
    {
        this.availableWidth = Math.Clamp(availableWidth, 1, PreferredWidth);
        Width = this.availableWidth;
        ApplyMetricLayout();
    }

    public void SetMetricVisibility(bool showFiveHour, bool showWeekly)
    {
        this.showFiveHour = showFiveHour;
        this.showWeekly = showWeekly;
        ApplyMetricLayout();
    }

    private void ApplyMetricLayout()
    {
        var width = availableWidth;
        var showFiveHourMetric = showFiveHour && (!showWeekly || width >= 100) && width >= 50;
        var showWeeklyMetric = showWeekly && width >= 50;
        var showBothMetrics = showFiveHourMetric && showWeeklyMetric;
        var showLabels = width >= 180;
        var showResetTimes = width >= 250;
        onlineCount.Visibility = width >= 80 ? Visibility.Visible : Visibility.Collapsed;

        fiveHourMetric.Visibility = showFiveHourMetric ? Visibility.Visible : Visibility.Collapsed;
        divider.Visibility = showBothMetrics ? Visibility.Visible : Visibility.Collapsed;
        weeklyMetric.Visibility = showWeeklyMetric ? Visibility.Visible : Visibility.Collapsed;
        fiveHourLabel.Visibility = showFiveHourMetric && showLabels ? Visibility.Visible : Visibility.Collapsed;
        weeklyLabel.Visibility = showWeeklyMetric && showLabels ? Visibility.Visible : Visibility.Collapsed;
        fiveHourReset.Visibility = showFiveHourMetric && showResetTimes ? Visibility.Visible : Visibility.Collapsed;
        weeklyReset.Visibility = showWeeklyMetric && showResetTimes ? Visibility.Visible : Visibility.Collapsed;
        SetMetricColumns(fiveHourMetric, showLabels, showResetTimes);
        SetMetricColumns(weeklyMetric, showLabels, showResetTimes);
        usageGrid.ColumnDefinitions[1].Width = new GridLength(
            showBothMetrics ? (showLabels ? 18 : 12) : 0);
        var leftAlignContent = showBothMetrics && showLabels && showResetTimes && width >= 265;
        usageGrid.ColumnDefinitions[0].Width = leftAlignContent ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        usageGrid.ColumnDefinitions[2].Width = leftAlignContent ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(fiveHourMetric, 0);
        Grid.SetColumnSpan(weeklyMetric, showBothMetrics ? 1 : 3);
        Grid.SetColumn(weeklyMetric, showBothMetrics ? 2 : 0);
        Grid.SetColumnSpan(fiveHourMetric, showBothMetrics ? 1 : 3);
        fiveHourMetric.HorizontalAlignment = showBothMetrics && leftAlignContent
            ? HorizontalAlignment.Left
            : showBothMetrics ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        weeklyMetric.HorizontalAlignment = showBothMetrics ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        usageGrid.Margin = new Thickness(8, 0, 0, 0);
        divider.Margin = new Thickness(0);
    }

    private static ToolTip CreateOnlineTooltip(IReadOnlyList<string> names, bool available)
    {
        var lines = available && names.Count > 0
            ? new[] { $"{names.Count} kişi online" }.Concat(names).ToArray()
            : new[] { available ? "Şu anda online kişi yok" : "Online bilgisi alınamadı" };
        return new ToolTip
        {
            Placement = PlacementMode.Top,
            Background = Theme.BgBrush,
            BorderBrush = Theme.BorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            HasDropShadow = true,
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine, lines),
                FontFamily = Theme.FontFamilyValue,
                FontSize = Theme.FontSizeSmall,
                Foreground = Theme.TextPrimaryBrush,
            },
        };
    }

    public void Reveal(bool animate)
    {
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            return;
        }
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static TextBlock CreateLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = Theme.TextMutedBrush,
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeBody,
            FontWeight = Theme.FontWeightBold,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = WidgetTextOutline,
        };
    }

    private static Grid CreateMetric(TextBlock label, TextBlock value, TextBlock reset)
    {
        var panel = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 2);
        Grid.SetColumn(reset, 4);
        panel.Children.Add(label);
        panel.Children.Add(value);
        panel.Children.Add(reset);
        return panel;
    }

    private static void SetMetricColumns(Grid metric, bool showLabels, bool showResetTimes)
    {
        metric.ColumnDefinitions[0].Width = showLabels ? GridLength.Auto : new GridLength(0);
        metric.ColumnDefinitions[1].Width = showLabels ? new GridLength(4) : new GridLength(0);
        metric.ColumnDefinitions[2].Width = GridLength.Auto;
        metric.ColumnDefinitions[3].Width = showResetTimes ? new GridLength(4) : new GridLength(0);
        metric.ColumnDefinitions[4].Width = showResetTimes ? GridLength.Auto : new GridLength(0);
        metric.Width = double.NaN;
    }

    private static TextBlock CreateText(string text, Color color, double minWidth, TextAlignment alignment)
    {
        return new TextBlock
        {
            Text = text,
            MinWidth = minWidth,
            Foreground = new SolidColorBrush(color),
            FontFamily = Theme.FontFamilyValue,
            FontSize = Theme.FontSizeBody,
            FontWeight = Theme.FontWeightBold,
            TextAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Effect = WidgetTextOutline,
        };
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        var point = PointToScreen(eventArgs.GetPosition(this));
        dragging = true;
        dragScreenX = point.X;
        dragWindowLeft = Left;
        CaptureMouse();
        eventArgs.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs eventArgs)
    {
        if (!dragging || eventArgs.LeftButton != MouseButtonState.Pressed) return;
        var point = PointToScreen(eventArgs.GetPosition(this));
        Dragged?.Invoke(dragWindowLeft + (point.X - dragScreenX) / NativeMethods.GetScaleForWindow(this));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!dragging) return;
        EndDrag();
        eventArgs.Handled = true;
    }

    private void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        DragCompleted?.Invoke();
    }

    private static IntPtr WindowMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WmMouseActivate) return IntPtr.Zero;
        handled = true;
        return new IntPtr(NativeMethods.MaNoActivate);
    }
}
