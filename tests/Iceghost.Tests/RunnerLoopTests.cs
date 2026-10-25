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
        Func<HttpRequestMessage, HttpResponseMessage> f, Func<Runtime, IRuntimeAdapter>? adapters = null)
    {
        var t = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var h = new Handler(f) { Time = t };
        var client = new ServerClient(new HttpClient(h) { BaseAddress = new Uri("http://x/") }, "tok");
        return (new RunnerLoop(client, _ => [S("s1"), S("s2")],
            adapters ?? (_ => new FakeRuntimeAdapter(c => new(true, "echo " + c.Text))), t), h, t);
    }

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
}
