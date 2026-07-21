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
using System.Windows.Shapes;

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
    private static Color RowHover => Color.FromRgb(0x2e, 0x2e, 0x2e);
    private static Color ScrollThumb => Theme.ScrollThumb;

    private readonly LatrixApiClient latrix;
    private readonly string apiKey;
    private readonly CancellationTokenSource lifetime = new();
    private readonly StackPanel rows;
    private readonly TextBlock totalTokensValue;
    private readonly TextBlock requestsValue;
    private readonly TextBlock activeValue;
    private readonly TextBlock errorsValue;
    private readonly TextBlock latencyValue;
    private readonly StackPanel onlineCards;
    private readonly Border errorsAccent;
    private readonly Border latencyAccent;
    private readonly List<Button> periodButtons = new();
    private IReadOnlyList<TelemetryPerson> currentUsers = Array.Empty<TelemetryPerson>();
    private IReadOnlySet<string> activeUserIds = new HashSet<string>(StringComparer.Ordinal);
    private int selectedDays = 7;
    private bool loading;
    private bool activeLoading;

    public TelemetryPanel(LatrixApiClient latrix, string apiKey)
    {
        this.latrix = latrix;
        this.apiKey = apiKey;
        Background = new SolidColorBrush(BgCanvas);
        MinWidth = 1120;
        MinHeight = 720;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var dashboard = new Grid { Margin = new Thickness(24, 24, 24, 24) };
        dashboard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dashboard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        dashboard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dashboard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        dashboard.RowDefinitions.Add(new RowDefinition());

        var periodSelector = CreatePeriodSelector();
        Grid.SetRow(periodSelector, 0);
        dashboard.Children.Add(periodSelector);

        var summary = CreateSummary(out totalTokensValue, out requestsValue, out activeValue, out errorsValue,
            out latencyValue, out errorsAccent, out latencyAccent);
        Grid.SetRow(summary, 2);
        dashboard.Children.Add(summary);

        var contentArea = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        contentArea.ColumnDefinitions.Add(new ColumnDefinition());
        contentArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        contentArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leftContent = new Grid();
        leftContent.RowDefinitions.Add(new RowDefinition());

        var table = new Border
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
        Grid.SetRow(table, 0);
        leftContent.Children.Add(table);
        contentArea.Children.Add(leftContent);

        var online = CreateOnlineCard(out onlineCards);
        Grid.SetColumn(online, 2);
        Grid.SetRowSpan(online, 2);
        contentArea.Children.Add(online);
        Grid.SetRow(contentArea, 4);
        dashboard.Children.Add(contentArea);
        root.Children.Add(dashboard);
        Content = root;

        Loaded += (_, _) =>
        {
            _ = RefreshAsync();
            _ = RefreshLoopAsync();
            _ = RefreshActiveAsync();
            _ = RefreshActiveLoopAsync();
        };

    }

    private Grid CreatePeriodSelector()
    {
        var selector = new Grid();
        selector.ColumnDefinitions.Add(new ColumnDefinition());
        selector.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selector.Children.Add(new TextBlock
        {
            Text = "TELEMETRY",
            Foreground = new SolidColorBrush(TextPrimary),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        AddPeriodButton(buttons, "Daily", 1);
        AddPeriodButton(buttons, "7 days", 7);
        AddPeriodButton(buttons, "Monthly", 30);
        Grid.SetColumn(buttons, 1);
        selector.Children.Add(buttons);
        UpdatePeriodButtonStyles();
        return selector;
    }

    private void AddPeriodButton(Panel parent, string label, int days)
    {
        var button = new Button
        {
            Content = label,
            Tag = days,
            Width = 78,
            Height = 30,
            Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 10,
            BorderThickness = new Thickness(1),
            Focusable = false,
        };
        button.Click += (_, _) =>
        {
            selectedDays = days;
            UpdatePeriodButtonStyles();
            _ = RefreshAsync();
        };
        periodButtons.Add(button);
        parent.Children.Add(button);
    }

    private void UpdatePeriodButtonStyles()
    {
        foreach (var button in periodButtons)
        {
            var selected = (int)button.Tag == selectedDays;
            button.Background = new SolidColorBrush(selected ? Accent : BgSurface);
            button.BorderBrush = new SolidColorBrush(selected ? Accent : BgBorder);
            button.Foreground = new SolidColorBrush(selected ? BgCanvas : TextSecondary);
        }
    }

    private Grid CreateSummary(out TextBlock totalTokensValue, out TextBlock requestsValue, out TextBlock activeValue,
        out TextBlock errorsValue, out TextBlock latencyValue, out Border errorsAccent, out Border latencyAccent)
    {
        var summary = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var totalTokens = CreateMetricValue("0");
        var requests = CreateMetricValue("0");
        var active = CreateMetricValue("0");
        var errors = CreateMetricValue("0");
        var latency = CreateMetricValue("--");
        summary.Children.Add(CreateSummaryCard(0, 0, "TOTAL TOKENS", totalTokens, "Across this period", TextSecondary, out _));
        summary.Children.Add(CreateSummaryCard(1, 0, "REQUESTS", requests, "All team members", TextSecondary, out _));
        summary.Children.Add(CreateSummaryCard(2, 0, "ACTIVE NOW", active, "Currently online", Success, out _));
        summary.Children.Add(CreateSummaryCard(3, 0, "ERRORS", errors, "Needs attention", TextMuted, out errorsAccent));
        summary.Children.Add(CreateSummaryCard(4, 0, "AVG LATENCY", latency, "Across active users", Warning, out latencyAccent));
        totalTokensValue = totalTokens;
        requestsValue = requests;
        activeValue = active;
        errorsValue = errors;
        latencyValue = latency;
        return summary;
    }



    private Border CreateOnlineCard(out StackPanel cards)
    {
        cards = new StackPanel();
        var title = new Grid();
        var titleCopy = new StackPanel { Orientation = Orientation.Horizontal };
        titleCopy.Children.Add(new Border
        {
            Width = 7,
            Height = 7,
            Background = new SolidColorBrush(Success),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleCopy.Children.Add(new TextBlock
        {
            Text = "ONLINE NOW",
            Foreground = new SolidColorBrush(Success),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        });
        title.Children.Add(titleCopy);

        var content = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(title);
        content.RowDefinitions.Add(new RowDefinition());
        var cardsScroll = new ScrollViewer
        {
            Content = cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 14, 0, 0),
        };
        ConfigureScrollViewer(cardsScroll);
        Grid.SetRow(cardsScroll, 1);
        content.Children.Add(cardsScroll);

        return new Border
        {
            Background = new SolidColorBrush(BgSurface),
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            MinWidth = 220,
            Child = content,
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

    private async Task RefreshActiveLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
            while (await timer.WaitForNextTickAsync(lifetime.Token)) await RefreshActiveAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task RefreshAsync()
    {
        if (loading || lifetime.IsCancellationRequested) return;
        loading = true;
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No Latrix API key was found.");
            var users = await latrix.ReadTelemetryAsync(apiKey, selectedDays, lifetime.Token);
            Render(users);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            Render(Array.Empty<TelemetryPerson>());
        }
        finally
        {
            loading = false;
        }
    }

    private async Task RefreshActiveAsync()
    {
        if (activeLoading || lifetime.IsCancellationRequested) return;
        activeLoading = true;
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No Latrix API key was found.");
            var users = await latrix.ReadActiveAsync(apiKey, lifetime.Token);
            RenderActiveUsers(users);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            RenderActiveUsers(Array.Empty<LatrixActiveUser>());
        }
        finally
        {
            activeLoading = false;
        }
    }

    private void Render(IReadOnlyList<TelemetryPerson> users)
    {
        var ordered = users.OrderByDescending(user => user.TotalTokens).ToArray();
        currentUsers = ordered;
        var totalTokens = ordered.Sum(user => user.TotalTokens);
        var totalRequests = ordered.Sum(user => user.Requests);
        var totalErrors = ordered.Sum(user => user.Errors);
        var latencyUsers = ordered.Where(user => user.AverageLatencyMs > 0).ToArray();
        totalTokensValue.Text = FormatTokens(totalTokens);
        requestsValue.Text = totalRequests.ToString("N0", CultureInfo.InvariantCulture);
        errorsValue.Text = totalErrors.ToString("N0", CultureInfo.InvariantCulture);
        errorsValue.Foreground = new SolidColorBrush(totalErrors > 0 ? Error : TextPrimary);
        latencyValue.Text = latencyUsers.Length == 0 ? "--" : FormatLatency(latencyUsers.Average(user => user.AverageLatencyMs));
        latencyValue.Foreground = new SolidColorBrush(latencyUsers.Any(user => user.AverageLatencyMs >= 15000) ? Warning : TextPrimary);
        errorsAccent.Background = new SolidColorBrush(totalErrors > 0 ? Error : TextMuted);
        latencyAccent.Background = new SolidColorBrush(latencyUsers.Any(user => user.AverageLatencyMs >= 15000) ? Warning : TextMuted);

        RenderRows();
    }

    private void RenderActiveUsers(IReadOnlyList<LatrixActiveUser> users)
    {
        onlineCards.Children.Clear();
        activeUserIds = users
            .Where(user => !string.IsNullOrWhiteSpace(user.UserId))
            .Select(user => user.UserId)
            .ToHashSet(StringComparer.Ordinal);
        activeValue.Text = users.Count.ToString(CultureInfo.InvariantCulture);
        RenderRows();
        if (users.Count == 0)
            return;

        for (var i = 0; i < users.Count; i++)
        {
            if (i > 0)
            {
                onlineCards.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(BgBorder),
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }
            onlineCards.Children.Add(CreateOnlineUserCard(users[i]));
        }
    }

    private static UIElement CreateOnlineUserCard(LatrixActiveUser user)
    {
        var row = new StackPanel();

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        nameLine.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            Background = new SolidColorBrush(Success),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        nameLine.Children.Add(new TextBlock
        {
            Text = user.Name,
            Foreground = new SolidColorBrush(TextPrimary),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(nameLine);

        var usageLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(13, 8, 0, 0) };
        usageLine.Children.Add(CreateUsageBadge(string.IsNullOrWhiteSpace(user.Model) ? "-" : user.Model, TextSecondary));
        if (!string.IsNullOrWhiteSpace(user.Effort))
            usageLine.Children.Add(CreateUsageBadge(user.Effort, Accent, new Thickness(5, 0, 0, 0)));
        usageLine.Children.Add(new TextBlock
        {
            Text = FormatElapsed(user.ElapsedMs),
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 10,
            Margin = new Thickness(8, 3, 0, 0),
        });
        row.Children.Add(usageLine);

        return row;
    }

    private static Border CreateUsageBadge(string text, Color foreground, Thickness? margin = null)
    {
        return new Border
        {
            Background = new SolidColorBrush(BgElevated),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = margin ?? new Thickness(0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(foreground),
                FontSize = 9,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
    }

    private void RenderRows()
    {
        rows.Children.Clear();
        if (currentUsers.Any())
        {
            for (var index = 0; index < currentUsers.Count; index++)
                rows.Children.Add(CreatePersonRow(currentUsers[index], index,
                    activeUserIds.Contains(currentUsers[index].UserId)));
        }
        else
        {
            rows.Children.Add(CreateEmptyState());
        }
    }

    private static Grid CreateHeader()
    {
        return CreateGrid(new[] { "TEAM MEMBER", "REQUESTS", "INPUT", "OUTPUT", "REASONING", "TOTAL", "MODELS", "ERRORS", "LATENCY", "LAST ACTIVE" }, true);
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

    private Border CreatePersonRow(TelemetryPerson user, int index, bool online)
    {
        var baseColor = index % 2 == 0 ? BgSurface : Color.FromRgb(0x18, 0x18, 0x18);
        var row = new Grid
        {
            MinWidth = ColumnWidths.Sum(),
            MinHeight = 52,
            Background = new SolidColorBrush(baseColor),
        };
        AddColumns(row);
        var person = new Grid { Margin = new Thickness(16, 0, 8, 0) };
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        nameLine.Children.Add(new TextBlock
        {
            Text = user.Name,
            Foreground = new SolidColorBrush(TextPrimary),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        identity.Children.Add(nameLine);
        identity.Children.Add(new TextBlock
        {
            Text = online ? "Online" : FormatPresence(user.LastActive),
            Foreground = new SolidColorBrush(online ? Success : TextMuted),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
        });
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
        AddCell(row, online ? "Active now" : user.LastActive, 9, false,
            online ? Success : TextSecondary, TextAlignment.Left);
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



    private static Border CreateSummaryCard(int column, int row, string label, TextBlock value,
        string detail, Color accent, out Border accentBar, int columnSpan = 1)
    {
        var content = new StackPanel { Margin = new Thickness(16, 14, 12, 12) };
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
        card.Children.Add(CreateSparkline(accent));
        var wrapper = new Border
        {
            Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 4 ? 0 : 5, 0),
            Child = card,
        };
        Grid.SetColumn(wrapper, column);
        Grid.SetRow(wrapper, row);
        if (columnSpan > 1) Grid.SetColumnSpan(wrapper, columnSpan);
        return wrapper;
    }

    private static Canvas CreateSparkline(Color accent)
    {
        var canvas = new Canvas
        {
            Width = 76,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 14, 14),
            IsHitTestVisible = false,
        };
        canvas.Children.Add(new Polyline
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1.25,
            Opacity = 0.9,
            Points = new PointCollection
            {
                new Point(0, 25), new Point(8, 20), new Point(13, 22), new Point(21, 12),
                new Point(27, 17), new Point(35, 8), new Point(42, 18), new Point(49, 13),
                new Point(57, 23), new Point(65, 10), new Point(76, 5),
            },
        });
        canvas.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(accent),
            Margin = new Thickness(73, 2, 0, 0),
        });
        return canvas;
    }

    private static TextBlock CreateEmptyState()
    {
        return new TextBlock
        {
            Text = "No teammate activity in this period.",
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(20, 42, 20, 42),
        };
    }

    private static string FormatPresence(string lastActive) =>
        string.IsNullOrWhiteSpace(lastActive) || lastActive == "--" ? "No recent activity" : $"Last active {lastActive}";

    private static string FormatLatency(double milliseconds) =>
        $"{milliseconds / 1000:0.0}s";

    private static string FormatElapsed(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds) / 1000;
        return seconds < 60
            ? $"{seconds}s"
            : $"{seconds / 60}m {seconds % 60}s";
    }

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
