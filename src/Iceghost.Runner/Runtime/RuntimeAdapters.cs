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
    /// <summary>exe: config override. Must be an absolute path to an existing file (no PATH search), else every command fails with that message.</summary>
    public static IRuntimeAdapter For(Runtime runtime, IReadOnlyList<string> extraArgs, TimeSpan timeout, string? exe = null)
    {
        if (exe is not null && !(Path.IsPathRooted(exe) && File.Exists(exe)))
            return new FakeRuntimeAdapter(_ => new(false, $"configured exe for {runtime.ToString().ToLowerInvariant()} not found: {exe}"));
        return runtime switch
        {
            Runtime.Claude => exe is null ? new ClaudeAdapter(extraArgs, timeout) : new ClaudeAdapter(extraArgs, timeout, exe),
            Runtime.Codex => exe is null ? new CodexAdapter(extraArgs, timeout) : new CodexAdapter(extraArgs, timeout, exe),
            _ => exe is null ? new PiAdapter(extraArgs, timeout) : new PiAdapter(extraArgs, timeout, exe),
        };
    }
}

public abstract class ProcessAdapter(string name, string exe, IReadOnlyList<string> extraArgs, TimeSpan timeout) : IRuntimeAdapter
{
    private const int CaptureLimit = 1_048_576;
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex SessionIdRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly char[] ShellMeta = ['"', '&', '|', '<', '>', '^', '%', '!', '(', ')', '\n', '\r', '\0'];
    public static bool HasShellMeta(string a) => a.IndexOfAny(ShellMeta) >= 0;

    /// <summary>Usual install dirs services don't have on PATH (absolute only; missing ones are skipped at lookup).</summary>
    public static IReadOnlyList<string> KnownDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> dirs;
        if (OperatingSystem.IsWindows())
            dirs = [Path.Combine(home, ".local", "bin"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                    Path.Combine(home, ".bun", "bin"), Path.Combine(home, ".volta", "bin")];
        else
        {
            var nvm = Path.Combine(home, ".nvm", "versions", "node");
            var newest = Directory.Exists(nvm)
                ? Directory.GetDirectories(nvm).OrderByDescending(d => Version.TryParse(Path.GetFileName(d).TrimStart('v'), out var v) ? v : new Version(0, 0)).Select(d => Path.Combine(d, "bin")).Take(1)
                : Enumerable.Empty<string>();
            dirs = [Path.Combine(home, ".local", "bin"), Path.Combine(home, ".claude", "local"), Path.Combine(home, ".npm-global", "bin"),
                    Path.Combine(home, ".bun", "bin"), Path.Combine(home, ".volta", "bin"), .. newest, "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"];
        }
        return dirs.Where(Path.IsPathRooted).ToList();
    }

    /// <summary>Directory to put first on the child's PATH so shims that call `node` next to themselves work under a minimal service PATH.</summary>
    public static string ChildPath(string exeDir, string? currentPath)
    {
        if (string.IsNullOrEmpty(currentPath)) return exeDir;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return currentPath.Split(Path.PathSeparator)[0].Equals(exeDir, cmp) ? currentPath : exeDir + Path.PathSeparator + currentPath;
    }

    private static bool IsFile(string p) =>
        File.Exists(p) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(p) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

    /// <summary>
    /// Resolves an executable name via PATH, then fallbackDirs (default KnownDirs()); PATHEXT on Windows, .exe first. Returns null when not found.
    /// npm-installed CLIs are .cmd shims on Windows, which Process.Start cannot launch by bare name.
    /// </summary>
    public static string? ResolveExecutable(string exe, string? pathVar = null, IEnumerable<string>? fallbackDirs = null)
    {
        var win = OperatingSystem.IsWindows();
        string[] exts = !win ? [] : [".exe", .. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(e => !e.Equals(".exe", StringComparison.OrdinalIgnoreCase))];
        var dirs = exe.IndexOfAny(['\\', '/']) >= 0
            ? new[] { "" }
            : (pathVar ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Concat(fallbackDirs ?? KnownDirs());
        foreach (var d in dirs)
        {
            if (d.Length > 0 && !Path.IsPathRooted(d)) continue; // relative PATH entries would resolve against the (untrusted) session cwd inside cmd.exe
            var b = d.Length == 0 ? exe : Path.Combine(d, exe);
            if ((!win || Path.HasExtension(b)) && IsFile(b)) return b;
            foreach (var x in exts) if (IsFile(b + x)) return b + x;
        }
        return null;
    }

    private string NotFound() =>
        $"'{exe}' not found on PATH or in: {string.Join(", ", KnownDirs())}. Set \"exe\": {{ \"{name}\": \"<full path>\" }} in config.json";

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
            return new(false, NotFound());
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
        if (resolved is null) return new(false, NotFound());
        resolved = Prefer(resolved);
        var o = await Exec(cmd.Cwd, resolved, Args(cmd.SessionId, cmd.Permission, cmd.AttachDir), cmd.Text, ct);
        if (o.Error is not null) return new(false, o.Error);
        var (code, stdout, stderr) = (o.Code, o.Out, o.Err);

        if (code != 0)
        {
            if (await OnFailure(cmd, resolved, stdout, stderr, ct) is { } alt) return alt;
            return stderr.Contains("session", StringComparison.OrdinalIgnoreCase)
                && (stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                    || stderr.Contains("invalid", StringComparison.OrdinalIgnoreCase))
                ? new(false, $"{name}: session not found")
                : new(false, $"{name} exited with code {code}: {Redact(FailureMessage(stdout, stderr))}");
        }
        try { return new(true, Parse(stdout)); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return new(false, $"{name}: {e.Message}");
        }
    }

    /// <summary>Hook: swap the resolved path for a better one (e.g. the real binary behind a shim).</summary>
    protected virtual string Prefer(string resolved) => resolved;

    /// <summary>Hook: a non-zero exit may be handled by a follow-up; null = report the normal failure.</summary>
    protected virtual Task<CommandResult?> OnFailure(RunnerCommand cmd, string resolved, string stdout, string stderr, CancellationToken ct) =>
        Task.FromResult<CommandResult?>(null);

    protected static bool IsShim(string path) =>
        OperatingSystem.IsWindows() && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

    protected sealed record Outcome(int Code, string Out, string Err, string? Error = null);

    /// <summary>Runs one process with the prompt on stdin. Shims go through cmd.exe (args checked for metacharacters), everything else uses ArgumentList.</summary>
    protected async Task<Outcome> Exec(string cwd, string resolved, IReadOnlyList<string> args, string stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            StandardInputEncoding = Utf8,
        };
        if (Path.IsPathRooted(resolved) && Path.GetDirectoryName(resolved) is { Length: > 0 } exeDir)
            psi.Environment["PATH"] = ChildPath(exeDir, psi.Environment.TryGetValue("PATH", out var cur) ? cur : null);
        if (IsShim(resolved))
        {
            if (extraArgs.Any(HasShellMeta)) return new(0, "", "", "extra args contain shell metacharacters");
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
        try { await p.StandardInput.WriteAsync(stdin.AsMemory(), ct); p.StandardInput.Close(); }
        catch (IOException) { } // process exited early; its exit code explains why
        await p.WaitForExitAsync(ct);
        return new(p.ExitCode, await outTask, await errTask);
    }

    /// <summary>Failure reason: stderr, else the stdout tail (some CLIs report errors on stdout).</summary>
    protected virtual string FailureMessage(string stdout, string stderr) =>
        stderr.Trim().Length > 0 ? stderr.Trim() : Tail(stdout);

    protected static string Tail(string s) { s = s.Trim(); return s.Length > 500 ? s[^500..] : s; }

    protected static string Redact(string s)
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

    public const string BusyMessage = "Codex is still running this session in the background (another Codex window or the Codex app). Close it, or wait for it to finish, then send again.";
    public const string QueuedMessage = "Codex is still busy with this session in the background, so your message was queued. It runs after the current turn; the reply will appear in the conversation.";
    const int MaxQueueText = 16_000; // command-line length ceiling on Windows is ~32k

    public enum Step { Done, Queue, Busy }

    /// <summary>codex keeps tasks alive in a shared app-server daemon; exec resume then fails with a thread-store conflict.</summary>
    public static bool IsBusy(string text) =>
        text.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase)
        || text.Contains("thread-store conflict", StringComparison.OrdinalIgnoreCase);

    /// <summary>The prompt goes on the command line for queue, so only a directly-launched exe (no cmd.exe) may do it.</summary>
    public static Step NextStep(bool failed, string output, bool native) =>
        !failed || !IsBusy(output) ? Step.Done : native ? Step.Queue : Step.Busy;

    // --message=<text> (not a separate arg) so text starting with '-' is not read as a flag
    public static IReadOnlyList<string> BuildQueueArguments(string sessionId, string text, string? permission = null) =>
        ["queue", "--thread", sessionId, "--message=" + text,
         .. permission is not null && Array.IndexOf(Modes, permission) >= 0 ? new[] { "-s", permission } : []];

    /// <summary>The real binary next to an npm shim (filesystem only); null if not found.</summary>
    public static string? NativeExe(string shimPath)
    {
        static IEnumerable<string> Sub(string dir, string pattern) => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir, pattern) : [];
        var root = Path.Combine(Path.GetDirectoryName(shimPath) ?? "", "node_modules", "@openai", "codex");
        return Sub(Path.Combine(root, "node_modules", "@openai"), "codex-win32-*").SelectMany(d => Sub(Path.Combine(d, "vendor"), "*"))
                   .Select(d => Path.Combine(d, "bin", "codex.exe")).FirstOrDefault(File.Exists)
            ?? Sub(Path.Combine(root, "vendor"), "*").Select(d => Path.Combine(d, "codex", "codex.exe")).FirstOrDefault(File.Exists);
    }

    protected override string Prefer(string resolved) => IsShim(resolved) && NativeExe(resolved) is { } n ? n : resolved;

    protected override async Task<CommandResult?> OnFailure(RunnerCommand cmd, string resolved, string stdout, string stderr, CancellationToken ct)
    {
        switch (NextStep(true, stdout + "\n" + stderr, !IsShim(resolved)))
        {
            case Step.Busy: return new(false, BusyMessage);
            case Step.Queue:
                if (cmd.Text.Length > MaxQueueText) return new(false, BusyMessage);
                var q = await Exec(cmd.Cwd, resolved, BuildQueueArguments(cmd.SessionId, cmd.Text, cmd.Permission), "", ct);
                return q.Code == 0 ? new(true, QueuedMessage) : new(false, $"codex queue failed: {Redact(Tail(q.Err.Trim().Length > 0 ? q.Err : q.Out))}");
            default: return null;
        }
    }

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
