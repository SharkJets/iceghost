using System.Net;
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
    TimeSpan? idleTimeout = null,
    string? attachDir = null)
{
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FullEvery = TimeSpan.FromMinutes(5);
    // ponytail: reports replace the whole list server-side, so a live transcript would resend every session each poll;
    // activity-only changes (tail, LastActivity, LastAction) are batched to this period. Per-session deltas need a server API change.
    private static readonly TimeSpan ActivityEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);

    private readonly string _attach = Path.GetFullPath(attachDir ?? Path.Combine(Path.GetTempPath(), "iceghost-attachments"));
    private readonly TimeSpan _idle = idleTimeout ?? TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _slots = new(Math.Max(1, maxParallel));
    private readonly SemaphoreSlim _reportGate = new(1);
    private readonly object _gate = new();
    private readonly Dictionary<(Runtime, string), Queue<RunnerCommand>> _queues = [];
    private readonly HashSet<(Runtime, string)> _running = [];
    private readonly List<Task> _workers = [];
    private volatile UnauthorizedRunnerException? _fatal;

    private string? _lastHash;
    private string? _lastKey;
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
            await PostResultAsync(cmd.Id, new CommandResult(false, "unknown session on this machine"), ct);
            return;
        }
        // dismissed/expired while queued behind another command (or across a server restart): don't run it
        try
        {
            if (!await client.HeartbeatAsync(cmd.Id, ct))
            {
                Console.Error.WriteLine($"command {cmd.Id} no longer active on server (dismissed or expired), skipping");
                return;
            }
        }
        catch (Exception e) when (e is not UnauthorizedRunnerException && !ct.IsCancellationRequested) { } // server may be down: run anyway
        cmd = cmd with { Runtime = known.Runtime, Cwd = known.Cwd, Permission = known.Permission, AttachDir = null }; // AttachDir is runner-local, never trusted from the server
        if (cmd.Image is not null)
        {
            string? fail = null;
            try
            {
                var bytes = Convert.FromBase64String(cmd.Image);
                if (Attachment.Ext(cmd.ImageType, bytes) is not { } ext) fail = "bad attachment";
                else
                {
                    Directory.CreateDirectory(_attach);
                    var path = Path.Combine(_attach, cmd.Id + ext);
                    await File.WriteAllBytesAsync(path, bytes, ct);
                    cmd = cmd with
                    {
                        Text = (cmd.Text.Trim().Length > 0 ? cmd.Text : "See the attached screenshot.") + "\n\n[Screenshot attached: " + path + "]",
                        Image = null, AttachDir = _attach,
                    };
                }
            }
            catch (FormatException) { fail = "bad attachment"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { fail = $"attachment not saved: {e.Message}"; }
            if (fail is not null) { await PostResultAsync(cmd.Id, new CommandResult(false, fail), ct); return; }
        }
        var key = (cmd.Runtime, cmd.SessionId);

        lock (_gate) _running.Add(key);
        try
        {
            try { await ReportAsync(ct); } // show the session as Working while it runs
            catch (Exception e) when (e is not UnauthorizedRunnerException && !ct.IsCancellationRequested) { }

            using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var idled = false;
            var gone = false;
            var monitor = MonitorAsync(cmd, turn, () => idled = true, () => gone = true, ct);
            CommandResult result;
            try { result = await adapters(cmd.Runtime).SendAsync(cmd, turn.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { result = new CommandResult(false, e.Message); }
            turn.Cancel();
            await monitor;
            if (gone) Console.Error.WriteLine($"command {cmd.Id} dismissed on server, stopped"); // nobody is waiting for a result
            else
            {
                if (idled) result = new CommandResult(false, $"stopped: no activity for {_idle.TotalMinutes:0.##} min");
                await PostResultAsync(cmd.Id, result, ct);
            }
        }
        finally { lock (_gate) _running.Remove(key); }
        _force = true;
        await ReportAsync(ct);
    }

    private static readonly TimeSpan[] ResultRetries = [.. new[] { 2.0, 5, 10, 20, 30, 60 }.Select(TimeSpan.FromSeconds)];

    // Rides out a server outage/reboot; 404 means the command is gone (dismissed/expired) so there is nothing to deliver.
    private async Task PostResultAsync(long id, CommandResult r, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            try { await client.ResultAsync(id, r, ct); return; }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return; }
            catch (Exception e) when (e is not UnauthorizedRunnerException && !ct.IsCancellationRequested)
            {
                if (i == ResultRetries.Length) { Console.Error.WriteLine($"result for command {id} not delivered, giving up: {e.Message}"); return; }
                await Task.Delay(ResultRetries[i], time, ct);
            }
        }
    }

    // Heartbeat every 15 s while the adapter runs, cancel the turn when the transcript has gone quiet for _idle or the server dropped the command.
    private async Task MonitorAsync(RunnerCommand cmd, CancellationTokenSource turn, Action onIdle, Action onGone, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        try
        {
            while (true)
            {
                try { if (!await client.HeartbeatAsync(cmd.Id, turn.Token)) { onGone(); turn.Cancel(); return; } }
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
            var sorted = sessions.OrderBy(s => s.Runtime).ThenBy(s => s.SessionId, StringComparer.Ordinal).ToList(); // scan order is by mtime: a write reorders
            var hash = Hash(sorted);
            var key = Hash(sorted.Select(s => s with { LastActivity = default, Tail = [], LastAction = null }).ToList()); // set, states, titles
            var full = running.Count > 0 || _force || key != _lastKey || now - _lastFull >= FullEvery
                || (hash != _lastHash && now - _lastFull >= ActivityEvery);
            await client.ReportAsync(new RunnerReport(Environment.MachineName, RuntimeInformation.OSDescription, full ? sessions : null), ct);
            if (running.Count > 0) { _lastHash = _lastKey = null; return; } // transient Working override: next report must be full
            if (full) { _lastHash = hash; _lastKey = key; _lastFull = now; }
            _force = false;
        }
        finally { _reportGate.Release(); }
    }

    private static string Hash(IReadOnlyList<SessionReport> s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s, IceJson.Options))));
}
