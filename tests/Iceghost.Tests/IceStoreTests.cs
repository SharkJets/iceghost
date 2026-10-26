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
    public void Reenrolled_host_clears_ghost_sessions()
    {
        var s = Store(); var (old, _) = s.CreateMachine("old"); var (fresh, _) = s.CreateMachine("new"); var (other, _) = s.CreateMachine("other");
        s.ApplyReport(old, Full(Sess("s1")), T);
        s.ApplyReport(other, new("OTHER", "linux", [Sess("s2")]), T);
        s.ApplyReport(fresh, Full(Sess("s3")), T.AddMinutes(1)); // old still online (<2 min): kept
        Assert.Equal(["s1", "s2", "s3"], s.ListSessions(T).Select(r => r.SessionId).Order());
        s.ApplyReport(fresh, new("H", "linux", [Sess("s3")]), T.AddMinutes(3));
        Assert.Equal(["s2", "s3"], s.ListSessions(T).Select(r => r.SessionId).Order());
        Assert.Equal(3, s.ListMachines().Count);
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
    public void Delivered_waits_6h()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        s.Enqueue(id, "s1", "hi", T); s.TakeCommands(id, T);
        s.TakeCommands(id, T.AddHours(6).AddMinutes(-1));
        Assert.Equal("delivered", s.GetSession(id, "s1", T)!.Commands[0].Status);
        s.TakeCommands(id, T.AddHours(6));
        var c = s.GetSession(id, "s1", T)!.Commands[0];
        Assert.Equal("failed", c.Status);
        Assert.Equal("no result from runner", c.Output);
    }

    [Fact]
    public void Fresh_take_fails_held_commands()
    {
        var s = Store(); var (a, _) = s.CreateMachine("a"); var (b, _) = s.CreateMachine("b");
        s.ApplyReport(a, Full(Sess("s1")), T); s.ApplyReport(b, Full(Sess("s1")), T);
        s.Enqueue(a, "s1", "one", T); s.Enqueue(a, "s1", "two", T); s.Enqueue(b, "s1", "other", T);
        s.TakeCommands(a, T); s.TakeCommands(b, T);
        s.Heartbeat(a, 2, T); // 2 running, 1 delivered
        s.Enqueue(a, "s1", "three", T);
        Assert.Equal([new RunnerCommand(4, Runtime.Claude, "s1", "C:\\p", "three")], s.TakeCommands(a, T, fresh: true));
        var cmds = s.GetSession(a, "s1", T)!.Commands;
        Assert.Equal([("failed", "runner restarted before finishing"), ("failed", "runner restarted before finishing"), ("delivered", null)], cmds.Take(3).Select(c => (c.Status, c.Output)));
        Assert.Equal("delivered", s.GetSession(b, "s1", T)!.Commands[0].Status);
    }

    [Fact]
    public void Dismiss_pending_command()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        s.Enqueue(id, "s1", "a", T); s.Enqueue(id, "s1", "b", T);
        Assert.False(s.Dismiss(id, "other", 1));
        Assert.True(s.Dismiss(id, "s1", 1));
        var c = s.GetSession(id, "s1", T)!.Commands[0];
        Assert.Equal(("failed", "dismissed"), (c.Status, c.Output));
        Assert.False(s.Dismiss(id, "s1", 1));
        s.TakeCommands(id, T); // only b is still queued
        Assert.True(s.Complete(id, 2, new(true, "x"), T));
        Assert.False(s.Dismiss(id, "s1", 2));
    }

    [Fact]
    public void Stale_delivered_expires_on_read()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        s.Enqueue(id, "s1", "hi", T); s.TakeCommands(id, T);
        Assert.Equal(1, s.ListSessions(T.AddHours(6).AddMinutes(-1))[0].PendingCommands);
        Assert.Equal(0, s.ListSessions(T.AddHours(6))[0].PendingCommands);
        var c = s.GetSession(id, "s1", T.AddHours(6))!.Commands[0];
        Assert.Equal(("failed", "no result from runner"), (c.Status, c.Output));
    }

    [Fact]
    public void Concurrent_takes_deliver_once()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        for (var i = 0; i < 20; i++) s.Enqueue(id, "s1", "m" + i, T);
        var n = Enumerable.Range(0, 4).Select(_ => Task.Run(() => s.TakeCommands(id, T).Count)).ToArray();
        Task.WaitAll(n);
        Assert.Equal(20, n.Sum(t => t.Result));
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

    [Fact]
    public void Duplicate_session_keeps_latest_activity()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        var newer = Sess("s1", T.AddHours(1)); var older = Sess("s1", T) with { State = SessionState.Stalled };
        foreach (var rep in new[] { new[] { newer, older }, [older, newer] })
        {
            s.ApplyReport(id, Full(rep), T);
            var r = Assert.Single(s.ListSessions(T));
            Assert.Equal((SessionState.Done, T.AddHours(1)), (r.State, r.LastActivity));
        }
    }

    IceStore Running(out long id)
    {
        var s = Store(); (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        s.Enqueue(id, "s1", "hi", T); s.TakeCommands(id, T);
        return s;
    }

    [Fact]
    public void Heartbeat_moves_to_running()
    {
        var s = Running(out var id);
        Assert.True(s.Heartbeat(id, 1, T.AddSeconds(5)));
        Assert.True(s.Heartbeat(id, 1, T.AddSeconds(20)));
        var c = s.GetSession(id, "s1", T.AddSeconds(20))!.Commands[0];
        Assert.Equal(("running", T.AddSeconds(5), T.AddSeconds(20)), (c.Status, c.StartedAt, c.HeartbeatAt));
        Assert.False(s.Heartbeat(id, 99, T));
    }

    [Fact]
    public void Heartbeat_other_machine_404()
    {
        var s = Running(out var id); var (b, _) = s.CreateMachine("b");
        Assert.False(s.Heartbeat(b, 1, T));
        Assert.Equal("delivered", s.GetSession(id, "s1", T)!.Commands[0].Status);
    }

    [Fact]
    public void Stale_running_fails_after_3min()
    {
        var s = Running(out var id);
        s.Heartbeat(id, 1, T);
        Assert.Equal("running", s.GetSession(id, "s1", T.AddMinutes(2))!.Commands[0].Status);
        Assert.Equal(1, s.ListSessions(T.AddMinutes(2))[0].PendingCommands);
        var c = s.GetSession(id, "s1", T.AddMinutes(3))!.Commands[0];
        Assert.Equal(("failed", "runner stopped responding (killed or offline)"), (c.Status, c.Output));
    }

    [Fact]
    public void Heartbeat_revives_heartbeat_expired()
    {
        var s = Running(out var id);
        s.Heartbeat(id, 1, T);
        Assert.Equal("failed", s.GetSession(id, "s1", T.AddMinutes(4))!.Commands[0].Status);
        Assert.True(s.Heartbeat(id, 1, T.AddMinutes(4)));
        var c = s.GetSession(id, "s1", T.AddMinutes(4))!.Commands[0];
        Assert.Equal(("running", (string?)null), (c.Status, c.Output));
    }

    [Fact]
    public void Heartbeat_does_not_revive_dismissed()
    {
        var s = Running(out var id);
        s.Heartbeat(id, 1, T);
        Assert.True(s.Dismiss(id, "s1", 1));
        Assert.False(s.Heartbeat(id, 1, T.AddSeconds(5)));
        var c = s.GetSession(id, "s1", T)!.Commands[0];
        Assert.Equal(("failed", "dismissed"), (c.Status, c.Output));
    }

    [Fact]
    public void Late_result_overrides_heartbeat_expiry()
    {
        var s = Running(out var id);
        s.Heartbeat(id, 1, T);
        Assert.Equal("failed", s.GetSession(id, "s1", T.AddMinutes(3))!.Commands[0].Status);
        Assert.True(s.Complete(id, 1, new(true, "ok"), T.AddMinutes(4)));
        var c = s.GetSession(id, "s1", T.AddMinutes(4))!.Commands[0];
        Assert.Equal(("done", "ok"), (c.Status, c.Output));
        Assert.False(s.Complete(id, 1, new(true, "again"), T.AddMinutes(4)));
    }

    [Fact]
    public void Late_result_does_not_override_dismiss()
    {
        var s = Running(out var id);
        s.Heartbeat(id, 1, T);
        Assert.True(s.Dismiss(id, "s1", 1));
        Assert.False(s.Complete(id, 1, new(true, "ok"), T));
        var c = s.GetSession(id, "s1", T)!.Commands[0];
        Assert.Equal(("failed", "dismissed"), (c.Status, c.Output));
    }

    [Fact]
    public void Complete_from_running()
    {
        var s = Running(out var id);
        s.Heartbeat(id, 1, T);
        Assert.True(s.Complete(id, 1, new(true, "ok"), T));
        Assert.Equal("done", s.GetSession(id, "s1", T)!.Commands[0].Status);
    }

    [Fact]
    public void Existing_db_gets_new_columns()
    {
        var s = Running(out var id);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "t.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE Commands DROP COLUMN StartedAt; ALTER TABLE Commands DROP COLUMN HeartbeatAt;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var s2 = Store();
        Assert.True(s2.Heartbeat(id, 1, T));
        Assert.Equal("running", s2.GetSession(id, "s1", T)!.Commands[0].Status);
    }

    [Fact]
    public void Last_action_saved_and_listed()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1") with { LastAction = new string('x', 200) }, Sess("s2")), T);
        var rows = s.ListSessions(T).ToDictionary(r => r.SessionId);
        Assert.Equal(140, rows["s1"].LastAction!.Length);
        Assert.EndsWith("…", rows["s1"].LastAction);
        Assert.Null(rows["s2"].LastAction);
    }

    [Fact]
    public void Existing_db_gets_last_action_column()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "t.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE Sessions DROP COLUMN LastAction;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var s2 = Store();
        s2.ApplyReport(id, Full(Sess("s1") with { LastAction = "Bash: ls" }), T);
        Assert.Equal("Bash: ls", s2.ListSessions(T)[0].LastAction);
    }

    [Fact]
    public void Runner_version_stored_and_kept_when_later_report_omits_it()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, new RunnerReport("h", "linux", null) { RunnerVersion = "0.1.16" }, T);
        Assert.Equal("0.1.16", s.ListMachines()[0].RunnerVersion);
        s.ApplyReport(id, Full(Sess("s1")), T);
        Assert.Equal("0.1.16", s.ListMachines()[0].RunnerVersion);
    }

    [Fact]
    public void Existing_db_gets_runner_version_column()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "t.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE Machines DROP COLUMN RunnerVersion;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var s2 = Store();
        s2.ApplyReport(id, new RunnerReport("h", "linux", null) { RunnerVersion = "0.1.2" }, T);
        Assert.Equal("0.1.2", s2.ListMachines()[0].RunnerVersion);
    }

    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    [Fact]
    public void Image_roundtrip_and_cleared_on_complete()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        s.Enqueue(id, "s1", "hi", T, Png, "image/png");
        Assert.True(s.GetSession(id, "s1", T)!.Commands[0].HasImage);
        var cmd = Assert.Single(s.TakeCommands(id, T));
        Assert.Equal(Png, Convert.FromBase64String(cmd.Image!));
        Assert.Equal("image/png", cmd.ImageType);
        s.Complete(id, 1, new(true, "ok"), T);
        s.Enqueue(id, "s1", "again", T);
        Assert.Null(Assert.Single(s.TakeCommands(id, T)).Image);
        Assert.Null(RawImage(1));
    }

    [Fact]
    public void Image_cleared_on_dismiss()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        s.ApplyReport(id, Full(Sess("s1")), T);
        s.Enqueue(id, "s1", "hi", T, Png, "image/png");
        Assert.NotNull(RawImage(1));
        s.Dismiss(id, "s1", 1);
        Assert.Null(RawImage(1));
    }

    object? RawImage(long cmdId)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "t.db")}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT Image FROM Commands WHERE Id = {cmdId}";
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    [Fact]
    public void Existing_db_gets_image_columns()
    {
        var s = Store(); var (id, _) = s.CreateMachine("d");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "t.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE Commands DROP COLUMN Image; ALTER TABLE Commands DROP COLUMN ImageType;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var s2 = Store();
        s2.ApplyReport(id, Full(Sess("s1")), T);
        s2.Enqueue(id, "s1", "hi", T, Png, "image/png");
        Assert.NotNull(Assert.Single(s2.TakeCommands(id, T)).Image);
    }
}
