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
    private static readonly Regex SessionIdRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly char[] ShellMeta = ['"', '&', '|', '<', '>', '^', '%', '!', '(', ')', '\n', '\r', '\0'];
    public static bool HasShellMeta(string a) => a.IndexOfAny(ShellMeta) >= 0;

    /// <summary>
    /// Resolves an executable name via PATH (+ PATHEXT on Windows, .exe first). Returns null when not found.
    /// npm-installed CLIs are .cmd shims on Windows, which Process.Start cannot launch by bare name.
    /// </summary>
    public static string? ResolveExecutable(string exe, string? pathVar = null)
    {
        if (!OperatingSystem.IsWindows()) return exe;
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] exts = [".exe", .. pathExt.Where(e => !e.Equals(".exe", StringComparison.OrdinalIgnoreCase))];
        var dirs = exe.IndexOfAny(['\\', '/']) >= 0
            ? new[] { "" }
            : (pathVar ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var d in dirs)
        {
            if (d.Length > 0 && !Path.IsPathRooted(d)) continue; // relative PATH entries would resolve against the (untrusted) session cwd inside cmd.exe
            var b = d.Length == 0 ? exe : Path.Combine(d, exe);
            if (Path.HasExtension(b) && File.Exists(b)) return b;
            foreach (var x in exts) if (File.Exists(b + x)) return b + x;
        }
        return null;
    }

    private static string Quote(string a) => a.Length > 0 && a.IndexOfAny([' ', '\t']) < 0 ? a : $"\"{a}\"";

    protected abstract IReadOnlyList<string> Args(string sessionId, string? permission, string? attachDir);
    protected abstract string Parse(string stdout);
    protected IReadOnlyList<string> Extra => extraArgs;

    public async Task<CommandResult> SendAsync(RunnerCommand cmd, CancellationToken ct)
    {
        try
        {
            if (!SessionIdRx.IsMatch(cmd.SessionId ?? "")) return new(false, "invalid session id");
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
        var resolved = ResolveExecutable(exe);
        if (resolved is null) return new(false, $"'{exe}' not found on PATH");
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cmd.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            StandardInputEncoding = Utf8,
        };
        var args = Args(cmd.SessionId, cmd.Permission, cmd.AttachDir);
        if (OperatingSystem.IsWindows() && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            if (extraArgs.Any(HasShellMeta)) return new(false, "extra args contain shell metacharacters");
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/d /s /c \"\"{resolved}\" {string.Join(' ', args.Select(Quote))}\"";
        }
        else
        {
            psi.FileName = resolved;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        p.Start();
        using var kill = ct.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
        var outTask = ReadBounded(p.StandardOutput, ct);
        var errTask = ReadBounded(p.StandardError, ct);
        try { await p.StandardInput.WriteAsync(cmd.Text.AsMemory(), ct); p.StandardInput.Close(); }
        catch (IOException) { } // process exited early; its exit code explains why
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
                : new(false, $"{name} exited with code {p.ExitCode}: {Redact(FailureMessage(stdout, stderr))}");
        }
        try { return new(true, Parse(stdout)); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return new(false, $"{name}: {e.Message}");
        }
    }

    /// <summary>Failure reason: stderr, else the stdout tail (some CLIs report errors on stdout).</summary>
    protected virtual string FailureMessage(string stdout, string stderr) =>
        stderr.Trim().Length > 0 ? stderr.Trim() : Tail(stdout);

    protected static string Tail(string s) { s = s.Trim(); return s.Length > 500 ? s[^500..] : s; }

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
    static readonly string[] Modes = ["acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan"];
    public IReadOnlyList<string> BuildArguments(string sessionId, IReadOnlyList<string> extra, string? permission = null, string? attachDir = null) =>
        ["--print", "--output-format", "json", .. extra,
         .. permission is not null && Array.IndexOf(Modes, permission) >= 0 && !extra.Contains("--permission-mode")
             ? new[] { "--permission-mode", permission } : [],
         .. attachDir is not null && !HasShellMeta(attachDir) ? new[] { "--add-dir", attachDir } : [],
         "--resume", sessionId];
    protected override IReadOnlyList<string> Args(string id, string? permission, string? attachDir) => BuildArguments(id, Extra, permission, attachDir);
    protected override string Parse(string o) => OutputParsers.Claude(o);
}

public sealed class CodexAdapter(IReadOnlyList<string> extraArgs, TimeSpan timeout, string exe = "codex")
    : ProcessAdapter("codex", exe, extraArgs, timeout)
{
    static readonly string[] Modes = ["read-only", "workspace-write", "danger-full-access"];
    // unquoted: not valid TOML so codex takes it as a raw string; avoids quotes on the cmd.exe path
    public IReadOnlyList<string> BuildArguments(string sessionId, IReadOnlyList<string> extra, string? permission = null) =>
        ["exec", "resume", "--json", "--skip-git-repo-check", .. extra,
         .. permission is not null && Array.IndexOf(Modes, permission) >= 0 ? new[] { "-c", "sandbox_mode=" + permission } : [],
         sessionId, "-"];
    protected override IReadOnlyList<string> Args(string id, string? permission, string? attachDir) => BuildArguments(id, Extra, permission);
    protected override string Parse(string o) => OutputParsers.Codex(o);

    /// <summary>Codex reports failures as JSONL on stdout: last turn.failed, else last error event.</summary>
    protected override string FailureMessage(string stdout, string stderr)
    {
        if (stderr.Trim().Length > 0) return stderr.Trim();
        string? failed = null, error = null;
        foreach (var line in stdout.Split('\n'))
        {
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(line.Trim());
                var r = d.RootElement;
                var type = r.TryGetProperty("type", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
                if (type == "turn.failed" && r.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var fm) && fm.GetString() is { } f) failed = f;
                else if (type == "error" && r.TryGetProperty("message", out var m) && m.GetString() is { } s) error = s;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
        }
        var msg = failed ?? error;
        if (msg is null) return Tail(stdout);
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(msg);
            if (d.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var im) && im.GetString() is { } inner) msg = inner;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
        return msg;
    }
}

/// <summary>Pi reads the prompt from stdin like the others (never on the command line).</summary>
public sealed class PiAdapter(IReadOnlyList<string> extraArgs, TimeSpan timeout, string exe = "pi")
    : ProcessAdapter("pi", exe, extraArgs, timeout)
{
    public IReadOnlyList<string> BuildArguments(string sessionId, IReadOnlyList<string> extra) =>
        ["--mode", "json", "--print", "--no-extensions", .. extra, "--session-id", sessionId];
    protected override IReadOnlyList<string> Args(string id, string? permission, string? attachDir) => BuildArguments(id, Extra);
    protected override string Parse(string o) => OutputParsers.Pi(o);
}
