using Iceghost.Api.Data;
using Iceghost.Contracts;
using Xunit;

namespace Iceghost.Tests;

public sealed class IceStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ice-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset T = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    public IceStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch { } }
    IceStore Store() => new(Path.Combine(_dir, "t.db"));

    static SessionReport Sess(string id, DateTimeOffset? at = null) =>
        new(Runtime.Claude, id, "C:\\p", "title", SessionState.Done, at ?? T, [new("user", "hi", T)]);
    static RunnerReport Full(params SessionReport[] s) => new("h", "linux", s);

    [Fact]
    public void Authenticate_roundtrip()
    {
        var s = Store();
        var (id, tok) = s.CreateMachine("desk");
        Assert.Equal(id, s.Authenticate(tok));
        Assert.Null(s.Authenticate("nope"));
        Assert.Null(s.Authenticate(""));
        Assert.DoesNotContain('=', tok);
    }

    [Fact]
    public void Heartbeat_keeps_sessions()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        s.ApplyReport(id, new("h2", "win", null), T.AddMinutes(1));
        Assert.Single(s.ListSessions(T));
        Assert.Equal(T.AddMinutes(1), s.ListMachines()[0].LastSeen);
    }

    [Fact]
    public void Report_replaces_sessions()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1"), Sess("s2")), T);
        s.ApplyReport(id, Full(Sess("s2")), T);
        Assert.Equal(["s2"], s.ListSessions(T).Select(r => r.SessionId));
    }

    [Fact]
    public void Enqueue_take_complete_flow()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        Assert.Equal(1, s.Enqueue(id, "s1", "hi", T));
        Assert.Equal(1, s.ListSessions(T)[0].PendingCommands);
        Assert.Equal([new RunnerCommand(1, Runtime.Claude, "s1", "C:\\p", "hi")], s.TakeCommands(id, T));
        Assert.Empty(s.TakeCommands(id, T));
        Assert.True(s.Complete(id, 1, new(true, "echo hi"), T));
        Assert.False(s.Complete(id, 1, new(true, "again"), T));
        var d = s.GetSession(id, "s1", T)!;
        Assert.Equal("done", d.Commands[0].Status);
        Assert.Equal("echo hi", d.Commands[0].Output);
        Assert.Equal(0, d.Session.PendingCommands);
        Assert.Equal("hi", d.Tail[0].Text);
    }

    [Fact]
    public void Enqueue_unknown_session_null()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        Assert.Null(s.Enqueue(id, "zz", "hi", T));
    }

    [Fact]
    public void Complete_other_machine_false()
    {
        var s = Store(); var (a, _) = s.CreateMachine("a"); var (b, _) = s.CreateMachine("b");
        s.ApplyReport(a, Full(Sess("s1")), T);
        s.Enqueue(a, "s1", "hi", T); s.TakeCommands(a, T);
        Assert.False(s.Complete(b, 1, new(true, "x"), T));
        Assert.Empty(s.TakeCommands(b, T));
        Assert.Null(s.GetSession(b, "s1", T));
        Assert.Null(s.Enqueue(b, "s1", "x", T));
        Assert.Equal("delivered", s.GetSession(a, "s1", T)!.Commands[0].Status);
    }

    [Fact]
    public void Delivered_after_60min_failed()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        s.Enqueue(id, "s1", "hi", T); s.TakeCommands(id, T);
        s.TakeCommands(id, T.AddMinutes(59));
        Assert.Equal("delivered", s.GetSession(id, "s1", T)!.Commands[0].Status);
        s.TakeCommands(id, T.AddMinutes(60));
        var c = s.GetSession(id, "s1", T)!.Commands[0];
        Assert.Equal("failed", c.Status);
        Assert.Equal("no result from runner", c.Output);
    }

    [Fact]
    public void Delete_machine_cascades()
    {
        var s = Store(); var (a, _) = s.CreateMachine("a"); var (b, tb) = s.CreateMachine("b");
        s.ApplyReport(a, Full(Sess("s1")), T); s.ApplyReport(b, Full(Sess("s1")), T);
        s.Enqueue(a, "s1", "hi", T);
        Assert.True(s.DeleteMachine(a));
        Assert.False(s.DeleteMachine(a));
        Assert.Equal([b], s.ListSessions(T).Select(r => r.MachineId));
        Assert.Single(s.ListMachines());
        Assert.Equal(b, s.Authenticate(tb));
    }

    [Fact]
    public void Sessions_newest_first()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("old", T), Sess("new", T.AddHours(1))), T);
        Assert.Equal(["new", "old"], s.ListSessions(T).Select(r => r.SessionId));
    }
}
