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
    private static Color RowHover => Color.FromRgb(0x2e, 0x2e, 0x2e);
    private static Color ScrollThumb => Theme.ScrollThumb;

    private readonly LatrixApiClient latrix;
    private readonly string apiKey;
    private readonly CancellationTokenSource lifetime = new();
    private readonly StackPanel rows;
    private readonly StackPanel passiveRows;
    private readonly TextBlock totalTokensValue;
    private readonly TextBlock requestsValue;
    private readonly TextBlock activeValue;
    private readonly TextBlock errorsValue;
    private readonly TextBlock latencyValue;
    private readonly TextBlock yourTokensValue;
    private readonly TextBlock yourRequestsValue;
    private readonly TextBlock yourErrorsValue;
    private readonly TextBlock yourLatencyValue;
    private readonly TextBlock onlineCountValue;
    private readonly StackPanel onlineCards;
    private readonly Border errorsAccent;
    private readonly Border latencyAccent;
    private IReadOnlyList<TelemetryPerson> currentUsers = Array.Empty<TelemetryPerson>();
    private string currentUserId;
    private string currentUserName;
    private bool passiveExpanded;
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
        root.RowDefinitions.Add(new RowDefinition());

        var summary = CreateSummary(out totalTokensValue, out requestsValue, out activeValue, out errorsValue,
            out latencyValue, out errorsAccent, out latencyAccent);
        Grid.SetRow(summary, 0);
        root.Children.Add(summary);

        var contentArea = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        contentArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3.55, GridUnitType.Star) });
        contentArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        contentArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftContent = new Grid();
        leftContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftContent.RowDefinitions.Add(new RowDefinition());

        var usage = CreateUsageCard(out yourTokensValue, out yourRequestsValue, out yourErrorsValue, out yourLatencyValue);
        Grid.SetRow(usage, 0);
        leftContent.Children.Add(usage);

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
        Grid.SetRow(table, 1);
        leftContent.Children.Add(table);
        contentArea.Children.Add(leftContent);

        var online = CreateOnlineCard(out onlineCountValue, out onlineCards);
        Grid.SetColumn(online, 2);
        Grid.SetRowSpan(online, 2);
        contentArea.Children.Add(online);
        Grid.SetRow(contentArea, 1);
        root.Children.Add(contentArea);
        Content = root;

        Loaded += (_, _) =>
        {
            _ = RefreshAsync();
            _ = RefreshLoopAsync();
        };

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
        summary.Children.Add(CreateSummaryCard(0, 0, "TOTAL TOKENS", totalTokens, "Across this period", Accent, out _));
        summary.Children.Add(CreateSummaryCard(1, 0, "REQUESTS", requests, "All team members", Accent, out _));
        summary.Children.Add(CreateSummaryCard(2, 0, "ACTIVE NOW", active, "Currently online", Success, out _));
        summary.Children.Add(CreateSummaryCard(3, 0, "ERRORS", errors, "Needs attention", Error, out errorsAccent));
        summary.Children.Add(CreateSummaryCard(4, 0, "AVG LATENCY", latency, "Across active users", TextMuted, out latencyAccent));
        totalTokensValue = totalTokens;
        requestsValue = requests;
        activeValue = active;
        errorsValue = errors;
        latencyValue = latency;
        return summary;
    }

    private Border CreateUsageCard(out TextBlock tokens, out TextBlock requests, out TextBlock errors, out TextBlock latency)
    {
        tokens = CreateUsageValue("--");
        requests = CreateUsageValue("--");
        errors = CreateUsageValue("--");
        latency = CreateUsageValue("--");
        var metrics = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        for (var i = 0; i < 4; i++)
            metrics.ColumnDefinitions.Add(new ColumnDefinition());
        AddUsageMetric(metrics, 0, "TOKENS", tokens);
        AddUsageMetric(metrics, 1, "REQUESTS", requests);
        AddUsageMetric(metrics, 2, "ERRORS", errors);
        AddUsageMetric(metrics, 3, "AVG LATENCY", latency);

        var content = new StackPanel { Margin = new Thickness(18, 14, 18, 13) };
        content.Children.Add(new TextBlock
        {
            Text = "YOUR USAGE",
            Foreground = new SolidColorBrush(Accent),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(metrics);
        var card = new Border
        {
            Height = 117,
            Margin = new Thickness(0, 0, 0, 19),
            Background = new SolidColorBrush(BgSurface),
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = content,
        };
        return card;
    }

    private static void AddUsageMetric(Grid metrics, int column, string label, TextBlock value)
    {
        var block = new StackPanel();
        block.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(TextMuted),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
        });
        block.Children.Add(value);
        Grid.SetColumn(block, column);
        metrics.Children.Add(block);
    }

    private Border CreateOnlineCard(out TextBlock count, out StackPanel cards)
    {
        count = new TextBlock
        {
            Text = "0 people online",
            Foreground = new SolidColorBrush(TextPrimary),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cards = new StackPanel();
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        Grid.SetColumn(count, 1);
        title.Children.Add(count);

        var content = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = "Most-used model by active user",
            Foreground = new SolidColorBrush(TextMuted),
            FontSize = 10,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(subtitle, 1);
        content.Children.Add(subtitle);
        content.RowDefinitions.Add(new RowDefinition());
        var cardsScroll = new ScrollViewer
        {
            Content = cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 14, 0, 0),
        };
        ConfigureScrollViewer(cardsScroll);
        Grid.SetRow(cardsScroll, 2);
        content.Children.Add(cardsScroll);

        return new Border
        {
            Background = new SolidColorBrush(BgSurface),
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
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

    private async Task RefreshAsync()
    {
        if (loading || lifetime.IsCancellationRequested) return;
        loading = true;
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No Latrix API key was found.");
            var users = await latrix.ReadTelemetryAsync(apiKey, 7, lifetime.Token);
            if (currentUserId == null)
            {
                try
                {
                    var identity = await latrix.ReadIdentityAsync(apiKey, lifetime.Token);
                    currentUserId = identity.UserId;
                    currentUserName = identity.Name;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception) { }
            }
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
        latencyValue.Text = latencyUsers.Length == 0 ? "--" : FormatLatency(latencyUsers.Average(user => user.AverageLatencyMs));
        latencyValue.Foreground = new SolidColorBrush(latencyUsers.Any(user => user.AverageLatencyMs >= 15000) ? Warning : TextPrimary);
        errorsAccent.Background = new SolidColorBrush(totalErrors > 0 ? Error : TextMuted);
        latencyAccent.Background = new SolidColorBrush(latencyUsers.Any(user => user.AverageLatencyMs >= 15000) ? Warning : TextMuted);

        var current = FindCurrentUser(ordered);
        yourTokensValue.Text = current == null ? "--" : FormatTokens(current.TotalTokens);
        yourRequestsValue.Text = current == null ? "--" : current.Requests.ToString("N0", CultureInfo.InvariantCulture);
        yourErrorsValue.Text = current == null ? "--" : current.Errors.ToString("N0", CultureInfo.InvariantCulture);
        yourLatencyValue.Text = current == null || current.AverageLatencyMs <= 0 ? "--" : FormatLatency(current.AverageLatencyMs);

        onlineCountValue.Text = $"{activeUsers} people online";
        RenderOnlineUsers(ordered.Where(user => user.Online));
        RenderRows();
    }

    private void RenderOnlineUsers(IEnumerable<TelemetryPerson> users)
    {
        onlineCards.Children.Clear();
        var onlineUsers = users.OrderByDescending(user => user.LastActiveUtc ?? DateTimeOffset.MinValue).ToArray();
        if (onlineUsers.Length == 0)
        {
            onlineCards.Children.Add(new TextBlock
            {
                Text = "Waiting for identity...",
                Foreground = new SolidColorBrush(TextSecondary),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 34, 0, 0),
            });
            return;
        }

        foreach (var user in onlineUsers)
        {
            var model = (user.Breakdown ?? Array.Empty<TelemetryBreakdown>())
                .OrderByDescending(breakdown => breakdown.TotalTokens)
                .FirstOrDefault();
            var effort = model?.EffortItems?.OrderByDescending(item => item.Requests).FirstOrDefault();
            onlineCards.Children.Add(CreateOnlineUserCard(user, model?.Model, effort?.Effort));
        }
    }

    private static Border CreateOnlineUserCard(TelemetryPerson user, string model, string effort)
    {
        var details = new StackPanel();
        details.Children.Add(new TextBlock
        {
            Text = user.Name,
            Foreground = new SolidColorBrush(TextPrimary),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        details.Children.Add(new TextBlock
        {
            Text = $"MODEL  {(string.IsNullOrWhiteSpace(model) ? "--" : model)}",
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 10,
            Margin = new Thickness(0, 5, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        details.Children.Add(new TextBlock
        {
            Text = $"EFFORT  {(string.IsNullOrWhiteSpace(effort) ? "--" : effort)}",
            Foreground = new SolidColorBrush(Accent),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return new Border
        {
            Background = new SolidColorBrush(BgElevated),
            BorderBrush = new SolidColorBrush(BgBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = details,
        };
    }

    private TelemetryPerson FindCurrentUser(IReadOnlyList<TelemetryPerson> users)
    {
        if (!string.IsNullOrWhiteSpace(currentUserId))
            return users.FirstOrDefault(user => string.Equals(user.UserId, currentUserId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(currentUserName))
            return users.FirstOrDefault(user => string.Equals(user.Name, currentUserName, StringComparison.OrdinalIgnoreCase));
        return null;
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

    private Border CreatePersonRow(TelemetryPerson user, int index)
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
            Text = user.Online ? "Online" : FormatPresence(user.LastActive),
            Foreground = new SolidColorBrush(user.Online ? Success : TextMuted),
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

    private static TextBlock CreateUsageValue(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 0),
        };
    }

    private static Border CreateSummaryCard(int column, int row, string label, TextBlock value,
        string detail, Color accent, out Border accentBar, int columnSpan = 1)
    {
        var content = new StackPanel { Margin = new Thickness(15, 13, 12, 11) };
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
