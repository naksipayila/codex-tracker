using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexUsageTray;

internal sealed class TelemetryPanel : UserControl, IDisposable
{
    private static readonly Color RowColor = Color.FromRgb(0x19, 0x2a, 0x3d);
    private static readonly Color HeaderColor = Color.FromRgb(0x16, 0x22, 0x30);
    private static readonly Color BorderColor = Color.FromRgb(0x2d, 0x45, 0x5e);
    private static readonly Color MutedColor = Color.FromRgb(0x8e, 0xa9, 0xc7);
    private static readonly Color TextColor = Color.FromRgb(0xe9, 0xf4, 0xfc);
    private readonly LatrixApiClient latrix;
    private readonly string apiKey;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox range;
    private readonly StackPanel rows;
    private readonly TextBlock status;
    private readonly Button refresh;
    private bool loading;

    public TelemetryPanel(LatrixApiClient latrix, string apiKey)
    {
        this.latrix = latrix;
        this.apiKey = apiKey;
        Margin = new Thickness(0, 0, 14, 0);
        MinWidth = 700;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        toolbar.Children.Add(new TextBlock
        {
            Text = "BY PERSON",
            Foreground = new SolidColorBrush(MutedColor),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        refresh = CreateButton("Refresh", RefreshAsync);
        DockPanel.SetDock(refresh, Dock.Right);
        toolbar.Children.Add(refresh);
        range = new ComboBox
        {
            Width = 82,
            Height = 26,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 11,
        };
        range.Items.Add("1 day");
        range.Items.Add("7 days");
        range.Items.Add("14 days");
        range.Items.Add("30 days");
        range.SelectedIndex = 1;
        range.SelectionChanged += (_, _) => _ = RefreshAsync();
        DockPanel.SetDock(range, Dock.Right);
        toolbar.Children.Add(range);
        root.Children.Add(toolbar);

        status = new TextBlock
        {
            Foreground = new SolidColorBrush(MutedColor),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 5),
        };
        Grid.SetRow(status, 1);
        root.Children.Add(status);

        rows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = rows,
            Height = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
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
        status.Text = "Loading...";
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No Latrix API key was found.");
            var days = range.SelectedIndex switch { 0 => 1, 2 => 14, 3 => 30, _ => 7 };
            var users = await latrix.ReadTelemetryAsync(apiKey, days, lifetime.Token);
            Render(users);
            status.Text = $"{users.Count} people · updated {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            status.Text = error.Message;
        }
        finally
        {
            loading = false;
            refresh.IsEnabled = true;
        }
    }

    private void Render(IReadOnlyList<TelemetryPerson> users)
    {
        rows.Children.Clear();
        rows.Children.Add(CreateHeader());
        foreach (var user in users.OrderByDescending(user => user.TotalTokens))
            rows.Children.Add(CreatePersonRow(user));
        if (users.Count == 0)
            rows.Children.Add(new TextBlock { Text = "No telemetry data for this period.", Margin = new Thickness(10), Foreground = new SolidColorBrush(MutedColor), FontSize = 11 });
    }

    private static Grid CreateHeader()
    {
        return CreateGrid(new[] { "Person", "Requests", "Input", "Output", "Reasoning", "Total", "Models", "Errors", "Latency", "Last active" }, true);
    }

    private static Grid CreateGrid(string[] values, bool header)
    {
        var grid = new Grid
        {
            MinWidth = 700,
            Height = header ? 28 : 30,
            Background = new SolidColorBrush(header ? HeaderColor : RowColor),
        };
        var widths = new[] { 175, 62, 62, 62, 70, 70, 52, 52, 66, 75 };
        foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        for (var i = 0; i < values.Length; i++)
        {
            var text = new TextBlock
            {
                Text = values[i],
                Margin = new Thickness(i == 0 ? 8 : 3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(header ? MutedColor : TextColor),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }

    private static Border CreatePersonRow(TelemetryPerson user)
    {
        var container = new StackPanel();
        var row = new Grid
        {
            MinWidth = 700,
            MinHeight = 30,
            Background = new SolidColorBrush(RowColor),
        };
        var widths = new[] { 175, 62, 62, 62, 70, 70, 52, 52, 66, 75 };
        foreach (var width in widths) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });

        var person = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        if (user.Online)
            person.Children.Add(new TextBlock { Text = "●", Foreground = new SolidColorBrush(Color.FromRgb(0x2b, 0xa5, 0x70)), FontSize = 10, Margin = new Thickness(0, 0, 5, 0) });
        person.Children.Add(new TextBlock { Text = user.Name, Foreground = new SolidColorBrush(TextColor), FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(user.Role))
            person.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0xe8, 0xea, 0xec)), CornerRadius = new CornerRadius(3), Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(4, 1, 4, 1), Child = new TextBlock { Text = user.Role, Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), FontSize = 8, FontWeight = FontWeights.Bold } });
        AddCell(row, person, 0);
        AddCell(row, user.Requests.ToString("N0"), 1);
        AddCell(row, FormatTokens(Math.Max(0, user.InputTokens - user.CachedTokens)), 2);
        AddCell(row, FormatTokens(user.OutputTokens), 3);
        AddCell(row, FormatTokens(user.ReasoningTokens), 4);
        AddCell(row, FormatTokens(user.TotalTokens), 5, true);
        AddCell(row, user.Models.ToString("N0"), 6);
        AddCell(row, user.Errors.ToString("N0"), 7, false, user.Errors > 0 ? Color.FromRgb(0xe0, 0x5d, 0x5d) : TextColor);
        AddCell(row, user.AverageLatencyMs > 0 ? $"{user.AverageLatencyMs / 1000:0.0}s" : "—", 8);
        AddCell(row, string.IsNullOrWhiteSpace(user.LastActive) ? "—" : user.LastActive, 9);
        container.Children.Add(row);

        if (user.Breakdown.Count > 0)
        {
            var details = new StackPanel { Margin = new Thickness(20, 2, 0, 5), Visibility = Visibility.Collapsed };
            foreach (var item in user.Breakdown)
                details.Children.Add(new TextBlock { Text = $"{item.Model}  ·  {FormatTokens(item.TotalTokens)} total  ·  {item.Requests:N0} requests" + (string.IsNullOrWhiteSpace(item.Efforts) ? "" : $"  ·  {item.Efforts}"), Foreground = new SolidColorBrush(Color.FromRgb(0xae, 0xd0, 0xf7)), FontSize = 9, Margin = new Thickness(0, 2, 0, 2) });
            row.MouseLeftButtonUp += (_, _) => details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            container.Children.Add(details);
        }
        return new Border { Child = container, BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1, 0, 1, 1) };
    }

    private static void AddCell(Grid row, string text, int column, bool bold = false, Color? color = null)
    {
        AddCell(row, new TextBlock
        {
            Text = text,
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(color ?? TextColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, column);
    }

    private static void AddCell(Grid row, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        row.Children.Add(element);
    }

    private static Button CreateButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Height = 26, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x68, 0x8a)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x3e, 0x9d, 0xc2)), FontSize = 10 };
        button.Click += async (_, _) => await action();
        return button;
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
