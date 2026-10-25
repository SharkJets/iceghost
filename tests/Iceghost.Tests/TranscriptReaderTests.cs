using Iceghost.Contracts;
using Iceghost.Runner.Scanning;
using Xunit;

namespace Iceghost.Tests;

public class TranscriptReaderTests
{
    static string CU(string text, string ts) =>
        $$$"""{"type":"user","sessionId":"s1","cwd":"C:\\p","timestamp":"{{{ts}}}","message":{"role":"user","content":"{{{text}}}"}}""";
    static string CA(string text, string ts, string extra = "") =>
        $$$"""{"type":"assistant","sessionId":"s1","cwd":"C:\\p","timestamp":"{{{ts}}}"{{{extra}}},"message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"{{{text}}}"}]}}""";

    [Fact]
    public void Claude_basic()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("fix it", "2026-10-25T10:00:00Z"), CA("Done.", "2026-10-25T10:01:00Z")])!;
        Assert.Equal(Runtime.Claude, s.Runtime);
        Assert.Equal("s1", s.SessionId);
        Assert.Equal("C:\\p", s.Cwd);
        Assert.Null(s.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T10:01:00Z"), s.LastActivity);
        Assert.True(s.TurnEnded);
        Assert.False(s.PendingTool);
        Assert.Equal(["user:fix it", "assistant:Done."], s.Tail.Select(e => e.Role + ":" + e.Text));
    }

    [Fact]
    public void Pi_pending_tool()
    {
        string[] lines =
        [
            """{"type":"session","id":"p1","cwd":"/w","timestamp":"2026-10-25T10:00:00Z"}""",
            """{"type":"message","timestamp":"2026-10-25T10:00:01Z","message":{"role":"assistant","stopReason":"toolUse","content":[{"type":"toolCall","id":"c1","name":"bash","arguments":{}}]}}""",
        ];
        var s = TranscriptReader.Read(Runtime.Pi, lines)!;
        Assert.True(s.PendingTool);
        Assert.False(s.TurnEnded);
        Assert.Equal("p1", s.SessionId);
        lines = [.. lines, """{"type":"message","timestamp":"2026-10-25T10:00:02Z","message":{"role":"toolResult","toolCallId":"c1","content":[{"type":"text","text":"ok"}]}}"""];
        s = TranscriptReader.Read(Runtime.Pi, lines)!;
        Assert.False(s.PendingTool);
        Assert.Equal("tool", s.Tail[^1].Role);
    }

    [Fact]
    public void Empty_is_null() => Assert.Null(TranscriptReader.Read(Runtime.Codex, []));

    [Fact]
    public void Torn_last_line_ignored()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("hi", "2026-10-25T10:00:00Z"), """{"type":"assi"""])!;
        Assert.Single(s.Tail);
    }

    [Fact]
    public void Crlf_lines()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("hi", "2026-10-25T10:00:00Z") + "\r", CA("Done.", "2026-10-25T10:01:00Z") + "\r"])!;
        Assert.Equal(2, s.Tail.Count);
        Assert.True(s.TurnEnded);
    }

    [Fact]
    public void Bom_first_line()
    {
        var s = TranscriptReader.Read(Runtime.Claude, ["\uFEFF" + CU("hi", "2026-10-25T10:00:00Z")])!;
        Assert.Single(s.Tail);
    }

    [Fact]
    public void Long_text_cut()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU(new string('x', 5000), "2026-10-25T10:00:00Z")])!;
        var t = s.Tail[0].Text;
        Assert.Equal(1000, t.Length);
        Assert.EndsWith("…", t);
    }

    [Fact]
    public void Claude_sidechain_skipped()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("hi", "2026-10-25T10:00:00Z"), CA("side", "2026-10-25T10:05:00Z", ",\"isSidechain\":true")])!;
        Assert.Single(s.Tail);
        Assert.False(s.TurnEnded);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T10:00:00Z"), s.LastActivity);
    }

    [Fact]
    public void Tail_keeps_last_10()
    {
        var lines = Enumerable.Range(0, 15).Select(i => CU("m" + i, "2026-10-25T10:00:00Z")).ToList();
        var s = TranscriptReader.Read(Runtime.Claude, lines)!;
        Assert.Equal(10, s.Tail.Count);
        Assert.Equal("m14", s.Tail[^1].Text);
    }

    [Fact]
    public void Claude_title_from_ai_title()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("hi", "2026-10-25T10:00:00Z"), """{"type":"ai-title","aiTitle":"Fix login"}"""])!;
        Assert.Equal("Fix login", s.Title);
    }

    static List<string> CodexLines(bool complete)
    {
        List<string> l =
        [
            """{"timestamp":"2026-10-25T10:00:00Z","type":"session_meta","payload":{"id":"t1","cwd":"/w"}}""",
            """{"timestamp":"2026-10-25T10:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"do it"}]}}""",
            """{"timestamp":"2026-10-25T10:00:02Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}}""",
        ];
        if (complete) l.Add("""{"timestamp":"2026-10-25T10:00:03Z","type":"event_msg","payload":{"type":"task_complete"}}""");
        return l;
    }

    [Fact]
    public void Codex_task_complete_ends_turn()
    {
        Assert.True(TranscriptReader.Read(Runtime.Codex, CodexLines(true))!.TurnEnded);
        Assert.False(TranscriptReader.Read(Runtime.Codex, CodexLines(false))!.TurnEnded);
    }

    [Fact]
    public void Codex_title_falls_back_to_first_user_text()
    {
        var s = TranscriptReader.Read(Runtime.Codex, CodexLines(true))!;
        Assert.Equal("do it", s.Title);
        Assert.Equal("t1", s.SessionId);
        Assert.Equal("/w", s.Cwd);
    }

    [Fact]
    public void Codex_function_call_pending()
    {
        var lines = CodexLines(false);
        lines.Add("""{"timestamp":"2026-10-25T10:00:04Z","type":"response_item","payload":{"type":"function_call","call_id":"c1","name":"shell"}}""");
        Assert.True(TranscriptReader.Read(Runtime.Codex, lines)!.PendingTool);
        lines.Add("""{"timestamp":"2026-10-25T10:00:05Z","type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"x"}}""");
        Assert.False(TranscriptReader.Read(Runtime.Codex, lines)!.PendingTool);
    }
}
