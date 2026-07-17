using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUsageTray;

internal sealed class TelemetryPanel : UserControl, IDisposable
{
    private static readonly double[] ColumnWidths = { 205, 78, 82, 82, 92, 86, 62, 62, 78, 96 };

    private static Color BgCanvas => Theme.Background;
    private static Color BgSurface => Theme.Surface;
    private static Color BgElevated => Theme.Elevated;
    private static Color BgHeader => Theme.Header;
    private static Color BgBorder => Theme.Border;
    private static Color TextSecondary => Theme.TextSecondary;
    private static Color TextMuted => Theme.TextMuted;
    private static Color TextPrimary => Theme.TextPrimary;
    private static Color Accent => Theme.Accent;
    private static Color Success => Theme.Success;
    private static Color Error => Theme.Error;
    private static Color Warning => Theme.Warning;
    private Color rowEven => BgSurface;
    private Color rowOdd => Color.FromRgb(0x18, 0x18, 0x18);
    private static Color RowHover => Color.FromRgb(0x2e, 0x2e, 0x2e);
    private static Color SelectedBg => Color.FromRgb(0x48, 0x48, 0x48);
    private static Color ScrollThumb => Theme.ScrollThumb;
    private static Color ScrollHover => Accent;

    private readonly LatrixApiClient latrix;
    private readonly string apiKey;
    private readonly CancellationTokenSource lifetime = new();
    private readonly StackPanel rangeFilters;
    private readonly StackPanel rows;
    private readonly StackPanel passiveRows;
    private readonly TextBlock status;
    private readonly Button refresh;
    private readonly TextBlock totalTokensValue;
    private readonly TextBlock requestsValue;
    private readonly TextBlock activeValue;
    private readonly TextBlock errorsValue;
    private readonly TextBlock latencyValue;
    private readonly Border errorsAccent;
    private readonly Border latencyAccent;
    private readonly Grid contentArea;
    private readonly Border table;
    private IReadOnlyList<TelemetryPerson> currentUsers = Array.Empty<TelemetryPerson>();
    private bool passiveExpanded;
    private int selectedRangeDays = 7;
    private bool loading;

    public TelemetryPanel(LatrixApiClient latrix, string apiKey)
    {
        this.latrix = latrix;
        this.apiKey = apiKey;
        Background = new SolidColorBrush(BgCanvas);
        MinWidth = 900;
        MinHeight = 500;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var root = new Grid { Margin = new Thickness(28, 22, 28, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingCopy = new StackPanel();
        headingCopy.Children.Add(new TextBlock
        {
            Text = "LATRIX TELEMETRY",
            Foreground = new SolidColorBrush(Accent),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        });
        headingCopy.Children.Add(new TextBlock
        {
            Text = "Team activity",
            Foreground = new SolidColorBrush(TextPrimary),
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 3),
        });
        headingCopy.Children.Add(new TextBlock
        {
            Text = "A clear view of usage, performance and model activity.",
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 12,
        });
        heading.Children.Add(headingCopy);

        status = new TextBlock
        {
            Text = "Ready to sync telemetry",
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 18, 0),
            MaxWidth = 360,
        };
        Grid.SetColumn(status, 1);
        heading.Children.Add(status);

        var liveBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(34, Accent.R, Accent.G, Accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, Accent.R, Accent.G, Accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 7, 12, 7),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Border
                    {
                        Width = 7,
                        Height = 7,
                        Background = new SolidColorBrush(Success),
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, 7, 0),
                    },
                    new TextBlock
                    {
                        Text = "LIVE SYNC",
                        Foreground = new SolidColorBrush(Accent),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                    },
                },
            },
        };
        Grid.SetColumn(liveBadge, 2);
        heading.Children.Add(liveBadge);
        root.Children.Add(heading);

        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toolbarInfo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var rangeLabel = new TextBlock
        {
            Text = "ACTIVITY RANGE",
            Foreground = new SolidColorBrush(TextMuted),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        toolbarInfo.Children.Add(rangeLabel);
        toolbar.Children.Add(toolbarInfo);
        var rangeControls = new StackPanel { Orientation = Orientation.Horizontal };
        rangeFilters = CreateRangeSelector();
        rangeControls.Children.Add(rangeFilters);
        refresh = CreateButton("Refresh", RefreshAsync);
        rangeControls.Children.Add(refresh);
        Grid.SetColumn(rangeControls, 1);
        toolbar.Children.Add(rangeControls);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var summary = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        for (var i = 0; i < 5; i++)
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        totalTokensValue = CreateMetricValue("--");
        requestsValue = CreateMetricValue("--");
        activeValue = CreateMetricValue("--");
        errorsValue = CreateMetricValue("--");
        latencyValue = CreateMetricValue("--");
        summary.Children.Add(CreateSummaryCard(0, "TOTAL TOKENS", totalTokensValue, "Across this period", Accent, out _));
        summary.Children.Add(CreateSummaryCard(1, "REQUESTS", requestsValue, "All team members", Accent, out _));
        summary.Children.Add(CreateSummaryCard(2, "ACTIVE NOW", activeValue, "Currently online", Success, out _));
        summary.Children.Add(CreateSummaryCard(3, "ERRORS", errorsValue, "Needs attention", Error, out errorsAccent));
        summary.Children.Add(CreateSummaryCard(4, "AVG LATENCY", latencyValue, "Across active users", Warning, out latencyAccent));
        Grid.SetRow(summary, 2);
        root.Children.Add(summary);

        table = new Border
        {
            Background = new SolidColorBrush(BgSurface),
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
        };
        var tableRoot = new Grid();
        tableRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tableRoot.RowDefinitions.Add(new RowDefinition());
        var header = CreateHeader();
        Grid.SetRow(header, 0);
        tableRoot.Children.Add(header);
        rows = new StackPanel();
        passiveRows = new StackPanel { Visibility = Visibility.Collapsed };
        var scroll = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 0, 2),
        };
        ConfigureScrollViewer(scroll);
        Grid.SetRow(scroll, 1);
        tableRoot.Children.Add(scroll);
        table.Child = tableRoot;

        contentArea = new Grid();
        contentArea.ColumnDefinitions.Add(new ColumnDefinition());
        contentArea.RowDefinitions.Add(new RowDefinition());
        contentArea.Children.Add(table);
        Grid.SetRow(contentArea, 3);
        root.Children.Add(contentArea);
        Content = root;

        Loaded += (_, _) =>
        {
            _ = RefreshAsync();
            _ = RefreshLoopAsync();
        };
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(lifetime.Token)) await RefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task RefreshAsync()
    {
        if (loading || lifetime.IsCancellationRequested) return;
        loading = true;
        refresh.IsEnabled = false;
        status.Foreground = new SolidColorBrush(TextSecondary);
        status.Text = "Syncing latest telemetry...";
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No Latrix API key was found.");
            var users = await latrix.ReadTelemetryAsync(apiKey, selectedRangeDays, lifetime.Token);
            Render(users);
            status.Foreground = new SolidColorBrush(TextSecondary);
            status.Text = $"{users.Count} people  /  updated {DateTime.Now:HH:mm:ss}  /  auto-refresh every 20s";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            status.Foreground = new SolidColorBrush(Error);
            status.Text = error.Message;
            Render(Array.Empty<TelemetryPerson>());
        }
        finally
        {
            loading = false;
            refresh.IsEnabled = true;
        }
    }

    private void Render(IReadOnlyList<TelemetryPerson> users)
    {
        var ordered = users.OrderByDescending(user => user.TotalTokens).ToArray();
        currentUsers = ordered;
        var totalTokens = ordered.Sum(user => user.TotalTokens);
        var totalRequests = ordered.Sum(user => user.Requests);
        var totalErrors = ordered.Sum(user => user.Errors);
        var activeUsers = ordered.Count(user => user.Online);
        var latencyUsers = ordered.Where(user => user.AverageLatencyMs > 0).ToArray();
        totalTokensValue.Text = FormatTokens(totalTokens);
        requestsValue.Text = totalRequests.ToString("N0", CultureInfo.InvariantCulture);
        activeValue.Text = activeUsers.ToString(CultureInfo.InvariantCulture);
        errorsValue.Text = totalErrors.ToString("N0", CultureInfo.InvariantCulture);
        errorsValue.Foreground = new SolidColorBrush(totalErrors > 0 ? Error : TextPrimary);
        errorsAccent.Background = new SolidColorBrush(totalErrors > 0 ? Error : TextMuted);
        latencyValue.Text = latencyUsers.Length == 0
            ? "--"
            : FormatLatency(latencyUsers.Average(user => user.AverageLatencyMs));
        var highLatency = latencyUsers.Any(user => user.AverageLatencyMs >= 15000);
        latencyValue.Foreground = new SolidColorBrush(highLatency ? Warning : TextPrimary);
        latencyAccent.Background = new SolidColorBrush(highLatency ? Warning : TextMuted);

        RenderRows();
    }

    private void RenderRows()
    {
        rows.Children.Clear();
        passiveRows.Children.Clear();
        var active = currentUsers.Where(user => !IsPassive(user)).ToArray();
        var passive = currentUsers.Where(IsPassive).ToArray();
        for (var index = 0; index < active.Length; index++)
            rows.Children.Add(CreatePersonRow(active[index], index));
        for (var index = 0; index < passive.Length; index++)
            passiveRows.Children.Add(CreatePersonRow(passive[index], index + active.Length));

        if (active.Length == 0 && passive.Length == 0)
            rows.Children.Add(CreateEmptyState());
        if (passive.Length > 0)
        {
            rows.Children.Add(CreatePassiveHeader(passive.Length));
            rows.Children.Add(passiveRows);
            passiveRows.Visibility = passiveExpanded ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static bool IsPassive(TelemetryPerson user) =>
        !user.Online && user.Requests == 0 && user.TotalTokens == 0;

    private Border CreatePassiveHeader(int count)
    {
        var button = new Button
        {
            Content = $"NO RECENT ACTIVITY  ·  {count}",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Height = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Foreground = new SolidColorBrush(TextSecondary),
            Background = Theme.ButtonNormalBrush,
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(0, 1, 0, 0),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(content);
        template.VisualTree = border;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(BgElevated)));
        style.Triggers.Add(hover);
        button.Style = style;
        button.Click += (_, _) =>
        {
            passiveExpanded = !passiveExpanded;
            passiveRows.Visibility = passiveExpanded ? Visibility.Visible : Visibility.Collapsed;
        };
        return new Border { Child = button };
    }

    private static Grid CreateHeader()
    {
        return CreateGrid(new[] { "PERSON", "REQUESTS", "INPUT", "OUTPUT", "REASONING", "TOTAL", "MODELS", "ERRORS", "LATENCY", "LAST ACTIVE" }, true);
    }

    private static Grid CreateGrid(string[] values, bool header)
    {
        var grid = new Grid
        {
            MinWidth = ColumnWidths.Sum(),
            Height = header ? 40 : 52,
            Background = new SolidColorBrush(header ? BgHeader : BgElevated),
        };
        AddColumns(grid);
        for (var i = 0; i < values.Length; i++)
        {
            var text = new TextBlock
            {
                Text = values[i],
                Margin = new Thickness(i == 0 ? 16 : 8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(header ? TextSecondary : TextPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }

    private Border CreatePersonRow(TelemetryPerson user, int index)
    {
        var baseColor = index % 2 == 0 ? BgSurface : rowOdd;
        var row = new Grid
        {
            MinWidth = ColumnWidths.Sum(),
            MinHeight = 52,
            Background = new SolidColorBrush(baseColor),
        };
        AddColumns(row);

        var person = new Grid { Margin = new Thickness(16, 0, 8, 0) };
        person.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        person.ColumnDefinitions.Add(new ColumnDefinition());
        var avatar = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(GetAvatarColor(user.Name)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = GetInitials(user.Name),
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            },
        };
        person.Children.Add(avatar);
        var identity = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        nameLine.Children.Add(new TextBlock
        {
            Text = user.Name,
            Foreground = new SolidColorBrush(TextPrimary),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(user.Role))
            nameLine.Children.Add(new Border
            {
                Background = Theme.ElevatedBrush,
                BorderBrush = Theme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(7, 0, 0, 0),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock
                {
                    Text = user.Role.ToUpperInvariant(),
                    Foreground = new SolidColorBrush(Accent),
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                },
            });
        identity.Children.Add(nameLine);
        identity.Children.Add(new TextBlock
        {
            Text = user.Online ? "Online" : FormatPresence(user.LastActive),
            Foreground = new SolidColorBrush(user.Online ? Success : TextMuted),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
        });
        Grid.SetColumn(identity, 1);
        person.Children.Add(identity);
        AddCell(row, person, 0);
        AddCell(row, user.Requests.ToString("N0", CultureInfo.InvariantCulture), 1);
        AddCell(row, FormatTokens(Math.Max(0, user.InputTokens - user.CachedTokens)), 2);
        AddCell(row, FormatTokens(user.OutputTokens), 3);
        AddCell(row, FormatTokens(user.ReasoningTokens), 4);
        AddCell(row, FormatTokens(user.TotalTokens), 5, true, Accent);
        AddCell(row, user.Models.ToString("N0", CultureInfo.InvariantCulture), 6);
        AddCell(row, user.Errors.ToString("N0", CultureInfo.InvariantCulture), 7, false, user.Errors > 0 ? Error : TextSecondary);
        AddCell(row, user.AverageLatencyMs > 0 ? FormatLatency(user.AverageLatencyMs) : "--", 8, false,
            user.AverageLatencyMs >= 15000 ? Warning : TextSecondary);
        AddCell(row, user.LastActive == "now" ? "Active now" : user.LastActive, 9, false,
            user.Online ? Success : TextSecondary, TextAlignment.Left);

        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(RowHover);
        row.MouseLeave += (_, _) => row.Background = new SolidColorBrush(baseColor);
        return new Border
        {
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
    }

    private static void AddCell(Grid row, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        row.Children.Add(element);
    }

    private static void AddCell(Grid row, string text, int column, bool bold = false, Color? color = null,
        TextAlignment alignment = TextAlignment.Right)
    {
        AddCell(row, new TextBlock
        {
            Text = text,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = alignment,
            FontSize = 11,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(color ?? TextPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, column);
    }

    private static void AddColumns(Grid grid)
    {
        foreach (var width in ColumnWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
    }

    private static void ConfigureScrollViewer(ScrollViewer scroll)
    {
        var thumbTemplate = new ControlTemplate(typeof(Thumb));
        var thumbBorder = new FrameworkElementFactory(typeof(Border));
        thumbBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        thumbBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        thumbTemplate.VisualTree = thumbBorder;
        var thumbStyle = new Style(typeof(Thumb));
        thumbStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(ScrollThumb)));
        thumbStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
        thumbStyle.Setters.Add(new Setter(Control.TemplateProperty, thumbTemplate));
        var thumbHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        thumbHover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Accent)));
        thumbStyle.Triggers.Add(thumbHover);

        var scrollTemplate = new ControlTemplate(typeof(ScrollBar));
        var track = new FrameworkElementFactory(typeof(TelemetryScrollBarTrack));
        track.Name = "PART_Track";
        track.SetValue(Track.OrientationProperty, Orientation.Vertical);
        track.SetValue(Track.IsDirectionReversedProperty, true);
        scrollTemplate.VisualTree = track;
        var scrollStyle = new Style(typeof(ScrollBar));
        scrollStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, 10d));
        scrollStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(BgCanvas)));
        scrollStyle.Setters.Add(new Setter(Control.TemplateProperty, scrollTemplate));
        scroll.Resources.Add(typeof(Thumb), thumbStyle);
        scroll.Resources.Add(typeof(ScrollBar), scrollStyle);
    }

    private static Button CreateFilterButton(string text, bool selected)
    {
        var button = CreatePillButton(text, selected ? Accent : BgSurface,
            selected ? TextPrimary : TextSecondary, 48);
        button.Margin = new Thickness(0, 0, 1, 0);
        return button;
    }

    private static Button CreatePillButton(string text, Color background, Color foreground, double width)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = 30,
            Padding = new Thickness(7, 3, 7, 3),
            Background = new SolidColorBrush(background),
            Foreground = new SolidColorBrush(foreground),
            BorderBrush = Theme.ButtonBorderBrush,
            BorderThickness = new Thickness(1),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(content);
        template.VisualTree = border;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Accent)));
        style.Triggers.Add(hover);
        button.Style = style;
        return button;
    }

    private StackPanel CreateRangeSelector()
    {
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var option in new[] { ("24h", 1), ("7d", 7), ("30d", 30) })
        {
            var button = CreateFilterButton(option.Item1, option.Item2 == selectedRangeDays);
            button.Click += (_, _) =>
            {
                selectedRangeDays = option.Item2;
                foreach (var filter in rangeFilters.Children.OfType<Button>())
                    filter.Background = new SolidColorBrush((int)filter.Tag == selectedRangeDays
                        ? Accent : BgSurface);
                _ = RefreshAsync();
            };
            button.Tag = option.Item2;
            filters.Children.Add(button);
        }
        return filters;
    }

    private static TextBlock CreateMetricValue(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(TextPrimary),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
        };
    }

    private static Border CreateSummaryCard(int column, string label, TextBlock value,
        string detail, Color accent, out Border accentBar)
    {
        var content = new StackPanel { Margin = new Thickness(16, 13, 12, 13) };
        content.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(TextMuted),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(value);
        content.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
        });
        var card = new Grid();
        card.Children.Add(new Border
        {
            Background = new SolidColorBrush(BgSurface),
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
        });
        accentBar = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(3, 0, 0, 3),
        };
        card.Children.Add(accentBar);
        card.Children.Add(content);
        var wrapper = new Border { Child = card, Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 4 ? 0 : 5, 0) };
        Grid.SetColumn(wrapper, column);
        return wrapper;
    }

    private static Button CreateButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 84,
            Height = 34,
            Padding = new Thickness(12, 4, 12, 4),
            Background = new SolidColorBrush(Accent),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Accent),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(content);
        template.VisualTree = border;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, button.Background));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, button.BorderBrush));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Theme.AccentHoverBrush));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, Theme.AccentPressedBrush));
        style.Triggers.Add(pressed);
        button.Style = style;
        button.Click += async (_, _) => await action();
        return button;
    }

    private static TextBlock CreateEmptyState()
    {
        return new TextBlock
        {
            Text = "No telemetry data for this period.",
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(20, 42, 20, 42),
        };
    }

    private static string GetInitials(string name)
    {
        var words = (name ?? "?").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        return words.Length == 1
            ? words[0].Substring(0, 1).ToUpperInvariant()
            : (words[0].Substring(0, 1) + words[^1].Substring(0, 1)).ToUpperInvariant();
    }

    private static Color GetAvatarColor(string name)
    {
        var colors = new[] { Accent, Accent, Theme.ButtonBorderHover, Accent };
        var hash = 17;
        foreach (var character in name ?? "") hash = unchecked(hash * 31 + character);
        return colors[(hash & int.MaxValue) % colors.Length];
    }

    private static string FormatPresence(string lastActive) =>
        string.IsNullOrWhiteSpace(lastActive) || lastActive == "--" ? "No recent activity" : $"Last active {lastActive}";

    private static string FormatLatency(double milliseconds) =>
        $"{milliseconds / 1000:0.0}s";

    private static string FormatTokens(long value)
    {
        var absolute = Math.Abs(value);
        var suffix = absolute >= 1_000_000_000 ? "B" : absolute >= 1_000_000 ? "M" : absolute >= 1_000 ? "K" : "";
        var divisor = suffix == "B" ? 1_000_000_000d : suffix == "M" ? 1_000_000d : suffix == "K" ? 1_000d : 1d;
        return (value / divisor).ToString(suffix.Length == 0 ? "N0" : "0.#", CultureInfo.InvariantCulture) + suffix;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

internal sealed class TelemetryScrollBarTrack : Track
{
    public TelemetryScrollBarTrack()
    {
        Thumb = new Thumb
        {
            Background = Theme.ScrollThumbBrush,
            MinHeight = 28,
        };
    }
}
