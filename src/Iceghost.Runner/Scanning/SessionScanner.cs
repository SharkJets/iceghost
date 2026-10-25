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
    const long BigFile = 4 << 20, TailBytes = 1 << 20;
    const int HeadLines = 50;
    readonly Dictionary<string, (DateTime Mtime, long Len, SessionSnapshot? Snap)> cache = new();

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
        var picked = files.OrderByDescending(f => f.Mtime).Take(MaxFiles).ToList();
        var keep = picked.Select(f => f.Path).ToHashSet();
        foreach (var k in cache.Keys.Where(k => !keep.Contains(k)).ToList()) cache.Remove(k);
        foreach (var f in picked)
        {
            try
            {
                var len = new FileInfo(f.Path).Length;
                if (!cache.TryGetValue(f.Path, out var c) || c.Mtime != f.Mtime || c.Len != len)
                {
                    c = (f.Mtime, len, read(f.Rt, ReadLines(f.Path, len)));
                    cache[f.Path] = c;
                }
                if (c.Snap is { } s)
                    reports.Add(new(s.Runtime, s.SessionId, s.Cwd, s.Title, StateClassifier.Classify(s, now), s.LastActivity, s.Tail));
            }
            catch (IOException) { } // ponytail: file vanished or unreadable, skipped until next scan
            catch (UnauthorizedAccessException) { }
        }
        return reports;
    }

    static List<string> ReadLines(string path, long len)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var lines = new List<string>();
        if (len <= BigFile)
        {
            using var sr = new StreamReader(fs);
            while (sr.ReadLine() is { } l) lines.Add(l);
            return lines;
        }
        using (var head = new StreamReader(fs, leaveOpen: true))
            for (var i = 0; i < HeadLines && head.ReadLine() is { } l; i++) lines.Add(l);
        fs.Seek(-TailBytes, SeekOrigin.End);
        using var tail = new StreamReader(fs);
        tail.ReadLine(); // partial first line of the chunk
        while (tail.ReadLine() is { } l) lines.Add(l);
        return lines;
    }
}
