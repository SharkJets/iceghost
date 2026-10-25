using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Iceghost.Contracts;

namespace Iceghost.Runner.Adapters;

public interface IRuntimeAdapter
{
    Task<CommandResult> SendAsync(RunnerCommand cmd, CancellationToken ct);
}

public sealed class FakeRuntimeAdapter(Func<RunnerCommand, CommandResult> reply) : IRuntimeAdapter
{
    public Task<CommandResult> SendAsync(RunnerCommand cmd, CancellationToken ct) => Task.FromResult(reply(cmd));
}

public static class RuntimeAdapters
{
    public static IRuntimeAdapter For(Runtime runtime, IReadOnlyList<string> extraArgs, TimeSpan timeout) => runtime switch
    {
        Runtime.Claude => new ClaudeAdapter(extraArgs, timeout),
        Runtime.Codex => new CodexAdapter(extraArgs, timeout),
        _ => new PiAdapter(extraArgs, timeout),
    };
}

public abstract class ProcessAdapter(string name, string exe, IReadOnlyList<string> extraArgs, TimeSpan timeout) : IRuntimeAdapter
{
    private const int CaptureLimit = 1_048_576;
    private static readonly UTF8Encoding Utf8 = new(false);

    protected abstract IReadOnlyList<string> Args(string sessionId);
    protected abstract bool PromptOnStdin { get; }
    protected abstract string Parse(string stdout);
    protected IReadOnlyList<string> Extra => extraArgs;

    public async Task<CommandResult> SendAsync(RunnerCommand cmd, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(cmd.Cwd)) return new(false, $"{name}: working directory not found: {cmd.Cwd}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await RunAsync(cmd, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(false, $"timed out after {timeout.TotalMinutes:0.##} min");
            }
        }
        catch (Win32Exception)
        {
            return new(false, $"'{exe}' not found on PATH");
        }
        catch (OperationCanceledException)
        {
            return new(false, "cancelled");
        }
        catch (Exception e)
        {
            return new(false, $"{name}: {e.Message}");
        }
    }

    private async Task<CommandResult> RunAsync(RunnerCommand cmd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cmd.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = PromptOnStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (PromptOnStdin) psi.StandardInputEncoding = Utf8;
        foreach (var a in Args(cmd.SessionId)) psi.ArgumentList.Add(a);
        if (!PromptOnStdin) { psi.ArgumentList.Add("--"); psi.ArgumentList.Add(cmd.Text); }

        using var p = new Process { StartInfo = psi };
        p.Start();
        using var kill = ct.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
        var outTask = ReadBounded(p.StandardOutput, ct);
        var errTask = ReadBounded(p.StandardError, ct);
        if (PromptOnStdin)
        {
            try { await p.StandardInput.WriteAsync(cmd.Text.AsMemory(), ct); p.StandardInput.Close(); }
            catch (IOException) { } // process exited early; its exit code explains why
        }
        await p.WaitForExitAsync(ct);
        var stdout = await outTask;
        var stderr = await errTask;

        if (p.ExitCode != 0)
        {
            return stderr.Contains("session", StringComparison.OrdinalIgnoreCase)
                && (stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                    || stderr.Contains("invalid", StringComparison.OrdinalIgnoreCase))
                ? new(false, $"{name}: session not found")
                : new(false, $"{name} exited with code {p.ExitCode}: {Redact(stderr.Trim())}");
        }
        try { return new(true, Parse(stdout)); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return new(false, $"{name}: {e.Message}");
        }
    }

    private static string Redact(string s)
    {
        s = Regex.Replace(s, @"(?i)\bbearer\s+[^\s,;]+", "Bearer [redacted]");
        s = Regex.Replace(s, @"(?i)(api[_ -]?key|access[_ -]?token|refresh[_ -]?token|token)(\s*[:=]?\s*)([^\s,;]+)", "$1$2[redacted]");
        return s.Length > 500 ? s[..500] : s;
    }

    private static async Task<string> ReadBounded(StreamReader r, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        while ((n = await r.ReadAsync(buf.AsMemory(), ct)) > 0)
            if (sb.Length < CaptureLimit) sb.Append(buf, 0, Math.Min(n, CaptureLimit - sb.Length));
        return sb.ToString();
    }
}

public sealed class ClaudeAdapter(IReadOnlyList<string> extraArgs, TimeSpan timeout, string exe = "claude")
    : ProcessAdapter("claude", exe, extraArgs, timeout)
{
    public IReadOnlyList<string> BuildArguments(string sessionId, IReadOnlyList<string> extra) =>
        ["--print", "--output-format", "json", .. extra, "--resume", sessionId];
    protected override IReadOnlyList<string> Args(string id) => BuildArguments(id, Extra);
    protected override bool PromptOnStdin => true;
    protected override string Parse(string o) => OutputParsers.Claude(o);
}

public sealed class CodexAdapter(IReadOnlyList<string> extraArgs, TimeSpan timeout, string exe = "codex")
    : ProcessAdapter("codex", exe, extraArgs, timeout)
{
    public IReadOnlyList<string> BuildArguments(string sessionId, IReadOnlyList<string> extra) =>
        ["exec", "resume", "--json", "--skip-git-repo-check", .. extra, sessionId, "-"];
    protected override IReadOnlyList<string> Args(string id) => BuildArguments(id, Extra);
    protected override bool PromptOnStdin => true;
    protected override string Parse(string o) => OutputParsers.Codex(o);
}

/// <summary>Pi takes the prompt as the last argument after "--" (appended by SendAsync), not on stdin.</summary>
public sealed class PiAdapter(IReadOnlyList<string> extraArgs, TimeSpan timeout, string exe = "pi")
    : ProcessAdapter("pi", exe, extraArgs, timeout)
{
    public IReadOnlyList<string> BuildArguments(string sessionId, IReadOnlyList<string> extra) =>
        ["--mode", "json", "--print", "--no-extensions", .. extra, "--session-id", sessionId];
    protected override IReadOnlyList<string> Args(string id) => BuildArguments(id, Extra);
    protected override bool PromptOnStdin => false;
    protected override string Parse(string o) => OutputParsers.Pi(o);
}
