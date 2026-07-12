using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex Usage Tray")]
[assembly: AssemblyDescription("Codex Usage Tray launcher")]
[assembly: AssemblyProduct("Codex Usage Tray")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var projectDirectory = AppDomain.CurrentDomain.BaseDirectory;
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
}
