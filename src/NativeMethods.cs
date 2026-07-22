using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace CodexUsageTray;

internal static class NativeMethods
{
    private const int TaskbarControlMargin = 8;
    private const int ReadableWidgetWidth = 120;
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExAppWindow = 0x00040000L;
    public const long WsExNoActivate = 0x08000000L;
    public const int WmMouseActivate = 0x0021;
    public const int MaNoActivate = 3;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize
    );

    public static IntPtr GetWindowLongPtr(IntPtr window, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));
    }

    public static void SetWindowLongPtr(IntPtr window, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(window, index, value);
        else SetWindowLong32(window, index, value.ToInt32());
    }

    public static void ApplyDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var darkMode = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        var captionColor = ToColorRef(Theme.Background);
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));

        var textColor = ToColorRef(Theme.TextPrimary);
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
    }

    private static int ToColorRef(System.Windows.Media.Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    public static double GetScaleForWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return 1;
        try
        {
            var dpi = GetDpiForWindow(handle);
            return dpi == 0 ? 1 : dpi / 96.0;
        }
        catch { return 1; }
    }

    public static TaskbarWidgetPlacement FindTaskbarWidgetPlacement(
        int requestedLeft,
        int screenLeft,
        int screenRight,
        int taskbarTop,
        int taskbarBottom,
        int preferredWidth
    )
    {
        return CalculateTaskbarWidgetPlacement(
            requestedLeft,
            screenLeft,
            screenRight,
            preferredWidth,
            GetTaskbarOccupiedRanges(screenLeft, screenRight, taskbarTop, taskbarBottom)
        );
    }

    internal static TaskbarWidgetPlacement CalculateTaskbarWidgetPlacement(
        int requestedLeft,
        int screenLeft,
        int screenRight,
        int preferredWidth,
        IEnumerable<HorizontalRange> occupiedRanges
    )
    {
        if (screenRight <= screenLeft) return new TaskbarWidgetPlacement(screenLeft, 1);

        var occupied = new List<HorizontalRange>();
        foreach (var range in occupiedRanges)
        {
            var left = Math.Clamp(range.Left, screenLeft, screenRight);
            var right = Math.Clamp(range.Right, screenLeft, screenRight);
            if (right > left) occupied.Add(new HorizontalRange(left, right));
        }
        occupied.Sort((first, second) => first.Left.CompareTo(second.Left));

        var best = new TaskbarWidgetPlacement(screenLeft, 0);
        var foundReadable = false;
        var cursor = screenLeft;
        foreach (var range in occupied)
        {
            if (range.Left > cursor)
                ConsiderFreeRange(cursor, range.Left, requestedLeft, preferredWidth, ref best, ref foundReadable);
            cursor = Math.Max(cursor, range.Right);
        }
        if (cursor < screenRight)
            ConsiderFreeRange(cursor, screenRight, requestedLeft, preferredWidth, ref best, ref foundReadable);

        if (best.Width > 0) return best;
        return new TaskbarWidgetPlacement(Math.Clamp(requestedLeft, screenLeft, screenRight - 1), 1);
    }

    private static void ConsiderFreeRange(
        int left,
        int right,
        int requestedLeft,
        int preferredWidth,
        ref TaskbarWidgetPlacement best,
        ref bool foundReadable
    )
    {
        var availableWidth = right - left;
        if (availableWidth <= 0) return;
        var readable = availableWidth >= ReadableWidgetWidth;
        if (foundReadable && !readable) return;
        if (readable && !foundReadable)
        {
            best = new TaskbarWidgetPlacement(left, 0);
            foundReadable = true;
        }

        var width = Math.Min(Math.Max(1, preferredWidth), availableWidth);
        var candidateLeft = Math.Clamp(requestedLeft, left, right - width);
        if (best.Width == 0 ||
            Math.Abs(candidateLeft - requestedLeft) < Math.Abs(best.Left - requestedLeft) ||
            (Math.Abs(candidateLeft - requestedLeft) == Math.Abs(best.Left - requestedLeft) && width > best.Width))
        {
            best = new TaskbarWidgetPlacement(candidateLeft, width);
        }
    }

    private static IEnumerable<HorizontalRange> GetTaskbarOccupiedRanges(
        int screenLeft,
        int screenRight,
        int taskbarTop,
        int taskbarBottom
    )
    {
        var occupied = new List<HorizontalRange>();
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return occupied;
            var root = AutomationElement.FromHandle(taskbar);
            var elements = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            foreach (AutomationElement element in elements)
            {
                var controlType = element.Current.ControlType;
                if (controlType != ControlType.Button && controlType != ControlType.ComboBox &&
                    controlType != ControlType.Edit && controlType != ControlType.ListItem &&
                    controlType != ControlType.MenuItem)
                {
                    continue;
                }
                if (element.Current.IsOffscreen) continue;
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Bottom <= taskbarTop || bounds.Top >= taskbarBottom) continue;
                var left = Math.Max(screenLeft, (int)Math.Floor(bounds.Left) - TaskbarControlMargin);
                var right = Math.Min(screenRight, (int)Math.Ceiling(bounds.Right) + TaskbarControlMargin);
                if (right > left) occupied.Add(new HorizontalRange(left, right));
            }
        }
        catch
        {
            // UI Automation can be temporarily unavailable while Explorer rebuilds the taskbar.
        }
        return occupied;
    }
}

internal readonly struct HorizontalRange
{
    public int Left { get; }
    public int Right { get; }

    public HorizontalRange(int left, int right)
    {
        Left = left;
        Right = right;
    }
}

internal readonly struct TaskbarWidgetPlacement
{
    public int Left { get; }
    public int Width { get; }

    public TaskbarWidgetPlacement(int left, int width)
    {
        Left = left;
        Width = width;
    }
}
