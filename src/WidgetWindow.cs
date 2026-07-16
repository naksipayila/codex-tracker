using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;

namespace CodexUsageTray;

internal sealed class WidgetWindow : Window
{
    public const double PreferredWidth = 340;
    private static readonly Color LabelColor = Color.FromRgb(0x8e, 0xa9, 0xc7);
    private static readonly Color ValueColor = Color.FromRgb(0x78, 0xe0, 0xb1);
    private static readonly Color ResetColor = Color.FromRgb(0xae, 0xd0, 0xf7);

    private readonly TextBlock fiveHourValue;
    private readonly TextBlock fiveHourReset;
    private readonly TextBlock weeklyValue;
    private readonly TextBlock weeklyReset;
    private readonly TextBlock fiveHourLabel;
    private readonly TextBlock weeklyLabel;
    private readonly StackPanel fiveHourMetric;
    private readonly StackPanel weeklyMetric;
    private readonly Border divider;
    private readonly Grid usageGrid;
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

        usageGrid = new Grid { Margin = new Thickness(8, 0, 8, 0), Cursor = Cursors.SizeAll };
        usageGrid.ColumnDefinitions.Add(new ColumnDefinition());
        usageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        usageGrid.ColumnDefinitions.Add(new ColumnDefinition());

        fiveHourValue = CreateText("--", ValueColor, 39, TextAlignment.Right);
        fiveHourReset = CreateText("", ResetColor, 44, TextAlignment.Right);
        fiveHourLabel = CreateLabel("5H");
        fiveHourMetric = CreateMetric(fiveHourLabel, fiveHourValue, fiveHourReset, false);
        Grid.SetColumn(fiveHourMetric, 0);
        usageGrid.Children.Add(fiveHourMetric);

        divider = new Border
        {
            Width = 1,
            Height = 22,
            Margin = new Thickness(8, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(163, 112, 150, 188)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(divider, 1);
        usageGrid.Children.Add(divider);

        weeklyValue = CreateText("--", ValueColor, 39, TextAlignment.Right);
        weeklyReset = CreateText("", ResetColor, 78, TextAlignment.Right);
        weeklyLabel = CreateLabel("W");
        weeklyMetric = CreateMetric(weeklyLabel, weeklyValue, weeklyReset, true);
        Grid.SetColumn(weeklyMetric, 2);
        usageGrid.Children.Add(weeklyMetric);
        root.Children.Add(usageGrid);

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
        var showResetTimes = width >= 280;

        fiveHourMetric.Visibility = showFiveHourMetric ? Visibility.Visible : Visibility.Collapsed;
        divider.Visibility = showBothMetrics ? Visibility.Visible : Visibility.Collapsed;
        weeklyMetric.Visibility = showWeeklyMetric ? Visibility.Visible : Visibility.Collapsed;
        fiveHourLabel.Visibility = showFiveHourMetric && showLabels ? Visibility.Visible : Visibility.Collapsed;
        weeklyLabel.Visibility = showWeeklyMetric && showLabels ? Visibility.Visible : Visibility.Collapsed;
        fiveHourReset.Visibility = showFiveHourMetric && showResetTimes ? Visibility.Visible : Visibility.Collapsed;
        weeklyReset.Visibility = showWeeklyMetric && showResetTimes ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(fiveHourMetric, 0);
        Grid.SetColumnSpan(weeklyMetric, showBothMetrics ? 1 : 3);
        Grid.SetColumn(weeklyMetric, showBothMetrics ? 2 : 0);
        Grid.SetColumnSpan(fiveHourMetric, showBothMetrics ? 1 : 3);
        fiveHourMetric.HorizontalAlignment = showBothMetrics ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        weeklyMetric.HorizontalAlignment = showBothMetrics ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        usageGrid.Margin = new Thickness(width < 120 ? 2 : 8, 0, width < 120 ? 2 : 8, 0);
        divider.Margin = new Thickness(showLabels ? 8 : 4, 0, showLabels ? 8 : 4, 0);
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
            Foreground = new SolidColorBrush(LabelColor),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = CreateShadow(),
        };
    }

    private static StackPanel CreateMetric(TextBlock label, TextBlock value, TextBlock reset, bool weekly)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = weekly ? HorizontalAlignment.Left : HorizontalAlignment.Right,
        };
        panel.Children.Add(label);
        value.Margin = new Thickness(2, 0, 0, 0);
        panel.Children.Add(value);
        reset.Margin = new Thickness(weekly ? 6 : 0, 0, 0, 0);
        panel.Children.Add(reset);
        return panel;
    }

    private static TextBlock CreateText(string text, Color color, double minWidth, TextAlignment alignment)
    {
        return new TextBlock
        {
            Text = text,
            MinWidth = minWidth,
            Foreground = new SolidColorBrush(color),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = CreateShadow(),
        };
    }

    private static System.Windows.Media.Effects.DropShadowEffect CreateShadow()
    {
        return new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 2,
            ShadowDepth = 1,
            Opacity = 0.7,
            Color = Colors.Black,
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
