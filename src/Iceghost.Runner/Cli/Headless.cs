using System.Runtime.InteropServices;
using System.Text;

namespace Iceghost.Runner.Cli;

public static class Headless
{
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    // Opens <dir>/runner.log for append (rotating to runner.log.1 past maxBytes) and writes the started line.
    public static StreamWriter OpenLog(string dir, long maxBytes)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "runner.log");
        if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
            File.Move(path, path + ".1", overwrite: true);
        var w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        w.WriteLine($"--- iceghost-runner started {DateTime.UtcNow:O} (pid {Environment.ProcessId}) ---");
        return w;
    }

    public static void Enable(string dir)
    {
        var log = TextWriter.Synchronized(OpenLog(dir, 10 * 1024 * 1024));
        Console.SetOut(log);
        Console.SetError(log);
        if (OperatingSystem.IsWindows()) FreeConsole();
    }
}
