using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

internal static class ContainedChild
{
    private static void Main(string[] args)
    {
        File.WriteAllText(args[0], Process.GetCurrentProcess().Id.ToString(), new UTF8Encoding(false));
        Thread.Sleep(30000);
    }
}
