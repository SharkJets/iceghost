using Iceghost.Contracts;
using Iceghost.Runner.Scanning;
using Xunit;

namespace Iceghost.Tests;

public class SessionScannerTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    readonly string dir = Directory.CreateTempSubdirectory("ig").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static SessionSnapshot Snap(bool ended, string text, TimeSpan age, bool tool = false) =>
        new(Runtime.Claude, "s1", @"C:\p", null, Now - age, ended, tool,
            [new TranscriptEntry("assistant", text, Now - age)]);

    [Fact] public void Question_needs_input() => Assert.Equal(SessionState.NeedsInput, StateClassifier.Classify(Snap(true, "Ship it? \n", TimeSpan.FromHours(1)), Now));
    [Fact] public void Ended_is_done() => Assert.Equal(SessionState.Done, StateClassifier.Classify(Snap(true, "Done.", TimeSpan.FromHours(1)), Now));
    [Fact] public void Fresh_working() => Assert.Equal(SessionState.Working, StateClassifier.Classify(Snap(false, "x", TimeSpan.FromSeconds(30)), Now));
    [Fact] public void Pending_tool_11h_needs_input() => Assert.Equal(SessionState.NeedsInput, StateClassifier.Classify(Snap(false, "x", TimeSpan.FromHours(11), true), Now));
    [Fact] public void Pending_tool_13h_stalled() => Assert.Equal(SessionState.Stalled, StateClassifier.Classify(Snap(false, "x", TimeSpan.FromHours(13), true), Now));
    [Fact] public void Pending_tool_needs_input() => Assert.Equal(SessionState.NeedsInput, StateClassifier.Classify(Snap(false, "x", TimeSpan.FromMinutes(3), true), Now));
    [Fact] public void Quiet_stalled() => Assert.Equal(SessionState.Stalled, StateClassifier.Classify(Snap(false, "x", TimeSpan.FromMinutes(11)), Now));

    ScanRoots Roots() => new(Path.Combine(dir, "c"), Path.Combine(dir, "x"), Path.Combine(dir, "p"));

    string Make(string rel, TimeSpan age, string content = "{}")
    {
        var p = Path.Combine(dir, "c", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content + "\n");
        File.SetLastWriteTimeUtc(p, Now.UtcDateTime - age);
        return p;
    }

    SessionScanner Scanner() => new(Roots(), (rt, _) => Snap(true, "Done.", TimeSpan.FromMinutes(1)) with { Runtime = rt });

    [Fact]
    public void Scan_reports_done()
    {
        Make("proj/a.jsonl", TimeSpan.FromMinutes(1));
        var r = Assert.Single(Scanner().Scan(Now));
        Assert.Equal(new(Runtime.Claude, "s1", @"C:\p", null, SessionState.Done, Now - TimeSpan.FromMinutes(1), r.Tail), r);
    }

    [Fact]
    public void Locked_file_read()
    {
        var p = Make("proj/a.jsonl", TimeSpan.FromMinutes(1));
        using var w = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Single(Scanner().Scan(Now));
    }

    [Fact]
    public void Subagent_files_skipped()
    {
        Make("proj/s/subagents/a.jsonl", TimeSpan.FromMinutes(1));
        Assert.Empty(Scanner().Scan(Now));
    }

    [Fact]
    public void Old_files_skipped()
    {
        Make("proj/a.jsonl", TimeSpan.FromDays(15));
        Assert.Empty(Scanner().Scan(Now));
    }

    [Fact]
    public void Cap_50_newest()
    {
        for (var i = 0; i < 60; i++) Make($"proj/{i}.jsonl", TimeSpan.FromMinutes(i + 1), i.ToString());
        var sc = new SessionScanner(Roots(), (rt, lines) => Snap(true, "Done.", TimeSpan.Zero) with { SessionId = lines[0] });
        var ids = sc.Scan(Now).Select(r => r.SessionId).ToHashSet();
        Assert.Equal(50, ids.Count);
        Assert.Contains("0", ids);
        Assert.DoesNotContain("55", ids);
    }

    SessionScanner Counting(Action<IReadOnlyList<string>> on) =>
        new(Roots(), (rt, l) => { on(l); return Snap(true, "Done.", TimeSpan.FromMinutes(1)) with { Runtime = rt }; });

    [Fact]
    public void Unchanged_file_not_reread()
    {
        Make("proj/a.jsonl", TimeSpan.FromMinutes(1));
        var n = 0;
        var sc = Counting(_ => n++);
        sc.Scan(Now); sc.Scan(Now.AddSeconds(15));
        Assert.Equal(1, n);
    }

    [Fact]
    public void Changed_file_reread()
    {
        var p = Make("proj/a.jsonl", TimeSpan.FromMinutes(1));
        var n = 0;
        var sc = Counting(_ => n++);
        sc.Scan(Now);
        File.AppendAllText(p, "{\"x\":1}\n");
        sc.Scan(Now);
        Assert.Equal(2, n);
    }

    [Fact]
    public void Large_file_head_and_tail()
    {
        var content = string.Join("\n",Enumerable.Range(0, 60000).Select(i => $"line{i:D6}-" + new string('x', 70)));
        var p = Make("proj/a.jsonl", TimeSpan.FromMinutes(1), content);
        Assert.True(new FileInfo(p).Length > 4 << 20);
        IReadOnlyList<string>? got = null;
        Counting(l => got = l).Scan(Now);
        Assert.StartsWith("line000000-", got![0]);
        Assert.StartsWith("line059999-", got[^1]);
        Assert.True(got.Count < 60000);
    }
}

[Collection("ApiEnv")]
public class ScanRootsEnvTests : IDisposable
{
    static readonly string[] Vars = ["PI_CODING_AGENT_SESSION_DIR", "PI_CODING_AGENT_DIR", "CLAUDE_CONFIG_DIR", "CODEX_HOME"];
    readonly Dictionary<string, string?> saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
    readonly string dir = Directory.CreateTempSubdirectory("igr").FullName;

    public ScanRootsEnvTests() { foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null); }
    public void Dispose()
    {
        foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void Pi_session_dir_beats_agent_dir_beats_default()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal([Path.Combine(home, ".pi", "agent", "sessions")], ScanRoots.Default().Pi);
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", Path.Combine(dir, "agent"));
        Assert.Equal([Path.Combine(dir, "agent", "sessions")], ScanRoots.Default().Pi);
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", Path.Combine(dir, "s"));
        Assert.Equal([Path.Combine(dir, "s")], ScanRoots.Default().Pi);
    }

    [Fact]
    public void Pi_settings_session_dir_added_with_tilde_expanded()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agent = Path.Combine(dir, "agent");
        Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(agent, "settings.json"), "{\"sessionDir\":\"~/my-pi\"}");
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", agent);
        Assert.Equal([Path.Combine(agent, "sessions"), Path.Combine(home, "my-pi")], ScanRoots.Default().Pi);
    }

    [Fact]
    public void Config_roots_replace_default_for_that_runtime_only()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(dir, "cx"));
        var r = ScanRoots.Default(new Dictionary<Runtime, string[]> { [Runtime.Pi] = [Path.Combine(dir, "a"), Path.Combine(dir, "b")] });
        Assert.Equal([Path.Combine(dir, "a"), Path.Combine(dir, "b")], r.Pi);
        Assert.Equal([Path.Combine(dir, "cx", "sessions")], r.Codex);
    }

    [Fact]
    public void Scan_walks_every_root_of_a_runtime()
    {
        string W(string root) { var p = Path.Combine(dir, root, "a.jsonl"); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "{}\n"); return p; }
        W("r1"); W("r2");
        var sc = new SessionScanner(new ScanRoots([], [], [Path.Combine(dir, "r1"), Path.Combine(dir, "r2")]),
            (rt, _) => new SessionSnapshot(rt, Guid.NewGuid().ToString(), "c", null, DateTimeOffset.UtcNow, true, false, []));
        Assert.Equal(2, sc.Scan(DateTimeOffset.UtcNow).Count);
    }
}
