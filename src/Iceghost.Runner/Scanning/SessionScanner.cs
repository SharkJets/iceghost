using Iceghost.Contracts;

namespace Iceghost.Runner.Scanning;

public sealed record ScanRoots(IReadOnlyList<string> Claude, IReadOnlyList<string> Codex, IReadOnlyList<string> Pi)
{
    public ScanRoots(string claudeProjects, string codexSessions, string piSessions)
        : this([claudeProjects], [codexSessions], [piSessions]) { }

    public IReadOnlyList<string> For(Runtime rt) => rt switch
    {
        Runtime.Claude => Claude,
        Runtime.Codex => Codex,
        _ => Pi,
    };

    /// <summary>Env-var defaults; a runtime present in <paramref name="overrides"/> (non-empty) replaces its default.</summary>
    public static ScanRoots Default(IReadOnlyDictionary<Runtime, string[]>? overrides = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";
        var claude = Env("CLAUDE_CONFIG_DIR");
        var codex = Env("CODEX_HOME");
        IReadOnlyList<string> Pick(Runtime rt, Func<IReadOnlyList<string>> dflt) =>
            overrides is not null && overrides.TryGetValue(rt, out var o) && o.Length > 0 ? o.Select(p => Expand(p, home)).ToList() : dflt();
        return new(
            Pick(Runtime.Claude, () => [Path.Combine(claude == "" ? Path.Combine(home, ".claude") : claude, "projects")]),
            Pick(Runtime.Codex, () => [Path.Combine(codex == "" ? Path.Combine(home, ".codex") : codex, "sessions")]),
            Pick(Runtime.Pi, () => PiRoots(home)));
    }

    static List<string> PiRoots(string home)
    {
        var dir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        var agent = string.IsNullOrEmpty(dir) ? Path.Combine(home, ".pi", "agent") : Expand(dir, home);
        var sess = Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR");
        var roots = new List<string> { string.IsNullOrEmpty(sess) ? Path.Combine(agent, "sessions") : Expand(sess, home) };
        try
        {
            var settings = Path.Combine(agent, "settings.json");
            if (File.Exists(settings)
                && System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings), new() { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip })
                    .RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } o
                && o.TryGetProperty("sessionDir", out var sd) && sd.ValueKind == System.Text.Json.JsonValueKind.String
                && sd.GetString() is { Length: > 0 } s)
            {
                var full = Expand(s, home);
                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { } // bad settings.json: keep the default root
        return roots;
    }

    static string Expand(string p, string home) =>
        p == "~" ? home : p.StartsWith("~/") || p.StartsWith("~\\") ? Path.Combine(home, p[2..]) : p;
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
        foreach (var (rt, root) in new[] { Runtime.Claude, Runtime.Codex, Runtime.Pi }.SelectMany(r => roots.For(r).Select(p => (r, p))))
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
                    reports.Add(new(s.Runtime, s.SessionId, s.Cwd, s.Title, StateClassifier.Classify(s, now), s.LastActivity, s.Tail) { Permission = s.Permission });
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
