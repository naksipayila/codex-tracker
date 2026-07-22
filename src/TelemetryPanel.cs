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
    private const double OnlinePanelWidth = 220;
    private static readonly double[] ColumnWidths = { 205, 78, 82, 82, 92, 86, 62, 96 };

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
    private static Color RowHover => Color.FromRgb(0x2e, 0x2e, 0x2e);
    private static Color ScrollThumb => Theme.ScrollThumb;

    private readonly LatrixApiClient latrix;
    private readonly string apiKey;
    private readonly CancellationTokenSource lifetime = new();
    private readonly StackPanel rows;
    private readonly TextBlock totalTokensValue;
    private readonly TextBlock requestsValue;
    private readonly TextBlock activeValue;
    private readonly TextBlock periodSummaryDetail;
    private readonly Canvas totalTokensSparkline;
    private readonly Canvas requestsSparkline;
    private readonly Canvas activeSparkline;
    private readonly StackPanel onlineCards;
    private readonly List<Button> periodButtons = new();
    private IReadOnlyList<TelemetryPerson> currentUsers = Array.Empty<TelemetryPerson>();
    private IReadOnlySet<string> activeUserIds = new HashSet<string>(StringComparer.Ordinal);
    private string selectedUserId;
    private int selectedDays = 7;
    private bool loading;
    private bool activeLoading;
    private long latestTotalTokens;
    private int latestRequests;
    private int latestActiveUsers;
    private readonly List<TelemetrySnapshot> snapshots = new();

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

        var summary = CreateSummary(out totalTokensValue, out requestsValue, out activeValue,
            out periodSummaryDetail, out totalTokensSparkline, out requestsSparkline, out activeSparkline);
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
            Style = CreatePeriodButtonStyle(),
        };
        button.MouseEnter += (_, _) => UpdatePeriodButtonStyles();
        button.MouseLeave += (_, _) => UpdatePeriodButtonStyles();
        button.Click += (_, _) =>
        {
            selectedDays = days;
            UpdatePeriodButtonStyles();
            periodSummaryDetail.Text = GetPeriodSummary(days);
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
            var hovered = button.IsMouseOver;
            button.Background = new SolidColorBrush(hovered
                ? selected ? Theme.AccentHover : Theme.ButtonHover
                : selected ? Accent : BgSurface);
            button.BorderBrush = new SolidColorBrush(hovered
                ? selected ? Theme.AccentHover : Theme.ButtonBorderHover
                : selected ? Accent : BgBorder);
            button.Foreground = new SolidColorBrush(selected ? BgCanvas : TextSecondary);
        }
    }

    private static Style CreatePeriodButtonStyle()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        content.SetValue(ContentPresenter.ContentStringFormatProperty,
            new TemplateBindingExtension(ContentControl.ContentStringFormatProperty));
        content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty,
            new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        border.AppendChild(content);
        template.VisualTree = border;

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private Grid CreateSummary(out TextBlock totalTokensValue, out TextBlock requestsValue, out TextBlock activeValue,
        out TextBlock periodSummaryDetail, out Canvas totalTokensSparkline, out Canvas requestsSparkline,
        out Canvas activeSparkline)
    {
        var summary = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(OnlinePanelWidth) });
        summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var totalTokens = CreateMetricValue("0");
        var requests = CreateMetricValue("0");
        var active = CreateMetricValue("0");
        var totalTokensCard = CreateSummaryCard(0, 0, "TOTAL TOKENS", totalTokens, GetPeriodSummary(selectedDays), TextSecondary,
            "Total tokens", out _, out totalTokensSparkline, out periodSummaryDetail);
        totalTokensCard.Margin = new Thickness(0, 0, 9, 0);
        summary.Children.Add(totalTokensCard);
        var requestsCard = CreateSummaryCard(1, 0, "REQUESTS", requests, "All team members", TextSecondary,
            "Requests", out _, out requestsSparkline, out _);
        requestsCard.Margin = new Thickness(9, 0, 0, 0);
        summary.Children.Add(requestsCard);
        var activeCard = CreateSummaryCard(3, 0, "ACTIVE NOW", active, "Currently online", Success,
            "Active users", out _, out activeSparkline, out _);
        activeCard.Width = OnlinePanelWidth;
        activeCard.Margin = new Thickness(0);
        summary.Children.Add(activeCard);
        totalTokensValue = totalTokens;
        requestsValue = requests;
        activeValue = active;
        return summary;
    }

    private static string GetPeriodSummary(int days) => days switch
    {
        1 => "Today",
        30 => "Last 30 days",
        _ => $"Last {days} days",
    };



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
            MinWidth = OnlinePanelWidth,
            Width = OnlinePanelWidth,
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
        totalTokensValue.Text = FormatTokens(totalTokens);
        requestsValue.Text = totalRequests.ToString("N0", CultureInfo.InvariantCulture);
        latestTotalTokens = totalTokens;
        latestRequests = totalRequests;
        RecordSnapshot();
        UpdateSparklines();

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
        latestActiveUsers = users.Count;
        RecordSnapshot();
        UpdateSparklines();
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
            if (selectedUserId != null && !currentUsers.Any(user => user.UserId == selectedUserId))
                selectedUserId = null;
            for (var index = 0; index < currentUsers.Count; index++)
            {
                var user = currentUsers[index];
                rows.Children.Add(CreatePersonRow(user, index, activeUserIds.Contains(user.UserId)));
                if (user.UserId == selectedUserId)
                    rows.Children.Add(CreatePersonDetails(user));
            }
        }
        else
        {
            rows.Children.Add(CreateEmptyState());
        }
    }

    private static Grid CreateHeader()
    {
        return CreateGrid(new[] { "TEAM MEMBER", "REQUESTS", "INPUT", "OUTPUT", "REASONING", "TOTAL", "MODELS", "LAST ACTIVE" }, true);
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
        AddCell(row, online ? "Active now" : user.LastActive, 7, false,
            online ? Success : TextSecondary, TextAlignment.Right);
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(RowHover);
        row.MouseLeave += (_, _) => row.Background = new SolidColorBrush(baseColor);
        var container = new Border
        {
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
        container.Cursor = Cursors.Hand;
        container.MouseLeftButtonUp += (_, args) =>
        {
            selectedUserId = selectedUserId == user.UserId ? null : user.UserId;
            RenderRows();
            args.Handled = true;
        };
        return container;
    }

    internal static Border CreatePersonDetails(TelemetryPerson user)
    {
        var content = new StackPanel { Margin = new Thickness(24, 12, 16, 14) };
        content.Children.Add(new TextBlock
        {
            Text = "MODELS USED",
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var breakdown = (user.Breakdown ?? Array.Empty<TelemetryBreakdown>())
            .OrderByDescending(item => item.Requests)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (breakdown.Length == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "No model usage for this period.",
                Foreground = new SolidColorBrush(TextMuted),
                FontSize = 11,
            });
        }
        else
        {
            foreach (var item in breakdown)
            {
                var line = new Grid { MinHeight = 32, Margin = new Thickness(0, 0, 0, 5) };
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var model = new StackPanel();
                model.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Model) ? "Unknown" : item.Model,
                    Foreground = new SolidColorBrush(TextPrimary),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                model.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Provider) ? "" : item.Provider,
                    Foreground = new SolidColorBrush(TextMuted),
                    FontSize = 9,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                Grid.SetColumn(model, 0);
                line.Children.Add(model);
                AddDetailValue(line, item.Requests.ToString("N0", CultureInfo.InvariantCulture), 1);
                AddDetailValue(line, FormatTokens(item.TotalTokens), 2, Accent);
                content.Children.Add(line);
            }
        }

        return new Border
        {
            Background = new SolidColorBrush(BgElevated),
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = content,
        };
    }

    private static void AddDetailValue(Grid line, string text, int column, Color? color = null)
    {
        var value = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(color ?? TextSecondary),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 8, 0),
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(value, column);
        line.Children.Add(value);
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
        for (var index = 0; index < ColumnWidths.Length; index++)
        {
            var width = index == ColumnWidths.Length - 1
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(ColumnWidths[index]);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }
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



    private Border CreateSummaryCard(int column, int row, string label, TextBlock value,
        string detail, Color accent, string graphTitle, out Border accentBar, out Canvas sparkline,
        out TextBlock detailText, int columnSpan = 1)
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
        detailText = new TextBlock
        {
            Text = detail,
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
        };
        content.Children.Add(detailText);
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
        sparkline = CreateSparkline(accent, Array.Empty<double>());
        card.Children.Add(sparkline);
        var wrapper = new Border
        {
            Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 4 ? 0 : 5, 0),
            Child = card,
            Cursor = Cursors.Hand,
        };
        wrapper.MouseLeftButtonUp += (_, _) =>
        {
            var window = new TelemetryGraphWindow(graphTitle, GetPeriodSummary(selectedDays), () => snapshots.ToArray(), graphTitle switch
            {
                "Total tokens" => snapshot => snapshot.TotalTokens,
                "Requests" => snapshot => snapshot.Requests,
                _ => snapshot => snapshot.ActiveUsers,
            });
            window.Show();
            window.Activate();
        };
        Grid.SetColumn(wrapper, column);
        Grid.SetRow(wrapper, row);
        if (columnSpan > 1) Grid.SetColumnSpan(wrapper, columnSpan);
        return wrapper;
    }

    private static Canvas CreateSparkline(Color accent, IReadOnlyList<double> values)
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
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1.25,
            Opacity = 0.9,
            Points = CreateSparklinePoints(values),
        };
        canvas.Children.Add(line);
        if (values.Count > 0)
        {
            var last = line.Points[^1];
            canvas.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(accent),
                Margin = new Thickness(last.X - 2.5, last.Y - 2.5, 0, 0),
            });
        }
        return canvas;
    }

    private static PointCollection CreateSparklinePoints(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new PointCollection();
        var min = values.Min();
        var max = values.Max();
        var range = Math.Max(1, max - min);
        return new PointCollection(values.Select((value, index) => new Point(
            values.Count == 1 ? 76 : index * 76d / (values.Count - 1),
            29 - (value - min) / range * 24)).ToArray());
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

    private void RecordSnapshot()
    {
        var snapshot = new TelemetrySnapshot(DateTimeOffset.UtcNow, latestTotalTokens, latestRequests, latestActiveUsers);
        if (snapshots.Count > 0 && (snapshot.CapturedAtUtc - snapshots[^1].CapturedAtUtc).TotalSeconds < 1)
            snapshots[^1] = snapshot;
        else
            snapshots.Add(snapshot);
        if (snapshots.Count > 90) snapshots.RemoveAt(0);
    }

    private void UpdateSparklines()
    {
        UpdateSparkline(totalTokensSparkline, snapshots.Select(snapshot => (double)snapshot.TotalTokens), TextSecondary);
        UpdateSparkline(requestsSparkline, snapshots.Select(snapshot => (double)snapshot.Requests), TextSecondary);
        UpdateSparkline(activeSparkline, snapshots.Select(snapshot => (double)snapshot.ActiveUsers), Success);
    }

    private static void UpdateSparkline(Canvas canvas, IEnumerable<double> values, Color accent)
    {
        var points = CreateSparklinePoints(values.ToArray());
        canvas.Children.Clear();
        canvas.Children.Add(new Polyline
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1.25,
            Opacity = 0.9,
            Points = points,
        });
        if (points.Count == 0) return;
        var last = points[^1];
        canvas.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(accent),
            Margin = new Thickness(last.X - 2.5, last.Y - 2.5, 0, 0),
        });
    }

    private static string FormatPresence(string lastActive) =>
        string.IsNullOrWhiteSpace(lastActive) || lastActive == "--" ? "No recent activity" : $"Last active {lastActive}";

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
