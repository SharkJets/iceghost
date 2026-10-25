using Iceghost.Contracts;
using Iceghost.Runner.Adapters;
using Xunit;

namespace Iceghost.Tests;

public sealed class RuntimeAdapterTests
{
    private static RunnerCommand Cmd(string text, string? cwd = null, Runtime rt = Runtime.Claude) =>
        new(1, rt, "s1", cwd ?? Path.GetTempPath(), text);

    private static readonly TimeSpan T = TimeSpan.FromMinutes(1);

    [Fact]
    public void Build_arguments_match_plan()
    {
        Assert.Equal(["--print", "--output-format", "json", "--resume", "s1"], new ClaudeAdapter([], T).BuildArguments("s1", []));
        Assert.Equal(["exec", "resume", "--json", "--skip-git-repo-check", "t1", "-"], new CodexAdapter([], T).BuildArguments("t1", []));
        Assert.Equal(["--mode", "json", "--print", "--no-extensions", "--session-id", "p1"], new PiAdapter([], T).BuildArguments("p1", []));
    }

    [Fact]
    public void Extra_args_inserted()
    {
        string[] x = ["--model", "m"];
        Assert.Equal(["--print", "--output-format", "json", "--model", "m", "--resume", "s1"], new ClaudeAdapter(x, T).BuildArguments("s1", x));
        Assert.Equal(["exec", "resume", "--json", "--skip-git-repo-check", "--model", "m", "t1", "-"], new CodexAdapter(x, T).BuildArguments("t1", x));
        Assert.Equal(["--mode", "json", "--print", "--no-extensions", "--model", "m", "--session-id", "p1"], new PiAdapter(x, T).BuildArguments("p1", x));
    }

    [Fact]
    public async Task Fake_echoes()
    {
        var r = await new FakeRuntimeAdapter(c => new(true, "echo " + c.Text)).SendAsync(Cmd("hi"), default);
        Assert.Equal(new CommandResult(true, "echo hi"), r);
    }

    [Fact]
    public async Task Missing_cwd_fails_cleanly()
    {
        foreach (var rt in Enum.GetValues<Runtime>())
        {
            var r = await RuntimeAdapters.For(rt, [], T).SendAsync(Cmd("hi", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), rt), default);
            Assert.False(r.Ok);
            Assert.Contains("working directory not found", r.Output);
        }
    }

    [Fact]
    public async Task Missing_exe_reports_not_on_path()
    {
        var r = await new ClaudeAdapter([], T, "iceghost-no-such-exe").SendAsync(Cmd("hi"), default);
        Assert.Equal(new CommandResult(false, "'iceghost-no-such-exe' not found on PATH"), r);
    }

    [Fact]
    public async Task Cmd_shim_resolved()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "fake.cmd"), "@echo {\"type\":\"result\",\"is_error\":false,\"result\":\"shim ok\"}\r\n");
            var path = ProcessAdapter.ResolveExecutable("fake", dir + ";" + Environment.GetEnvironmentVariable("PATH"));
            Assert.EndsWith("fake.cmd", path, StringComparison.OrdinalIgnoreCase);
            var r = await new ClaudeAdapter([], T, path!).SendAsync(Cmd("hi"), default);
            Assert.Equal(new CommandResult(true, "shim ok"), r);
            var bad = await new ClaudeAdapter(["a&calc"], T, path!).SendAsync(Cmd("hi"), default);
            Assert.False(bad.Ok);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("a&b"), InlineData("a|b"), InlineData("a^b"), InlineData("%PATH%"), InlineData("!x!"), InlineData("(a)"), InlineData("a\"b"), InlineData("a\nb")]
    public void Shell_metacharacters_rejected(string arg) => Assert.True(ProcessAdapter.HasShellMeta(arg));

    [Fact]
    public void Relative_path_entries_are_ignored()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory().FullName;
        var old = Directory.GetCurrentDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "evil.cmd"), "@echo x\r\n");
            Directory.SetCurrentDirectory(dir);
            Assert.Null(ProcessAdapter.ResolveExecutable("evil", "."));
        }
        finally { Directory.SetCurrentDirectory(old); Directory.Delete(dir, true); }
    }

    private static async Task<CommandResult> FailingStdout(bool codex, string stdout)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "out.txt"), stdout);
            var win = OperatingSystem.IsWindows();
            var path = Path.Combine(dir, win ? "fake.cmd" : "fake.sh");
            File.WriteAllText(path, win ? "@type \"%~dp0out.txt\"\r\n@exit /b 1\r\n" : "#!/bin/sh\ncat \"$(dirname \"$0\")/out.txt\"\nexit 1\n");
            if (!win) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            IRuntimeAdapter a = codex ? new CodexAdapter([], T, path) : new ClaudeAdapter([], T, path);
            return await a.SendAsync(Cmd("hi"), default);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Codex_failure_surfaces_turn_failed_message()
    {
        var inner = "{\\\"error\\\":{\\\"message\\\":\\\"unknown input item type\\\"}}";
        var r = await FailingStdout(true, "{\"type\":\"thread.started\"}\n{\"type\":\"error\",\"message\":\"" + inner + "\"}\n{\"type\":\"turn.failed\",\"error\":{\"message\":\"" + inner + "\"}}\n");
        Assert.False(r.Ok);
        Assert.Contains("unknown input item type", r.Output);
        Assert.DoesNotContain("thread.started", r.Output);
    }

    [Fact]
    public async Task Empty_stderr_falls_back_to_stdout_tail()
    {
        var r = await FailingStdout(false, "boom from stdout\n");
        Assert.False(r.Ok);
        Assert.Contains("boom from stdout", r.Output);
    }

    [Fact]
    public void Prompt_not_in_argv()
    {
        const string prompt = "SECRET-PROMPT";
        Assert.DoesNotContain(prompt, new ClaudeAdapter([], T).BuildArguments("s1", []));
        Assert.DoesNotContain(prompt, new CodexAdapter([], T).BuildArguments("s1", []));
        Assert.DoesNotContain(prompt, new PiAdapter([], T).BuildArguments("s1", []));
    }

    [Fact]
    public async Task Bad_session_id_rejected()
    {
        foreach (var id in new[] { "x & calc", "", "-x", "a\n" })
        {
            var r = await new ClaudeAdapter([], T, "iceghost-no-such-exe").SendAsync(Cmd("hi") with { SessionId = id }, default);
            Assert.Equal(new CommandResult(false, "invalid session id"), r);
        }
    }

    [Fact]
    public void Factory_returns_matching_adapter()
    {
        Assert.IsType<ClaudeAdapter>(RuntimeAdapters.For(Runtime.Claude, [], T));
        Assert.IsType<CodexAdapter>(RuntimeAdapters.For(Runtime.Codex, [], T));
        Assert.IsType<PiAdapter>(RuntimeAdapters.For(Runtime.Pi, [], T));
    }

    [Fact]
    public void Claude_parser()
    {
        Assert.Equal("Claude answer", OutputParsers.Claude("""{"type":"result","is_error":false,"result":"Claude answer","session_id":"s"}"""));
        Assert.Throws<FormatException>(() => OutputParsers.Claude("""{"type":"result","result":""}"""));
        var e = Assert.Throws<FormatException>(() => OutputParsers.Claude("""{"is_error":true,"result":"boom"}"""));
        Assert.Equal("boom", e.Message);
    }

    [Fact]
    public void Codex_parser_takes_last_agent_message_and_handles_crlf()
    {
        var o = "{\"type\":\"thread.started\",\"thread_id\":\"t\"}\r\n"
              + "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"first\"}}\r\n"
              + "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Codex answer\"}}\r\n"
              + "{\"type\":\"turn.completed\"}\r\n";
        Assert.Equal("Codex answer", OutputParsers.Codex(o));
        Assert.Throws<FormatException>(() => OutputParsers.Codex("""{"type":"turn.completed"}"""));
    }

    [Fact]
    public void Pi_parser_joins_text_parts_and_keeps_unicode()
    {
        var o = """
            {"type":"session","id":"p"}
            {"type":"message_end","message":{"role":"user","content":"q"}}
            {"type":"message_end","message":{"role":"assistant","content":[{"type":"thinking","text":"x"},{"type":"text","text":"Pi "},{"type":"text","text":"answer ✓"}]}}
            """;
        Assert.Equal("Pi answer ✓", OutputParsers.Pi(o));
        Assert.Throws<FormatException>(() => OutputParsers.Pi("""{"type":"agent_end","messages":[]}"""));
    }

    [Fact]
    public void Claude_adds_permission_mode() =>
        Assert.Equal(["--print", "--output-format", "json", "--permission-mode", "auto", "--resume", "s1"],
            new ClaudeAdapter([], T).BuildArguments("s1", [], "auto"));

    [Fact]
    public void Claude_unknown_permission_ignored() =>
        Assert.Equal(["--print", "--output-format", "json", "--resume", "s1"],
            new ClaudeAdapter([], T).BuildArguments("s1", [], "x & calc"));

    [Fact]
    public void Claude_extraargs_permission_wins()
    {
        string[] x = ["--permission-mode", "plan"];
        Assert.Equal(["--print", "--output-format", "json", "--permission-mode", "plan", "--resume", "s1"],
            new ClaudeAdapter(x, T).BuildArguments("s1", x, "auto"));
    }

    [Fact]
    public void Codex_adds_sandbox_mode()
    {
        Assert.Equal(["exec", "resume", "--json", "--skip-git-repo-check", "-c", "sandbox_mode=workspace-write", "t1", "-"],
            new CodexAdapter([], T).BuildArguments("t1", [], "workspace-write"));
        Assert.Equal(["exec", "resume", "--json", "--skip-git-repo-check", "t1", "-"],
            new CodexAdapter([], T).BuildArguments("t1", [], "bogus"));
    }

    [Fact]
    public void Claude_add_dir_only_when_attach_dir_set()
    {
        Assert.Equal(["--print", "--output-format", "json", "--add-dir", "/a", "--resume", "s1"], new ClaudeAdapter([], T).BuildArguments("s1", [], null, "/a"));
        Assert.DoesNotContain("--add-dir", new ClaudeAdapter([], T).BuildArguments("s1", []));
        Assert.DoesNotContain("--add-dir", new ClaudeAdapter([], T).BuildArguments("s1", [], null, "/a&calc"));
    }
}
