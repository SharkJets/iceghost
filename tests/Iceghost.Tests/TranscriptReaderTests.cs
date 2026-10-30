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

    static string CTool(string input, string ts) =>
        $$$"""{"type":"assistant","sessionId":"s1","cwd":"C:\\p","timestamp":"{{{ts}}}","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{{{input}}} }]}}""";

    [Fact]
    public void Claude_last_action_tool_use_command() =>
        Assert.Equal("Bash: dotnet test", TranscriptReader.Read(Runtime.Claude, [CU("go", "2026-10-25T10:00:00Z"), CTool("""{"command":"dotnet test"}""", "2026-10-25T10:01:00Z")])!.LastAction);

    [Fact]
    public void Claude_last_action_text_after_tool() =>
        Assert.Equal("All green", TranscriptReader.Read(Runtime.Claude, [CU("go", "2026-10-25T10:00:00Z"), CTool("""{"command":"x"}""", "2026-10-25T10:01:00Z"), CA("All   green\\nmore", "2026-10-25T10:02:00Z")])!.LastAction);

    [Fact]
    public void Codex_last_action_function_call()
    {
        string[] lines =
        [
            """{"type":"session_meta","timestamp":"2026-10-25T10:00:00Z","payload":{"id":"c1","cwd":"/w"}}""",
            """{"type":"response_item","timestamp":"2026-10-25T10:00:01Z","payload":{"type":"function_call","name":"shell","arguments":"{\"command\":[\"ls\",\"-la\"]}","call_id":"k1"}}""",
        ];
        Assert.Equal("shell: ls -la", TranscriptReader.Read(Runtime.Codex, lines)!.LastAction);
    }

    [Fact]
    public void Pi_last_action_tool_call()
    {
        string[] lines =
        [
            """{"type":"session","id":"p1","cwd":"/w","timestamp":"2026-10-25T10:00:00Z"}""",
            """{"type":"message","timestamp":"2026-10-25T10:00:01Z","message":{"role":"assistant","stopReason":"toolUse","content":[{"type":"toolCall","id":"c1","name":"read","arguments":{"path":"a.txt"}}]}}""",
        ];
        Assert.Equal("read: a.txt", TranscriptReader.Read(Runtime.Pi, lines)!.LastAction);
    }

    [Fact]
    public void Last_action_truncated_140()
    {
        var a = TranscriptReader.Read(Runtime.Claude, [CU("go", "2026-10-25T10:00:00Z"), CTool($$$"""{"command":"{{{new string('x', 300)}}}"}""", "2026-10-25T10:01:00Z")])!.LastAction!;
        Assert.Equal(140, a.Length);
        Assert.EndsWith("…", a);
    }

    [Fact]
    public void Claude_basic()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("fix it", "2026-10-25T10:00:00Z"), CA("Done.", "2026-10-25T10:01:00Z")])!;
        Assert.Equal(Runtime.Claude, s.Runtime);
        Assert.Equal("s1", s.SessionId);
        Assert.Equal("C:\\p", s.Cwd);
        Assert.Equal("fix it", s.Title);
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

    static string LastAssistantText(params string[] lines) =>
        TranscriptReader.Read(Runtime.Claude, lines)!.Tail.Last(e => e.Role == "assistant").Text;

    [Fact]
    public void Latest_assistant_reply_kept_up_to_16000()
    {
        var t = LastAssistantText(CU("go", "2026-10-25T10:00:00Z"), CA(new string('x', 5000), "2026-10-25T10:01:00Z"));
        Assert.Equal(5000, t.Length);
        Assert.DoesNotContain("…", t);
    }

    [Fact]
    public void Latest_reply_capped_at_16000()
    {
        var t = LastAssistantText(CU("go", "2026-10-25T10:00:00Z"), CA(new string('x', 20000), "2026-10-25T10:01:00Z"));
        Assert.Equal(16000, t.Length);
        Assert.EndsWith("…", t);
    }

    [Fact]
    public void Older_entries_still_capped_at_1000()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("go", "2026-10-25T10:00:00Z"), CA(new string('x', 5000), "2026-10-25T10:01:00Z"),
            CA("short", "2026-10-25T10:02:00Z")])!;
        Assert.Equal(1000, s.Tail[1].Text.Length);
        Assert.EndsWith("…", s.Tail[1].Text);
        Assert.Equal("short", s.Tail[2].Text);
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
    public void Tail_keeps_40_entries()
    {
        var lines = Enumerable.Range(0, 60).Select(i => i % 2 == 0 ? CU("m" + i, "2026-10-25T10:00:00Z") : CA("m" + i, "2026-10-25T10:00:00Z")).ToList();
        var s = TranscriptReader.Read(Runtime.Claude, lines)!;
        Assert.Equal(40, s.Tail.Count);
        Assert.Equal("m20", s.Tail[0].Text);
        Assert.Equal("m59", s.Tail[^1].Text);
    }

    [Fact]
    public void Tail_budget_drops_oldest()
    {
        var lines = Enumerable.Range(0, 40).Select(i => CU(new string((char)('a' + i % 26), 1000), "2026-10-25T10:00:00Z")).ToList();
        lines.Add(CA(new string('z', 16000), "2026-10-25T10:01:00Z"));
        var s = TranscriptReader.Read(Runtime.Claude, lines)!;
        Assert.True(s.Tail.Sum(e => e.Text.Length) <= 40000);
        Assert.True(s.Tail.Count < 40);
        Assert.Equal(16000, s.Tail[^1].Text.Length);
    }

    [Fact]
    public void Claude_title_from_ai_title()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("hi", "2026-10-25T10:00:00Z"), """{"type":"ai-title","aiTitle":"Fix login"}"""])!;
        Assert.Equal("Fix login", s.Title);
    }

    [Fact]
    public void Claude_title_falls_back_to_first_user_text()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("fix it", "2026-10-25T10:00:00Z")])!;
        Assert.Equal("fix it", s.Title);
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

    [Fact]
    public void Claude_harness_user_lines_skipped()
    {
        string[] lines =
        [
            CU("fix it", "2026-10-25T10:00:00Z"), CA("Done.", "2026-10-25T10:01:00Z"),
            """{"type":"user","isMeta":true,"sessionId":"s1","timestamp":"2026-10-25T10:02:00Z","message":{"role":"user","content":"caveat"}}""",
            CU("<command-name>/exit</command-name>", "2026-10-25T10:02:01Z"),
            CU("<local-command-stdout>bye</local-command-stdout>", "2026-10-25T10:02:02Z"),
            """{"type":"user","sessionId":"s1","timestamp":"2026-10-25T10:02:03Z","message":{"role":"user","content":[{"type":"text","text":"<command-message>x</command-message>"}]}}""",
            """{"type":"user","isCompactSummary":true,"sessionId":"s1","timestamp":"2026-10-25T10:02:04Z","message":{"role":"user","content":"summary"}}""",
        ];
        var s = TranscriptReader.Read(Runtime.Claude, lines)!;
        Assert.True(s.TurnEnded);
        Assert.Equal(["user:fix it", "assistant:Done."], s.Tail.Select(e => e.Role + ":" + e.Text));
    }

    static void AssertSkipped(string injected)
    {
        var s = TranscriptReader.Read(Runtime.Claude, [CU("fix it", "2026-10-25T10:00:00Z"), CA("Done.", "2026-10-25T10:01:00Z"),
            CU(injected, "2026-10-25T10:02:00Z")])!;
        Assert.True(s.TurnEnded);
        Assert.Equal(["user:fix it", "assistant:Done."], s.Tail.Select(e => e.Role + ":" + e.Text));
    }

    [Fact]
    public void Claude_task_notification_skipped() => AssertSkipped("  <task-notification>x</task-notification>");

    [Fact]
    public void Claude_system_reminder_skipped() => AssertSkipped("\\n<system-reminder>x</system-reminder>");

    [Fact]
    public void Codex_synthetic_user_messages_skipped()
    {
        var lines = CodexLines(true);
        lines.Insert(1, """{"timestamp":"2026-10-25T10:00:00Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<user_instructions>x</user_instructions>"}]}}""");
        lines.Insert(1, """{"timestamp":"2026-10-25T10:00:00Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>x</environment_context>"}]}}""");
        lines.Add("""{"timestamp":"2026-10-25T10:00:04Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>y</environment_context>"}]}}""");
        var s = TranscriptReader.Read(Runtime.Codex, lines)!;
        Assert.Equal("do it", s.Title);
        Assert.True(s.TurnEnded);
        Assert.Equal(["user:do it", "assistant:ok"], s.Tail.Select(e => e.Role + ":" + e.Text));
    }

    [Fact]
    public void Codex_task_complete_error_shown()
    {
        var lines = CodexLines(false);
        lines.Add("""{"timestamp":"2026-10-25T10:00:03Z","type":"event_msg","payload":{"type":"task_complete","last_agent_message":null,"error":{"message":"quota exceeded"}}}""");
        var s = TranscriptReader.Read(Runtime.Codex, lines)!;
        Assert.True(s.TurnEnded);
        Assert.Equal("assistant:error: quota exceeded", s.Tail[^1].Role + ":" + s.Tail[^1].Text);
    }

    [Fact]
    public void Pi_title_falls_back_to_first_user_text()
    {
        string[] lines =
        [
            """{"type":"session","id":"p1","cwd":"/w","timestamp":"2026-10-25T10:00:00Z"}""",
            $$$"""{"type":"message","timestamp":"2026-10-25T10:00:01Z","message":{"role":"user","content":[{"type":"text","text":"{{{new string('y', 200)}}}"}]}}""",
        ];
        var t = TranscriptReader.Read(Runtime.Pi, lines)!.Title!;
        Assert.Equal(80, t.Length);
        Assert.StartsWith("yyy", t);
    }

    // Mirrors a real claude transcript: a tool_use never answered because the user interrupted and typed a new prompt.
    [Fact]
    public void Claude_interrupted_tool_use_not_pending()
    {
        string[] lines =
        [
            CU("go", "2026-10-25T10:00:00Z"),
            """{"type":"assistant","sessionId":"s1","timestamp":"2026-10-25T10:00:01Z","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}]}}""",
            """{"type":"file-history-snapshot","messageId":"m1"}""",
            CU("never mind, do this instead", "2026-10-25T10:30:00Z"),
        ];
        Assert.False(TranscriptReader.Read(Runtime.Claude, lines)!.PendingTool);
        lines = [.. lines[..2], CA("Stopped.", "2026-10-25T10:31:00Z")];
        Assert.False(TranscriptReader.Read(Runtime.Claude, lines)!.PendingTool);
    }

    [Fact]
    public void Pi_new_prompt_clears_unanswered_calls()
    {
        string[] lines =
        [
            """{"type":"session","id":"p1","cwd":"/w","timestamp":"2026-10-25T10:00:00Z"}""",
            """{"type":"message","timestamp":"2026-10-25T10:00:01Z","message":{"role":"assistant","stopReason":"toolUse","content":[{"type":"toolCall","id":"c1"},{"type":"toolCall","id":"c2"}]}}""",
            """{"type":"message","timestamp":"2026-10-25T10:00:02Z","message":{"role":"toolResult","toolCallId":"c1","content":[{"type":"text","text":"ok"}]}}""",
            """{"type":"message","timestamp":"2026-10-25T10:00:03Z","message":{"role":"user","content":[{"type":"text","text":"stop"}]}}""",
        ];
        Assert.False(TranscriptReader.Read(Runtime.Pi, lines)!.PendingTool);
    }

    static string PM(string mode) => $$"""{"type":"permission-mode","permissionMode":"{{mode}}","sessionId":"s1"}""";
    static string CUm(string entry, string mode) =>
        $$$"""{"type":"user","sessionId":"s1","cwd":"C:\\p","timestamp":"2026-10-25T10:00:00Z","entrypoint":"{{{entry}}}","permissionMode":"{{{mode}}}","message":{"role":"user","content":"hi"}}""";

    [Fact]
    public void Claude_permission_from_last_mode_line()
    {
        var s = TranscriptReader.Read(Runtime.Claude, [PM("plan"), CUm("sdk-cli", "default"), PM("auto"), CA("ok", "2026-10-25T10:01:00Z")])!;
        Assert.Equal("auto", s.Permission);
        Assert.Equal("acceptEdits", TranscriptReader.Read(Runtime.Claude, [CUm("cli", "acceptEdits")])!.Permission);
    }

    [Fact]
    public void Claude_permission_ignores_sdk_cli_default()
    {
        Assert.Equal("auto", TranscriptReader.Read(Runtime.Claude, [CUm("cli", "auto"), CUm("sdk-cli", "default")])!.Permission);
        Assert.Null(TranscriptReader.Read(Runtime.Claude, [CUm("sdk-cli", "default")])!.Permission);
        Assert.Null(TranscriptReader.Read(Runtime.Claude, [CUm("cli", "default")])!.Permission);
    }

    [Fact]
    public void Claude_permission_from_sdk_cli_non_default()
    {
        Assert.Equal("acceptEdits", TranscriptReader.Read(Runtime.Claude, [CUm("sdk-cli", "acceptEdits"), CUm("sdk-cli", "acceptEdits")])!.Permission);
    }

    [Fact]
    public void Codex_permission_from_first_turn_context()
    {
        var lines = CodexLines(true);
        lines.Insert(1, """{"timestamp":"2026-10-25T10:00:00Z","type":"turn_context","payload":{"sandbox_policy":{"type":"workspace-write"}}}""");
        lines.Add("""{"timestamp":"2026-10-25T10:00:04Z","type":"turn_context","payload":{"sandbox_policy":{"type":"read-only"}}}""");
        Assert.Equal("workspace-write", TranscriptReader.Read(Runtime.Codex, lines)!.Permission);
        Assert.Null(TranscriptReader.Read(Runtime.Codex, CodexLines(true))!.Permission);
    }
}
