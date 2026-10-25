using Iceghost.Contracts;

namespace Iceghost.Runner.Scanning;

public sealed record ScanRoots(string ClaudeProjects, string CodexSessions, string PiSessions)
{
    public static ScanRoots Default()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var codex = Environment.GetEnvironmentVariable("CODEX_HOME");
        return new(
            Path.Combine(string.IsNullOrEmpty(claude) ? Path.Combine(home, ".claude") : claude, "projects"),
            Path.Combine(string.IsNullOrEmpty(codex) ? Path.Combine(home, ".codex") : codex, "sessions"),
            Path.Combine(home, ".pi", "agent", "sessions"));
    }
}

public sealed class SessionScanner(ScanRoots roots, Func<Runtime, IReadOnlyList<string>, SessionSnapshot?> read)
{
    const int MaxFiles = 50;
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    public IReadOnlyList<SessionReport> Scan(DateTimeOffset now)
    {
        var files = new List<(Runtime Rt, string Path, DateTime Mtime)>();
        foreach (var (rt, root) in new[] { (Runtime.Claude, roots.ClaudeProjects), (Runtime.Codex, roots.CodexSessions), (Runtime.Pi, roots.PiSessions) })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var p in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    if (p.Contains("subagents", StringComparison.OrdinalIgnoreCase)) continue;
                    var m = File.GetLastWriteTimeUtc(p);
                    if (now - new DateTimeOffset(m, TimeSpan.Zero) > MaxAge) continue;
                    files.Add((rt, p, m));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var reports = new List<SessionReport>();
        foreach (var f in files.OrderByDescending(f => f.Mtime).Take(MaxFiles))
        {
            try
            {
                var lines = ReadLines(f.Path);
                if (read(f.Rt, lines) is { } s)
                    reports.Add(new(s.Runtime, s.SessionId, s.Cwd, s.Title, StateClassifier.Classify(s, now), s.LastActivity, s.Tail));
            }
            catch (IOException) { } // ponytail: file vanished or unreadable, skipped until next scan
            catch (UnauthorizedAccessException) { }
        }
        return reports;
    }

    static List<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } l) lines.Add(l);
        return lines;
    }
}
