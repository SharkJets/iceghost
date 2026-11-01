using System.Runtime.InteropServices;
using Iceghost.Runner.Adapters;
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

    // Linux/macOS from an interactive terminal: relaunch detached so the shell gets its prompt back. Never under systemd/launchd, in the detached child, or with --foreground.
    public static bool ShouldDetach(bool isWindows, bool interactive, System.Collections.IDictionary env, bool foregroundFlag) =>
        !isWindows && interactive && !foregroundFlag && !env.Contains("INVOCATION_ID") && !env.Contains("XPC_SERVICE_NAME") && env["ICEGHOST_DETACHED"] as string != "1";

    public static (string file, string[] args) DetachCommand(bool isMac, string? setsidPath, string exe, IReadOnlyList<string> args) =>
        !isMac && setsidPath is not null ? (setsidPath, [exe, .. args]) : ("nohup", [exe, .. args]);

    // Drops --server/--token and their values (same pairing as Program.ParseFlags) so the daemon's argv carries no secret.
    public static string[] StripEnrollment(IReadOnlyList<string> args)
    {
        var o = new List<string>(args.Take(1)); // the "run" verb
        for (var i = 1; i < args.Count; i++)
            if (args[i] is "--headless" or "--foreground") o.Add(args[i]);
            else if (args[i] is "--server" or "--token") i++;
            else { o.Add(args[i]); if (i + 1 < args.Count) o.Add(args[++i]); }
        return [.. o];
    }

    // Starts the detached child with stdio piped away (closed here) and returns its pid.
    public static int Detach(IReadOnlyList<string> args)
    {
        var (file, a) = DetachCommand(OperatingSystem.IsMacOS(), ProcessAdapter.ResolveExecutable("setsid"), Environment.ProcessPath!, args);
        var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        psi.Environment["ICEGHOST_DETACHED"] = "1";
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.Close();
        return p.Id;
    }

    public static void Enable(string dir)
    {
        var log = TextWriter.Synchronized(OpenLog(dir, 10 * 1024 * 1024));
        Console.SetOut(log);
        Console.SetError(log);
        if (OperatingSystem.IsWindows()) FreeConsole();
    }
}
