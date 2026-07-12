using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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
        if (args.Length == 4 && args[0] == "--pin-hwnd" && args[2] == "--parent-pid")
        {
            PinWindow(args[1], args[3]);
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

    private static void PinWindow(string windowHandleText, string parentProcessIdText)
    {
        long windowHandleValue;
        int parentProcessId;
        if (!long.TryParse(windowHandleText, out windowHandleValue) || !int.TryParse(parentProcessIdText, out parentProcessId)) return;

        var windowHandle = new IntPtr(windowHandleValue);
        while (IsWindow(windowHandle))
        {
            try
            {
                Process.GetProcessById(parentProcessId);
            }
            catch
            {
                return;
            }

            // Electron owns placement; this helper only keeps its transparent HWND above the taskbar.
            SetWindowPos(windowHandle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            Thread.Sleep(75);
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
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
