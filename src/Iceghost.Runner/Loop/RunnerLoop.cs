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
    TimeProvider time)
{
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FullEvery = TimeSpan.FromMinutes(5);

    private string? _lastHash;
    private DateTimeOffset _lastFull;
    private bool _force;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var started = time.GetUtcNow();
                await ReportAsync(null, ct);
                foreach (var cmd in await client.PollAsync(ct))
                {
                    // ponytail: serial execution, per-session queues if two machines' worth of work piles up
                    await ExecuteAsync(cmd, ct);
                }
                backoff = TimeSpan.FromSeconds(1);
                var wait = ScanEvery - (time.GetUtcNow() - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (UnauthorizedRunnerException) { throw; }
            catch (Exception)
            {
                try { await Task.Delay(backoff, time, ct); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task ExecuteAsync(RunnerCommand cmd, CancellationToken ct)
    {
        try { await ReportAsync(cmd, ct); } // show the session as Working while it runs
        catch (Exception e) when (e is not UnauthorizedRunnerException && !ct.IsCancellationRequested) { }

        CommandResult result;
        try { result = await adapters(cmd.Runtime).SendAsync(cmd, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { result = new CommandResult(false, e.Message); }

        await client.ResultAsync(cmd.Id, result, ct);
        _force = true;
        await ReportAsync(null, ct);
    }

    private async Task ReportAsync(RunnerCommand? running, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var sessions = scan(now);
        if (running is not null)
            sessions = sessions.Select(s => s.Runtime == running.Runtime && s.SessionId == running.SessionId
                ? s with { State = SessionState.Working } : s).ToList();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sessions, IceJson.Options))));
        var full = running is not null || _force || hash != _lastHash || now - _lastFull >= FullEvery;
        await client.ReportAsync(new RunnerReport(Environment.MachineName, RuntimeInformation.OSDescription, full ? sessions : null), ct);
        if (running is not null) { _lastHash = null; return; } // transient Working override: next report must be full
        if (full) { _lastHash = hash; _lastFull = now; }
        _force = false;
    }
}
