using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iceghost.Contracts;
using Iceghost.Runner.Loop;
using Iceghost.Runner.Adapters;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iceghost.Tests;

public class RunnerLoopTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        private readonly List<(string Method, string Path, string? Body, DateTimeOffset At)> _calls = [];
        public FakeTimeProvider Time = null!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            lock (_calls) _calls.Add((r.Method.Method, r.RequestUri!.PathAndQuery, body, Time.GetUtcNow()));
            return f(r);
        }
        public List<(string Method, string Path, string? Body, DateTimeOffset At)> Snapshot() { lock (_calls) return [.. _calls]; }
    }

    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o, o.GetType(), options: IceJson.Options) };

    private static SessionReport S(string id) =>
        new(Runtime.Claude, id, "C:\\p", null, SessionState.Done, DateTimeOffset.UnixEpoch, []);

    private static (RunnerLoop Loop, Handler H, FakeTimeProvider T) Make(
        Func<HttpRequestMessage, HttpResponseMessage> f, Func<Runtime, IRuntimeAdapter>? adapters = null,
        Func<FakeTimeProvider, Func<DateTimeOffset, IReadOnlyList<SessionReport>>>? scan = null,
        int maxParallel = 4, TimeSpan? idle = null)
    {
        var t = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var h = new Handler(f) { Time = t };
        var client = new ServerClient(new HttpClient(h) { BaseAddress = new Uri("http://x/") }, "tok");
        return (new RunnerLoop(client, scan?.Invoke(t) ?? (_ => [S("s1"), S("s2")]),
            adapters ?? (_ => new FakeRuntimeAdapter(c => new(true, "echo " + c.Text))), t, maxParallel, idle), h, t);
    }

    // Blocks every command until Release() (or cancellation); tracks concurrency and start order.
    private sealed class Gate : IRuntimeAdapter
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active, _max;
        public readonly List<long> Started = [];
        public int Active => Volatile.Read(ref _active);
        public int Max => Volatile.Read(ref _max);
        public void Release() => _open.TrySetResult();
        public async Task<CommandResult> SendAsync(RunnerCommand cmd, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _active);
            int m;
            while ((m = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, m) != m) { }
            lock (Started) Started.Add(cmd.Id);
            try { await _open.Task.WaitAsync(ct); return new(true, "done " + cmd.Id); }
            catch (OperationCanceledException) { return new(false, "cancelled"); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static bool Posted(Handler h, long id, string what) => h.Snapshot().Any(c => c.Path.EndsWith($"/{id}/{what}"));

    private static Func<FakeTimeProvider, Func<DateTimeOffset, IReadOnlyList<SessionReport>>> Sessions(params string[] ids) =>
        _ => _ => ids.Select(S).ToList();

    // Advance fake time in small steps, letting the loop run, until cond holds.
    private static async Task Pump(FakeTimeProvider t, Func<bool> cond)
    {
        for (var i = 0; i < 600 && !cond(); i++)
        {
            await Task.Delay(5);
            t.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.True(cond(), "condition not reached");
    }

    // GET commands serves `cmds` once, then empty; everything else is 204.
    private static Func<HttpRequestMessage, HttpResponseMessage> Server(params RunnerCommand[] cmds)
    {
        var served = 0;
        return r => r.Method == HttpMethod.Get
            ? Json(Interlocked.Exchange(ref served, 1) == 0 ? cmds : Array.Empty<RunnerCommand>())
            : new(HttpStatusCode.NoContent);
    }

    private static T Body<T>(string? b) => JsonSerializer.Deserialize<T>(b!, IceJson.Options)!;

    [Fact]
    public async Task First_full_then_heartbeat_when_unchanged()
    {
        var (loop, h, t) = Make(Server());
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().Count(c => c.Path.EndsWith("report")) >= 2);
        cts.Cancel(); await run;
        var reports = h.Snapshot().Where(c => c.Path.EndsWith("report")).Select(c => Body<RunnerReport>(c.Body)).ToList();
        Assert.Equal(2, reports[0].Sessions!.Count);
        Assert.Null(reports[1].Sessions);
    }

    [Fact]
    public async Task Command_runs_posts_result_then_full_report()
    {
        var (loop, h, t) = Make(Server(new RunnerCommand(7, Runtime.Claude, "s1", "C:\\p", "hi")));
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().SkipWhile(c => !c.Path.EndsWith("/7/result")).Any(c => c.Path.EndsWith("report")));
        cts.Cancel(); await run;
        var calls = h.Snapshot();
        var idx = calls.FindIndex(c => c.Path.EndsWith("/7/result"));
        Assert.Equal(new CommandResult(true, "echo hi"), Body<CommandResult>(calls[idx].Body));
        Assert.NotNull(Body<RunnerReport>(calls.Skip(idx).First(c => c.Path.EndsWith("report")).Body).Sessions);
        var during = calls.Take(idx).Where(c => c.Path.EndsWith("report")).Select(c => Body<RunnerReport>(c.Body))
            .Any(rp => rp.Sessions?.Any(s => s.SessionId == "s1" && s.State == SessionState.Working) == true);
        Assert.True(during);
    }

    [Fact]
    public async Task Unauthorized_stops_loop()
    {
        var (loop, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<UnauthorizedRunnerException>(() => loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Network_error_backs_off()
    {
        var (loop, h, t) = Make(_ => throw new HttpRequestException("down"));
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().Count >= 5);
        cts.Cancel(); await run;
        var at = h.Snapshot().Select(c => c.At).ToList();
        Assert.Equal([1, 2, 4, 8], at.Zip(at.Skip(1), (a, b) => (int)(b - a).TotalSeconds).Take(4));
    }

    [Fact]
    public async Task Result_posted_when_adapter_fails()
    {
        var (loop, h, t) = Make(Server(new RunnerCommand(9, Runtime.Claude, "s1", "C:\\p", "hi")), _ => new Throwing());
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().Any(c => c.Path.EndsWith("/9/result")));
        cts.Cancel(); await run;
        var res = Body<CommandResult>(h.Snapshot().First(c => c.Path.EndsWith("/9/result")).Body);
        Assert.False(res.Ok);
        Assert.Contains("boom", res.Output);
    }

    [Fact]
    public async Task Unknown_session_rejected()
    {
        var ran = false;
        var (loop, h, t) = Make(Server(new RunnerCommand(11, Runtime.Claude, "nope", "C:\\p", "hi")),
            _ => new FakeRuntimeAdapter(_ => { ran = true; return new(true, ""); }));
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().Any(c => c.Path.EndsWith("/11/result")));
        cts.Cancel(); await run;
        var res = Body<CommandResult>(h.Snapshot().First(c => c.Path.EndsWith("/11/result")).Body);
        Assert.False(res.Ok);
        Assert.Equal("unknown session on this machine", res.Output);
        Assert.False(ran);
    }

    [Fact]
    public async Task Uses_local_cwd_not_server_cwd()
    {
        string? cwd = null;
        var (loop, h, t) = Make(Server(new RunnerCommand(12, Runtime.Claude, "s1", "C:\\evil", "hi")),
            _ => new FakeRuntimeAdapter(c => { cwd = c.Cwd; return new(true, ""); }));
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().Any(c => c.Path.EndsWith("/12/result")));
        cts.Cancel(); await run;
        Assert.Equal("C:\\p", cwd);
    }

    [Fact]
    public async Task Cancel_completes_without_throwing()
    {
        var (loop, _, _) = Make(Server());
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await run;
    }

    private sealed class Throwing : IRuntimeAdapter
    {
        public Task<CommandResult> SendAsync(RunnerCommand cmd, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Permission_copied_from_scan()
    {
        RunnerCommand? seen = null;
        var t = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var h = new Handler(Server(new RunnerCommand(7, Runtime.Claude, "s1", "C:\\p", "hi") { Permission = "bypassPermissions" })) { Time = t };
        var client = new ServerClient(new HttpClient(h) { BaseAddress = new Uri("http://x/") }, "tok");
        var loop = new RunnerLoop(client, _ => [S("s1") with { Permission = "plan" }],
            _ => new FakeRuntimeAdapter(c => { seen = c; return new(true, "ok"); }), t);
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().Any(c => c.Path.EndsWith("/7/result")));
        cts.Cancel(); await run;
        Assert.Equal("plan", seen!.Permission);
    }

    private static RunnerCommand C(long id, string session) => new(id, Runtime.Claude, session, "C:\\p", "hi");

    [Fact]
    public async Task Two_sessions_run_in_parallel()
    {
        var gate = new Gate();
        var (loop, h, t) = Make(Server(C(1, "s1"), C(2, "s2")), _ => gate);
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => gate.Active == 2);
        gate.Release();
        await Pump(t, () => Posted(h, 1, "result") && Posted(h, 2, "result"));
        cts.Cancel(); await run;
    }

    [Fact]
    public async Task Same_session_runs_in_order()
    {
        var gate = new Gate();
        var (loop, h, t) = Make(Server(C(1, "s1"), C(2, "s1")), _ => gate);
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => gate.Active == 1);
        for (var i = 0; i < 20; i++) { await Task.Delay(5); t.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(1, gate.Active);
        gate.Release();
        await Pump(t, () => Posted(h, 1, "result") && Posted(h, 2, "result"));
        cts.Cancel(); await run;
        Assert.Equal(1, gate.Max);
        Assert.Equal(new long[] { 1, 2 }, gate.Started);
    }

    [Fact]
    public async Task Max_parallel_respected()
    {
        var gate = new Gate();
        var ids = Enumerable.Range(1, 5).Select(i => "s" + i).ToArray();
        var (loop, h, t) = Make(Server(ids.Select((s, i) => C(i + 1, s)).ToArray()), _ => gate, Sessions(ids), maxParallel: 2);
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => gate.Active == 2);
        for (var i = 0; i < 20; i++) { await Task.Delay(5); t.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(2, gate.Active);
        gate.Release();
        await Pump(t, () => Enumerable.Range(1, 5).All(i => Posted(h, i, "result")));
        cts.Cancel(); await run;
        Assert.Equal(2, gate.Max);
    }

    [Fact]
    public async Task Heartbeat_sent_while_running()
    {
        var gate = new Gate();
        var (loop, h, t) = Make(Server(C(5, "s1")), _ => gate);
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().Count(c => c.Path.EndsWith("/5/heartbeat")) >= 3);
        Assert.False(Posted(h, 5, "result"));
        gate.Release();
        await Pump(t, () => Posted(h, 5, "result"));
        cts.Cancel(); await run;
        var calls = h.Snapshot();
        Assert.Equal("POST", calls.First(c => c.Path.EndsWith("/5/heartbeat")).Method);
        Assert.DoesNotContain(calls.SkipWhile(c => !c.Path.EndsWith("/5/result")), c => c.Path.EndsWith("/5/heartbeat"));
    }

    [Fact]
    public async Task Scan_continues_during_command()
    {
        var gate = new Gate();
        var (loop, h, t) = Make(Server(C(1, "s1")), _ => gate);
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => gate.Active == 1);
        var before = h.Snapshot().Count(c => c.Path.EndsWith("report"));
        await Pump(t, () => h.Snapshot().Count(c => c.Path.EndsWith("report")) >= before + 2);
        Assert.False(Posted(h, 1, "result"));
        gate.Release();
        await Pump(t, () => Posted(h, 1, "result"));
        cts.Cancel(); await run;
    }

    [Fact]
    public async Task Idle_timeout_cancels()
    {
        var gate = new Gate(); // never released: only the idle timeout can end it
        var (loop, h, t) = Make(Server(C(1, "s1")), _ => gate, idle: TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => Posted(h, 1, "result"));
        cts.Cancel(); await run;
        var res = Body<CommandResult>(h.Snapshot().First(c => c.Path.EndsWith("/1/result")).Body);
        Assert.Equal(new CommandResult(false, "stopped: no activity for 1 min"), res);
    }

    [Fact]
    public async Task Busy_turn_not_cancelled()
    {
        var gate = new Gate();
        var (loop, h, t) = Make(Server(C(1, "s1")), _ => gate,
            t => _ => [S("s1") with { LastActivity = t.GetUtcNow() }], idle: TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => h.Snapshot().Count(c => c.Path.EndsWith("/1/heartbeat")) >= 12); // ~3 min of fake time
        Assert.False(Posted(h, 1, "result"));
        gate.Release();
        await Pump(t, () => Posted(h, 1, "result"));
        cts.Cancel(); await run;
        Assert.Equal(new CommandResult(true, "done 1"), Body<CommandResult>(h.Snapshot().First(c => c.Path.EndsWith("/1/result")).Body));
    }

    // Real transcript files read by the real SessionScanner/TranscriptReader/StateClassifier.
    private sealed class Tree : IDisposable
    {
        public readonly string Dir = Directory.CreateTempSubdirectory("igl").FullName;
        public static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1); // Make()'s fake clock start
        public readonly Iceghost.Runner.Scanning.SessionScanner Scanner;
        public Tree() => Scanner = new(new(Path.Combine(Dir, "c"), Path.Combine(Dir, "x"), Path.Combine(Dir, "p")), Iceghost.Runner.Scanning.TranscriptReader.Read);
        public static string Line(string id, string type, string text, TimeSpan ago, string? stop = null) => JsonSerializer.Serialize(new
        {
            type, sessionId = id, cwd = @"C:\p", timestamp = (Now - ago).ToString("o"),
            message = new { role = type, content = text, stop_reason = stop },
        });
        public void Write(string id, params string[] lines)
        {
            var p = Path.Combine(Dir, "c", "proj", id + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.AppendAllLines(p, lines);
        }
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static List<RunnerReport> Reports(Handler h) =>
        h.Snapshot().Where(c => c.Path.EndsWith("report")).Select(c => Body<RunnerReport>(c.Body)).ToList();

    [Fact]
    public async Task Unchanged_scan_sends_heartbeat_only()
    {
        using var tree = new Tree();
        tree.Write("a", Tree.Line("a", "user", "hi", TimeSpan.FromMinutes(30)), Tree.Line("a", "assistant", "done", TimeSpan.FromMinutes(29), "end_turn"));
        tree.Write("b", Tree.Line("b", "user", "hello there", TimeSpan.FromSeconds(30))); // working
        tree.Write("c", Tree.Line("c", "user", "q", TimeSpan.FromHours(2)), Tree.Line("c", "assistant", "Ship it?", TimeSpan.FromHours(2), "end_turn"));
        var (loop, h, t) = Make(Server(), scan: _ => tree.Scanner.Scan);
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => Reports(h).Count >= 3);
        cts.Cancel(); await run;
        var reports = Reports(h);
        Assert.Equal(3, reports[0].Sessions!.Count);
        Assert.Null(reports[1].Sessions);
        Assert.Null(reports[2].Sessions);
    }

    [Fact]
    public async Task Activity_only_change_is_throttled_state_change_is_not()
    {
        using var tree = new Tree();
        tree.Write("a", Tree.Line("a", "user", "hi", TimeSpan.FromMinutes(30)), Tree.Line("a", "assistant", "done", TimeSpan.FromMinutes(29), "end_turn"));
        tree.Write("b", Tree.Line("b", "user", "go", TimeSpan.FromSeconds(30)));
        var (loop, h, t) = Make(Server(), scan: _ => tree.Scanner.Scan);
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Pump(t, () => Reports(h).Count >= 1);
        tree.Write("b", Tree.Line("b", "assistant", "working on it", TimeSpan.Zero)); // still Working: only tail/LastActivity moved
        await Pump(t, () => Reports(h).Count >= 2);
        Assert.Null(Reports(h)[1].Sessions);
        await Pump(t, () => Reports(h).Count(r => r.Sessions is not null) >= 2); // activity still arrives, just batched
        var full = Reports(h).Last(r => r.Sessions is not null);
        Assert.Equal("working on it", full.Sessions!.Single(s => s.SessionId == "b").Tail[^1].Text);
        var n = Reports(h).Count;
        tree.Write("b", Tree.Line("b", "assistant", "all done", TimeSpan.Zero, "end_turn")); // Working -> Done
        await Pump(t, () => Reports(h).Count > n);
        cts.Cancel(); await run;
        Assert.Equal(SessionState.Done, Reports(h)[n].Sessions!.Single(s => s.SessionId == "b").State);
    }
}
