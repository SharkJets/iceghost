using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iceghost.Contracts;
using Iceghost.Runner.Adapters;

namespace Iceghost.Runner.Loop;

public sealed class RunnerLoop(
    ServerClient client,
    Func<DateTimeOffset, IReadOnlyList<SessionReport>> scan,
    Func<Runtime, IRuntimeAdapter> adapters,
    TimeProvider time,
    int maxParallel = 4,
    TimeSpan? idleTimeout = null)
{
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FullEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _idle = idleTimeout ?? TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _slots = new(Math.Max(1, maxParallel));
    private readonly SemaphoreSlim _reportGate = new(1);
    private readonly object _gate = new();
    private readonly Dictionary<(Runtime, string), Queue<RunnerCommand>> _queues = [];
    private readonly HashSet<(Runtime, string)> _running = [];
    private readonly List<Task> _workers = [];
    private volatile UnauthorizedRunnerException? _fatal;

    private string? _lastHash;
    private DateTimeOffset _lastFull;
    private bool _force;

    public async Task RunAsync(CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try { await LoopAsync(run.Token, ct); }
        finally
        {
            run.Cancel();
            Task[] pending;
            lock (_gate) pending = [.. _workers];
            await Task.WhenAll(pending); // workers swallow their own failures
        }
    }

    private async Task LoopAsync(CancellationToken run, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_fatal is not null) throw _fatal;
                var started = time.GetUtcNow();
                await ReportAsync(run);
                foreach (var cmd in await client.PollAsync(run))
                    Enqueue(cmd, run);
                backoff = TimeSpan.FromSeconds(1);
                var wait = ScanEvery - (time.GetUtcNow() - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, run);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (_fatal is not null) { throw _fatal; }
            catch (UnauthorizedRunnerException) { throw; }
            catch (Exception)
            {
                try { await Task.Delay(backoff, time, run); }
                catch (OperationCanceledException) { if (_fatal is not null) throw _fatal; return; }
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }
    }

    // One FIFO per (runtime, session): a session never runs two commands at once, different sessions share maxParallel slots.
    private void Enqueue(RunnerCommand cmd, CancellationToken ct)
    {
        var key = (cmd.Runtime, cmd.SessionId);
        lock (_gate)
        {
            if (_queues.TryGetValue(key, out var q)) { q.Enqueue(cmd); return; }
            _queues[key] = new Queue<RunnerCommand>([cmd]);
            _workers.RemoveAll(t => t.IsCompleted);
            _workers.Add(Task.Run(() => DrainAsync(key, ct)));
        }
    }

    private async Task DrainAsync((Runtime, string) key, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                RunnerCommand cmd;
                lock (_gate)
                {
                    var q = _queues[key];
                    if (q.Count == 0) { _queues.Remove(key); return; }
                    cmd = q.Dequeue();
                }
                await _slots.WaitAsync(ct);
                try { await ExecuteAsync(cmd, ct); }
                catch (UnauthorizedRunnerException e) { _fatal = e; return; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { } // result post failed: server expiry covers it, keep the queue moving
                finally { _slots.Release(); }
            }
        }
        catch (OperationCanceledException) { } // shutdown while waiting for a slot
    }

    private async Task ExecuteAsync(RunnerCommand cmd, CancellationToken ct)
    {
        // never trust server-sent Runtime/Cwd: only run for sessions this machine actually scanned
        var known = scan(time.GetUtcNow()).FirstOrDefault(s => s.Runtime == cmd.Runtime && s.SessionId == cmd.SessionId);
        if (known is null)
        {
            await client.ResultAsync(cmd.Id, new CommandResult(false, "unknown session on this machine"), ct);
            return;
        }
        cmd = cmd with { Runtime = known.Runtime, Cwd = known.Cwd, Permission = known.Permission };
        var key = (cmd.Runtime, cmd.SessionId);

        lock (_gate) _running.Add(key);
        try
        {
            try { await ReportAsync(ct); } // show the session as Working while it runs
            catch (Exception e) when (e is not UnauthorizedRunnerException && !ct.IsCancellationRequested) { }

            using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var idled = false;
            var monitor = MonitorAsync(cmd, turn, () => idled = true, ct);
            CommandResult result;
            try { result = await adapters(cmd.Runtime).SendAsync(cmd, turn.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { result = new CommandResult(false, e.Message); }
            turn.Cancel();
            await monitor;
            if (idled) result = new CommandResult(false, $"stopped: no activity for {_idle.TotalMinutes:0.##} min");

            await client.ResultAsync(cmd.Id, result, ct);
        }
        finally { lock (_gate) _running.Remove(key); }
        _force = true;
        await ReportAsync(ct);
    }

    // Heartbeat every 15 s while the adapter runs, and cancel the turn when the transcript has gone quiet for _idle.
    private async Task MonitorAsync(RunnerCommand cmd, CancellationTokenSource turn, Action onIdle, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        try
        {
            while (true)
            {
                try { await client.HeartbeatAsync(cmd.Id, turn.Token); }
                catch (UnauthorizedRunnerException e) { _fatal = e; turn.Cancel(); return; }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"heartbeat for command {cmd.Id} failed: {e.Message}");
                }
                await Task.Delay(HeartbeatEvery, time, turn.Token);
                var now = time.GetUtcNow();
                var last = scan(now).FirstOrDefault(s => s.Runtime == cmd.Runtime && s.SessionId == cmd.SessionId)?.LastActivity ?? started;
                if (now - (last > started ? last : started) >= _idle) { onIdle(); turn.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { } // a failed scan must not take the turn down
    }

    private async Task ReportAsync(CancellationToken ct)
    {
        await _reportGate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var sessions = scan(now);
            HashSet<(Runtime, string)> running;
            lock (_gate) running = [.. _running];
            if (running.Count > 0)
                sessions = sessions.Select(s => running.Contains((s.Runtime, s.SessionId))
                    ? s with { State = SessionState.Working } : s).ToList();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sessions, IceJson.Options))));
            var full = running.Count > 0 || _force || hash != _lastHash || now - _lastFull >= FullEvery;
            await client.ReportAsync(new RunnerReport(Environment.MachineName, RuntimeInformation.OSDescription, full ? sessions : null), ct);
            if (running.Count > 0) { _lastHash = null; return; } // transient Working override: next report must be full
            if (full) { _lastHash = hash; _lastFull = now; }
            _force = false;
        }
        finally { _reportGate.Release(); }
    }
}
