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
}
