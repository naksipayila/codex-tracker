using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex Usage Tray")]
[assembly: AssemblyDescription("Codex Usage Tray launcher")]
[assembly: AssemblyProduct("Codex Usage Tray")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--pin-hwnd" && args[2] == "--parent-pid")
        {
            PinWindow(args[1], args[3], Array.IndexOf(args, "--hide-in-fullscreen") >= 0);
            return;
        }

        var projectDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src");
        var electron = Path.Combine(projectDirectory, "node_modules", "electron", "dist", "electron.exe");

        if (!File.Exists(electron) && !RunAndWait(projectDirectory, "npm install"))
        {
            MessageBox.Show(
                "Widget dependencies could not be installed. Make sure Node.js is installed, then try again.",
                "Codex Usage Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        try
        {
            Process.Start(CreateCommand(projectDirectory, "npm start"));
        }
        catch (Exception error)
        {
            MessageBox.Show(
                "The widget could not be started.\n\n" + error.Message,
                "Codex Usage Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private static void PinWindow(string windowHandleText, string parentProcessIdText, bool hideInFullscreen)
    {
        long windowHandleValue;
        int parentProcessId;
        if (!long.TryParse(windowHandleText, out windowHandleValue) || !int.TryParse(parentProcessIdText, out parentProcessId)) return;

        pinnedWindow = new IntPtr(windowHandleValue);
        pinnedParentProcessId = parentProcessId;
        hideInFullscreenApps = hideInFullscreen;
        winEventCallback = HandleWinEvent;
        foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            winEventCallback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess
        );
        objectHook = SetWinEventHook(
            EventObjectCreate,
            EventObjectReorder,
            IntPtr.Zero,
            winEventCallback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess
        );

        var fallbackTimer = new System.Windows.Forms.Timer { Interval = 250 };
        fallbackTimer.Tick += delegate
        {
            if (!IsWindow(pinnedWindow) || !IsParentProcessRunning())
            {
                fallbackTimer.Stop();
                Application.ExitThread();
                return;
            }
            UpdatePinnedWindow();
        };

        try
        {
            UpdatePinnedWindow();
            fallbackTimer.Start();
            Application.Run();
        }
        finally
        {
            fallbackTimer.Dispose();
            if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
            if (objectHook != IntPtr.Zero) UnhookWinEvent(objectHook);
        }
    }

    private static bool IsParentProcessRunning()
    {
        try
        {
            using (var process = Process.GetProcessById(pinnedParentProcessId))
            {
                return !process.HasExited;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void HandleWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime
    )
    {
        try
        {
            if (eventType == EventSystemForeground ||
                eventType == EventObjectReorder ||
                (objectId == ObjectIdWindow && eventType == EventObjectShow) ||
                IsTaskbarWindow(windowHandle))
            {
                UpdatePinnedWindow();
            }
        }
        catch
        {
            // The fallback timer will retry if a transient shell event cannot be handled.
        }
    }

    private static bool IsTaskbarWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return false;
        var className = new StringBuilder(64);
        if (GetClassName(windowHandle, className, className.Capacity) == 0) return false;
        var windowClass = className.ToString();
        return windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd" ||
            windowClass == "Progman" || windowClass == "WorkerW";
    }

    private static void UpdatePinnedWindow()
    {
        if (hideInFullscreenApps && IsFullscreenForegroundWindow())
        {
            if (!widgetHiddenForFullscreen)
            {
                ShowWindow(pinnedWindow, SwHide);
                widgetHiddenForFullscreen = true;
            }
            return;
        }

        if (widgetHiddenForFullscreen)
        {
            ShowWindow(pinnedWindow, SwShowNoActivate);
            widgetHiddenForFullscreen = false;
        }
        RaisePinnedWindow();
    }

    private static bool IsFullscreenForegroundWindow()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || foregroundWindow == pinnedWindow ||
            !IsWindowVisible(foregroundWindow) || IsTaskbarWindow(foregroundWindow))
        {
            return false;
        }

        var monitor = MonitorFromWindow(foregroundWindow, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var monitorInfo = new MonitorInfo();
        monitorInfo.cbSize = Marshal.SizeOf(monitorInfo);
        WindowRect windowRect;
        return GetMonitorInfo(monitor, ref monitorInfo) && GetWindowRect(foregroundWindow, out windowRect) &&
            windowRect.Left <= monitorInfo.rcMonitor.Left &&
            windowRect.Top <= monitorInfo.rcMonitor.Top &&
            windowRect.Right >= monitorInfo.rcMonitor.Right &&
            windowRect.Bottom >= monitorInfo.rcMonitor.Bottom;
    }

    private static void RaisePinnedWindow()
    {
        if (raisingPinnedWindow || !IsWindow(pinnedWindow)) return;
        raisingPinnedWindow = true;
        try
        {
            // Keep the widget at the top of the topmost band without taking focus.
            SetWindowPos(
                pinnedWindow,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging
            );
        }
        finally
        {
            raisingPinnedWindow = false;
        }
    }

    private static bool RunAndWait(string projectDirectory, string command)
    {
        try
        {
            using (var process = Process.Start(CreateCommand(projectDirectory, command)))
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateCommand(string projectDirectory, string command)
    {
        return new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /c " + command,
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
    }

    private static readonly IntPtr HwndTopmost = new IntPtr(-1);
    private static IntPtr pinnedWindow;
    private static int pinnedParentProcessId;
    private static bool raisingPinnedWindow;
    private static bool hideInFullscreenApps;
    private static bool widgetHiddenForFullscreen;
    private static WinEventDelegate winEventCallback;
    private static IntPtr foregroundHook;
    private static IntPtr objectHook;

    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectReorder = 0x8004;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime
    );

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public WindowRect rcMonitor;
        public WindowRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate eventHook,
        uint processId,
        uint threadId,
        uint flags
    );

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr eventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
}
