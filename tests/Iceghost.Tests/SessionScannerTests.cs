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
}
