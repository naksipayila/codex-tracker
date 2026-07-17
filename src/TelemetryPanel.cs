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
    private static readonly Color CanvasColor = Color.FromRgb(0x18, 0x0b, 0x0f);
    private static readonly Color SurfaceColor = Color.FromRgb(0x24, 0x11, 0x16);
    private static readonly Color RaisedColor = Color.FromRgb(0x32, 0x18, 0x21);
    private static readonly Color HeaderColor = Color.FromRgb(0x3a, 0x1b, 0x24);
    private static readonly Color BorderColor = Color.FromRgb(0x63, 0x31, 0x3d);
    private static readonly Color MutedColor = Color.FromRgb(0xc3, 0x9c, 0xa5);
    private static readonly Color DimColor = Color.FromRgb(0x8f, 0x66, 0x70);
    private static readonly Color TextColor = Color.FromRgb(0xff, 0xf1, 0xf3);
    private static readonly Color AccentColor = Color.FromRgb(0xd0, 0x64, 0x78);
    private static readonly Color BlueColor = Color.FromRgb(0xb9, 0x79, 0x85);
    private static readonly Color GreenColor = Color.FromRgb(0x48, 0xd4, 0x9b);
    private static readonly Color RedColor = Color.FromRgb(0xff, 0x7b, 0x86);
    private static readonly Color AmberColor = Color.FromRgb(0xf1, 0xb8, 0x5b);
    private static readonly double[] ColumnWidths = { 205, 78, 82, 82, 92, 86, 62, 62, 78, 96 };

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
    private readonly Border detailsPanel;
    private Border detailAvatar;
    private TextBlock detailName;
    private TextBlock detailMeta;
    private StackPanel detailBody;
    private readonly Dictionary<string, Button> detailTabs = new();
    private IReadOnlyList<TelemetryPerson> currentUsers = Array.Empty<TelemetryPerson>();
    private TelemetryPerson selectedUser;
    private string selectedDetailTab = "Models";
    private bool passiveExpanded;
    private int selectedRangeDays = 7;
    private bool loading;

    public TelemetryPanel(LatrixApiClient latrix, string apiKey)
    {
        this.latrix = latrix;
        this.apiKey = apiKey;
        Background = new SolidColorBrush(CanvasColor);
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
            Foreground = new SolidColorBrush(AccentColor),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        });
        headingCopy.Children.Add(new TextBlock
        {
            Text = "Team activity",
            Foreground = new SolidColorBrush(TextColor),
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 3),
        });
        headingCopy.Children.Add(new TextBlock
        {
            Text = "A clear view of usage, performance and model activity.",
            Foreground = new SolidColorBrush(MutedColor),
            FontSize = 12,
        });
        heading.Children.Add(headingCopy);

        status = new TextBlock
        {
            Text = "Ready to sync telemetry",
            Foreground = new SolidColorBrush(MutedColor),
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
            Background = new SolidColorBrush(Color.FromArgb(34, AccentColor.R, AccentColor.G, AccentColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, AccentColor.R, AccentColor.G, AccentColor.B)),
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
                        Background = new SolidColorBrush(GreenColor),
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, 7, 0),
                    },
                    new TextBlock
                    {
                        Text = "LIVE SYNC",
                        Foreground = new SolidColorBrush(AccentColor),
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
            Foreground = new SolidColorBrush(DimColor),
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
        summary.Children.Add(CreateSummaryCard(0, "TOTAL TOKENS", totalTokensValue, "Across this period", BlueColor, out _));
        summary.Children.Add(CreateSummaryCard(1, "REQUESTS", requestsValue, "All team members", AccentColor, out _));
        summary.Children.Add(CreateSummaryCard(2, "ACTIVE NOW", activeValue, "Currently online", GreenColor, out _));
        summary.Children.Add(CreateSummaryCard(3, "ERRORS", errorsValue, "Needs attention", RedColor, out errorsAccent));
        summary.Children.Add(CreateSummaryCard(4, "AVG LATENCY", latencyValue, "Across active users", AmberColor, out latencyAccent));
        Grid.SetRow(summary, 2);
        root.Children.Add(summary);

        table = new Border
        {
            Background = new SolidColorBrush(SurfaceColor),
            BorderBrush = new SolidColorBrush(BorderColor),
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

        detailsPanel = CreateDetailsPanel();
        contentArea = new Grid();
        contentArea.ColumnDefinitions.Add(new ColumnDefinition());
        contentArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(332) });
        contentArea.RowDefinitions.Add(new RowDefinition());
        contentArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentArea.Children.Add(table);
        contentArea.Children.Add(detailsPanel);
        Grid.SetColumn(detailsPanel, 1);
        contentArea.SizeChanged += (_, _) => ApplyResponsiveLayout();
        Grid.SetRow(contentArea, 3);
        root.Children.Add(contentArea);
        Content = root;
        UpdateDetailsPanel();

        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout();
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
        status.Foreground = new SolidColorBrush(MutedColor);
        status.Text = "Syncing latest telemetry...";
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No Latrix API key was found.");
            var users = await latrix.ReadTelemetryAsync(apiKey, selectedRangeDays, lifetime.Token);
            Render(users);
            status.Foreground = new SolidColorBrush(MutedColor);
            status.Text = $"{users.Count} people  /  updated {DateTime.Now:HH:mm:ss}  /  auto-refresh every 20s";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            status.Foreground = new SolidColorBrush(RedColor);
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
        if (selectedUser != null)
            selectedUser = ordered.FirstOrDefault(user => user.UserId == selectedUser.UserId);
        var totalTokens = ordered.Sum(user => user.TotalTokens);
        var totalRequests = ordered.Sum(user => user.Requests);
        var totalErrors = ordered.Sum(user => user.Errors);
        var activeUsers = ordered.Count(user => user.Online);
        var latencyUsers = ordered.Where(user => user.AverageLatencyMs > 0).ToArray();
        totalTokensValue.Text = FormatTokens(totalTokens);
        requestsValue.Text = totalRequests.ToString("N0", CultureInfo.InvariantCulture);
        activeValue.Text = activeUsers.ToString(CultureInfo.InvariantCulture);
        errorsValue.Text = totalErrors.ToString("N0", CultureInfo.InvariantCulture);
        errorsValue.Foreground = new SolidColorBrush(totalErrors > 0 ? RedColor : TextColor);
        errorsAccent.Background = new SolidColorBrush(totalErrors > 0 ? RedColor : DimColor);
        latencyValue.Text = latencyUsers.Length == 0
            ? "--"
            : FormatLatency(latencyUsers.Average(user => user.AverageLatencyMs));
        var highLatency = latencyUsers.Any(user => user.AverageLatencyMs >= 15000);
        latencyValue.Foreground = new SolidColorBrush(highLatency ? AmberColor : TextColor);
        latencyAccent.Background = new SolidColorBrush(highLatency ? AmberColor : DimColor);

        RenderRows();
        UpdateDetailsPanel();
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
            Foreground = new SolidColorBrush(MutedColor),
            Background = new SolidColorBrush(Color.FromRgb(0x2b, 0x14, 0x1b)),
            BorderBrush = new SolidColorBrush(BorderColor),
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
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(RaisedColor)));
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
            Background = new SolidColorBrush(header ? HeaderColor : RaisedColor),
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
                Foreground = new SolidColorBrush(header ? MutedColor : TextColor),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }

    private Border CreatePersonRow(TelemetryPerson user, int index)
    {
        var baseColor = index % 2 == 0 ? SurfaceColor : Color.FromRgb(0x2a, 0x14, 0x1b);
        var selected = selectedUser?.UserId == user.UserId;
        var row = new Grid
        {
            MinWidth = ColumnWidths.Sum(),
            MinHeight = 52,
            Background = new SolidColorBrush(selected ? Color.FromRgb(0x5a, 0x23, 0x35) : baseColor),
            Cursor = Cursors.Hand,
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
            Foreground = new SolidColorBrush(TextColor),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(user.Role))
            nameLine.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x42, 0x20, 0x2b)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x75, 0x3c, 0x4b)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(7, 0, 0, 0),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock
                {
                    Text = user.Role.ToUpperInvariant(),
                    Foreground = new SolidColorBrush(BlueColor),
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                },
            });
        identity.Children.Add(nameLine);
        identity.Children.Add(new TextBlock
        {
            Text = user.Online ? "Online" : FormatPresence(user.LastActive),
            Foreground = new SolidColorBrush(user.Online ? GreenColor : DimColor),
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
        AddCell(row, FormatTokens(user.TotalTokens), 5, true, AccentColor);
        AddCell(row, user.Models.ToString("N0", CultureInfo.InvariantCulture), 6);
        AddCell(row, user.Errors.ToString("N0", CultureInfo.InvariantCulture), 7, false, user.Errors > 0 ? RedColor : MutedColor);
        AddCell(row, user.AverageLatencyMs > 0 ? FormatLatency(user.AverageLatencyMs) : "--", 8, false,
            user.AverageLatencyMs >= 15000 ? AmberColor : MutedColor);
        AddCell(row, user.LastActive == "now" ? "Active now" : user.LastActive, 9, false,
            user.Online ? GreenColor : MutedColor, TextAlignment.Left);

        var selectedAccent = new Border
        {
            Width = 3,
            Background = new SolidColorBrush(AccentColor),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
        };
        row.Children.Add(selectedAccent);
        row.MouseEnter += (_, _) =>
        {
            if (selectedUser?.UserId != user.UserId)
                row.Background = new SolidColorBrush(Color.FromRgb(0x45, 0x1d, 0x2a));
        };
        row.MouseLeave += (_, _) =>
        {
            if (selectedUser?.UserId != user.UserId)
                row.Background = new SolidColorBrush(baseColor);
        };
        row.MouseLeftButtonUp += (_, eventArgs) =>
        {
            SelectPerson(user);
            eventArgs.Handled = true;
        };
        return new Border
        {
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
    }

    private void SelectPerson(TelemetryPerson user)
    {
        selectedUser = user;
        RenderRows();
        UpdateDetailsPanel();
    }

    private Border CreateDetailsPanel()
    {
        var shell = new Border
        {
            Background = new SolidColorBrush(SurfaceColor),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
        };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());

        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        detailAvatar = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(0x8f, 0x2e, 0x45)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "?",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            },
        };
        header.Children.Add(detailAvatar);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        detailName = new TextBlock
        {
            Text = "Select a person",
            Foreground = new SolidColorBrush(TextColor),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        detailMeta = new TextBlock
        {
            Text = "Click a row to inspect usage",
            Foreground = new SolidColorBrush(MutedColor),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        identity.Children.Add(detailName);
        identity.Children.Add(detailMeta);
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);
        var clear = CreatePillButton("Clear", Color.FromRgb(0x32, 0x18, 0x21), MutedColor, 52);
        clear.Click += (_, _) =>
        {
            selectedUser = null;
            RenderRows();
            UpdateDetailsPanel();
        };
        Grid.SetColumn(clear, 2);
        header.Children.Add(clear);
        panel.Children.Add(header);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var tab in new[] { "Overview", "Models", "Activity" })
        {
            var button = CreateTabButton(tab);
            button.Click += (_, _) =>
            {
                selectedDetailTab = tab;
                UpdateDetailsPanel();
            };
            detailTabs[tab] = button;
            tabs.Children.Add(button);
        }
        Grid.SetRow(tabs, 1);
        panel.Children.Add(tabs);

        detailBody = new StackPanel();
        var detailScroll = new ScrollViewer
        {
            Content = detailBody,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 2, 0),
        };
        ConfigureScrollViewer(detailScroll);
        Grid.SetRow(detailScroll, 2);
        panel.Children.Add(detailScroll);
        shell.Child = panel;
        return shell;
    }

    private void UpdateDetailsPanel()
    {
        var hasSelection = selectedUser != null;
        detailAvatar.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSelection)
        {
            detailName.Text = "Select a person";
            detailMeta.Text = "Click a row to inspect usage";
            detailBody.Children.Clear();
            detailBody.Children.Add(new TextBlock
            {
                Text = "Member insights will appear here with model usage, effort mix and activity status.",
                Foreground = new SolidColorBrush(MutedColor),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }
        else
        {
            var user = selectedUser;
            ((TextBlock)detailAvatar.Child).Text = GetInitials(user.Name);
            detailAvatar.Background = new SolidColorBrush(GetAvatarColor(user.Name));
            detailName.Text = user.Name;
            detailMeta.Text = string.IsNullOrWhiteSpace(user.Role)
                ? (user.Online ? "Online" : FormatPresence(user.LastActive))
                : $"{user.Role.ToUpperInvariant()}  ·  {(user.Online ? "Online" : FormatPresence(user.LastActive))}";
            detailBody.Children.Clear();
            detailBody.Children.Add(selectedDetailTab switch
            {
                "Models" => CreateModelsBody(user),
                "Activity" => CreateActivityBody(user),
                _ => CreateOverviewBody(user),
            });
        }

        foreach (var pair in detailTabs)
        {
            var selected = pair.Key == selectedDetailTab;
            pair.Value.Background = new SolidColorBrush(selected ? Color.FromRgb(0x8f, 0x2e, 0x45) : Color.FromRgb(0x32, 0x18, 0x21));
            pair.Value.Foreground = new SolidColorBrush(selected ? TextColor : MutedColor);
        }
    }

    private static StackPanel CreateOverviewBody(TelemetryPerson user)
    {
        var body = new StackPanel();
        body.Children.Add(CreateDetailSectionLabel("Usage snapshot"));
        var stats = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        stats.ColumnDefinitions.Add(new ColumnDefinition());
        stats.ColumnDefinitions.Add(new ColumnDefinition());
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddDetailStat(stats, 0, 0, "REQUESTS", user.Requests.ToString("N0", CultureInfo.InvariantCulture));
        AddDetailStat(stats, 1, 0, "TOTAL TOKENS", FormatTokens(user.TotalTokens));
        AddDetailStat(stats, 0, 1, "AVG LATENCY", user.AverageLatencyMs > 0 ? FormatLatency(user.AverageLatencyMs) : "--");
        AddDetailStat(stats, 1, 1, "ERRORS", user.Errors.ToString("N0", CultureInfo.InvariantCulture), user.Errors > 0 ? RedColor : TextColor);
        body.Children.Add(stats);
        body.Children.Add(CreateDetailSectionLabel("Token composition"));
        body.Children.Add(CreateDetailLine("Input", FormatTokens(Math.Max(0, user.InputTokens - user.CachedTokens))));
        body.Children.Add(CreateDetailLine("Output", FormatTokens(user.OutputTokens)));
        body.Children.Add(CreateDetailLine("Reasoning", FormatTokens(user.ReasoningTokens)));
        body.Children.Add(CreateDetailLine("Cached", FormatTokens(user.CachedTokens)));
        return body;
    }

    private static StackPanel CreateModelsBody(TelemetryPerson user)
    {
        var body = new StackPanel();
        body.Children.Add(CreateDetailSectionLabel("Model mix"));
        var maxTokens = user.Breakdown.Count == 0 ? 0 : user.Breakdown.Max(item => item.TotalTokens);
        if (user.Breakdown.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "No model breakdown is available for this member.",
                Foreground = new SolidColorBrush(MutedColor),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            return body;
        }
        foreach (var item in user.Breakdown)
            body.Children.Add(CreateModelCard(item, maxTokens));
        return body;
    }

    private static StackPanel CreateActivityBody(TelemetryPerson user)
    {
        var body = new StackPanel();
        body.Children.Add(CreateDetailSectionLabel("Activity status"));
        body.Children.Add(CreateDetailLine("Current status", user.Online ? "Online" : "Offline", user.Online ? GreenColor : MutedColor));
        body.Children.Add(CreateDetailLine("Last active", user.LastActive == "now" ? "Active now" : user.LastActive));
        body.Children.Add(CreateDetailLine("Models used", user.Models.ToString("N0", CultureInfo.InvariantCulture)));
        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2b, 0x14, 0x1b)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock
            {
                Text = "The activity view reflects the latest telemetry snapshot. Historical event details are not included in this response.",
                Foreground = new SolidColorBrush(MutedColor),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
            },
        });
        return body;
    }

    private static Border CreateModelCard(TelemetryBreakdown item, long maxTokens)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2b, 0x14, 0x1b)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x75, 0x3c, 0x4b)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 10, 11, 9),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var body = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = item.Model,
            Foreground = new SolidColorBrush(TextColor),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var total = new TextBlock
        {
            Text = FormatTokens(item.TotalTokens),
            Foreground = new SolidColorBrush(AccentColor),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
        };
        Grid.SetColumn(total, 1);
        top.Children.Add(total);
        body.Children.Add(top);

        var ratio = maxTokens <= 0 ? 0 : Math.Clamp((double)item.TotalTokens / maxTokens, 0, 1);
        var bar = new Grid { Height = 5, Margin = new Thickness(0, 9, 0, 9) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(ratio, 0.02), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - ratio, 0), GridUnitType.Star) });
        bar.Children.Add(new Border { Background = new SolidColorBrush(AccentColor), CornerRadius = new CornerRadius(3) });
        var remainder = new Border { Background = new SolidColorBrush(Color.FromRgb(0x4a, 0x24, 0x2e)), CornerRadius = new CornerRadius(3) };
        Grid.SetColumn(remainder, 1);
        bar.Children.Add(remainder);
        body.Children.Add(bar);
        var info = new WrapPanel { Orientation = Orientation.Horizontal };
        info.Children.Add(new TextBlock
        {
            Text = $"{item.Requests:N0} requests",
            Foreground = new SolidColorBrush(MutedColor),
            FontSize = 10,
            Margin = new Thickness(0, 2, 6, 0),
        });
        foreach (var effort in ParseEfforts(item.Efforts))
            info.Children.Add(CreateEffortChip(effort));
        body.Children.Add(info);
        card.Child = body;
        return card;
    }

    private static IEnumerable<(string Level, string Count)> ParseEfforts(string efforts)
    {
        foreach (var item in (efforts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2) yield return (parts[0], parts[1]);
        }
    }

    private static Border CreateEffortChip((string Level, string Count) effort)
    {
        var color = GetEffortColor(effort.Level);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(35, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(85, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(6, -3, 0, 0),
            Child = new TextBlock
            {
                Text = $"{effort.Level.ToUpperInvariant()} {effort.Count}",
                Foreground = new SolidColorBrush(color),
                FontSize = 8,
                FontWeight = FontWeights.SemiBold,
            },
        };
    }

    private static Color GetEffortColor(string level) =>
        level?.ToLowerInvariant() switch
        {
            "xhigh" => RedColor,
            "high" => AmberColor,
            "medium" => BlueColor,
            _ => MutedColor,
        };

    private static TextBlock CreateDetailSectionLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Foreground = new SolidColorBrush(DimColor),
        FontSize = 9,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static void AddDetailStat(Grid grid, int column, int row, string label, string value, Color? color = null)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2b, 0x14, 0x1b)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 8, 9, 8),
            Margin = new Thickness(column == 0 ? 0 : 4, row == 0 ? 0 : 4, column == 1 ? 0 : 4, row == 1 ? 0 : 4),
        };
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(DimColor),
            FontSize = 8,
            FontWeight = FontWeights.SemiBold,
        });
        copy.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = new SolidColorBrush(color ?? TextColor),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 0, 0),
        });
        card.Child = copy;
        Grid.SetColumn(card, column);
        Grid.SetRow(card, row);
        grid.Children.Add(card);
    }

    private static Border CreateDetailLine(string label, string value, Color? color = null)
    {
        var line = new Grid { MinHeight = 28 };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(MutedColor),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var valueText = new TextBlock
        {
            Text = value,
            Foreground = new SolidColorBrush(color ?? TextColor),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueText, 1);
        line.Children.Add(valueText);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4a, 0x24, 0x2e)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = line,
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
            Foreground = new SolidColorBrush(color ?? TextColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, column);
    }

    private static void AddColumns(Grid grid)
    {
        foreach (var width in ColumnWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
    }

    private void ApplyResponsiveLayout()
    {
        if (contentArea == null) return;
        var sidePanel = ActualWidth >= 1120;
        contentArea.ColumnDefinitions[1].Width = sidePanel ? new GridLength(332) : new GridLength(0);
        contentArea.RowDefinitions[1].Height = sidePanel ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(table, 0);
        Grid.SetRow(table, 0);
        Grid.SetColumnSpan(table, sidePanel ? 1 : 2);
        Grid.SetColumn(detailsPanel, sidePanel ? 1 : 0);
        Grid.SetRow(detailsPanel, sidePanel ? 0 : 1);
        Grid.SetColumnSpan(detailsPanel, sidePanel ? 1 : 2);
        detailsPanel.Margin = sidePanel ? new Thickness(14, 0, 0, 0) : new Thickness(0, 14, 0, 0);
    }

    private static void ConfigureScrollViewer(ScrollViewer scroll)
    {
        var thumbTemplate = new ControlTemplate(typeof(Thumb));
        var thumbBorder = new FrameworkElementFactory(typeof(Border));
        thumbBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        thumbBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        thumbTemplate.VisualTree = thumbBorder;
        var thumbStyle = new Style(typeof(Thumb));
        thumbStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x7b, 0x35, 0x49))));
        thumbStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
        thumbStyle.Setters.Add(new Setter(Control.TemplateProperty, thumbTemplate));
        var thumbHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        thumbHover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(AccentColor)));
        thumbStyle.Triggers.Add(thumbHover);

        var scrollTemplate = new ControlTemplate(typeof(ScrollBar));
        var track = new FrameworkElementFactory(typeof(TelemetryScrollBarTrack));
        track.Name = "PART_Track";
        track.SetValue(Track.OrientationProperty, Orientation.Vertical);
        track.SetValue(Track.IsDirectionReversedProperty, true);
        scrollTemplate.VisualTree = track;
        var scrollStyle = new Style(typeof(ScrollBar));
        scrollStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, 10d));
        scrollStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x20, 0x0e, 0x13))));
        scrollStyle.Setters.Add(new Setter(Control.TemplateProperty, scrollTemplate));
        scroll.Resources.Add(typeof(Thumb), thumbStyle);
        scroll.Resources.Add(typeof(ScrollBar), scrollStyle);
    }

    private static Button CreateFilterButton(string text, bool selected)
    {
        var button = CreatePillButton(text, selected ? Color.FromRgb(0x8f, 0x2e, 0x45) : SurfaceColor,
            selected ? TextColor : MutedColor, 48);
        button.Margin = new Thickness(0, 0, 1, 0);
        return button;
    }

    private static Button CreateTabButton(string text) =>
        CreatePillButton(text, Color.FromRgb(0x32, 0x18, 0x21), MutedColor, 74);

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
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2d, 0x4a, 0x64)),
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
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(AccentColor)));
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
                        ? Color.FromRgb(0x8f, 0x2e, 0x45) : SurfaceColor);
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
            Foreground = new SolidColorBrush(TextColor),
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
            Foreground = new SolidColorBrush(DimColor),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(value);
        content.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = new SolidColorBrush(MutedColor),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
        });
        var card = new Grid();
        card.Children.Add(new Border
        {
            Background = new SolidColorBrush(SurfaceColor),
            BorderBrush = new SolidColorBrush(BorderColor),
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
            Background = new SolidColorBrush(Color.FromRgb(0x8f, 0x2e, 0x45)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xd0, 0x64, 0x78)),
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
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xb4, 0x4c, 0x63))));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x61, 0x1e, 0x2e))));
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
            Foreground = new SolidColorBrush(MutedColor),
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
        var colors = new[] { BlueColor, AccentColor, Color.FromRgb(0x9d, 0x5d, 0x72), Color.FromRgb(0xc2, 0x6a, 0x66) };
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
            Background = new SolidColorBrush(Color.FromRgb(0x7b, 0x35, 0x49)),
            MinHeight = 28,
        };
    }
}
