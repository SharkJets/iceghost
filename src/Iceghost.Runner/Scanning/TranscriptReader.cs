using System.Text.Json;

namespace Iceghost.Runner.Scanning;

using Iceghost.Contracts;
using Runtime = Iceghost.Contracts.Runtime; // Iceghost.Runner.Runtime namespace would shadow the enum

/// <summary>Parses transcript JSONL lines (claude, codex, pi) into a snapshot. Never throws on bad lines.</summary>
public static class TranscriptReader
{
    const int TailMax = 10, TextMax = 1000;

    sealed class Acc
    {
        public string? Id, Cwd, Title, FirstUser;
        public DateTimeOffset Last = DateTimeOffset.MinValue;
        public bool Ended;
        public readonly HashSet<string> Pending = [];
        public readonly List<TranscriptEntry> Tail = [];

        public void Add(string role, string? text, DateTimeOffset at)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (role == "user") FirstUser ??= Cut(text);
            Tail.Add(new(role, Cut(text), at));
            if (Tail.Count > TailMax) Tail.RemoveAt(0);
        }
    }

    public static SessionSnapshot? Read(Runtime runtime, IReadOnlyList<string> lines)
    {
        var a = new Acc();
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                switch (runtime)
                {
                    case Runtime.Claude: Claude(r, a); break;
                    case Runtime.Codex: Codex(r, a); break;
                    default: Pi(r, a); break;
                }
            }
            catch (JsonException) { } // torn or garbage line
        }
        if (a.Id is null) return null;
        return new(runtime, a.Id, a.Cwd ?? "", a.Title ?? (runtime == Runtime.Codex ? a.FirstUser : null),
            a.Last, a.Ended, a.Pending.Count > 0, a.Tail);
    }

    static void Claude(JsonElement r, Acc a)
    {
        var type = Str(r, "type");
        if (type == "ai-title") { a.Title = Str(r, "aiTitle") ?? a.Title; return; }
        if (r.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return;
        if (type is not ("user" or "assistant")) return;
        a.Id ??= Str(r, "sessionId");
        a.Cwd ??= Str(r, "cwd");
        var at = Stamp(r, a);
        if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;
        var content = m.TryGetProperty("content", out var c) ? c : default;
        if (content.ValueKind == JsonValueKind.String) Text(type, content.GetString(), at, a);
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var p in content.EnumerateArray())
                switch (Str(p, "type"))
                {
                    case "text": Text(type, Str(p, "text"), at, a); break;
                    case "tool_use": if (Str(p, "id") is { } id) a.Pending.Add(id); break;
                    case "tool_result":
                        if (Str(p, "tool_use_id") is { } rid) a.Pending.Remove(rid);
                        a.Add("tool", Flatten(p.TryGetProperty("content", out var tc) ? tc : default), at);
                        a.Ended = false;
                        break;
                }
        if (type == "assistant")
        {
            var stop = Str(m, "stop_reason");
            a.Ended = stop is not null && stop != "tool_use";
        }
    }

    static void Text(string role, string? text, DateTimeOffset at, Acc a)
    {
        a.Add(role, text, at);
        if (role == "user") a.Ended = false;
    }

    static void Codex(JsonElement r, Acc a)
    {
        var at = Stamp(r, a);
        if (!r.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return;
        switch (Str(r, "type"))
        {
            case "session_meta": a.Id ??= Str(p, "id"); a.Cwd ??= Str(p, "cwd"); break;
            case "event_msg":
                var et = Str(p, "type");
                if (et == "task_complete") a.Ended = true;
                else if (et == "task_started") a.Ended = false;
                break;
            case "response_item":
                switch (Str(p, "type"))
                {
                    case "message":
                        var role = Str(p, "role");
                        if (role is not ("user" or "assistant")) break;
                        a.Add(role, Flatten(p.TryGetProperty("content", out var c) ? c : default), at);
                        if (role == "user") a.Ended = false;
                        break;
                    case "function_call": if (Str(p, "call_id") is { } id) a.Pending.Add(id); break;
                    case "function_call_output":
                        if (Str(p, "call_id") is { } rid) a.Pending.Remove(rid);
                        a.Add("tool", Flatten(p.TryGetProperty("output", out var o) ? o : default), at);
                        break;
                }
                break;
        }
    }

    static void Pi(JsonElement r, Acc a)
    {
        var at = Stamp(r, a);
        var type = Str(r, "type");
        if (type == "session") { a.Id ??= Str(r, "id"); a.Cwd ??= Str(r, "cwd"); return; }
        if (type != "message" || !r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;
        var content = m.TryGetProperty("content", out var c) ? c : default;
        switch (Str(m, "role"))
        {
            case "user": a.Add("user", Flatten(content), at); a.Ended = false; break;
            case "assistant":
                a.Add("assistant", Flatten(content), at);
                if (content.ValueKind == JsonValueKind.Array)
                    foreach (var p in content.EnumerateArray())
                        if (Str(p, "type") == "toolCall" && Str(p, "id") is { } id) a.Pending.Add(id);
                a.Ended = Str(m, "stopReason") is { } s && s != "toolUse";
                break;
            case "toolResult":
                if (Str(m, "toolCallId") is { } rid) a.Pending.Remove(rid);
                a.Add("tool", Flatten(content), at);
                a.Ended = false;
                break;
        }
    }

    static DateTimeOffset Stamp(JsonElement r, Acc a)
    {
        var at = Str(r, "timestamp") is { } s && DateTimeOffset.TryParse(s, null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : a.Last;
        if (at > a.Last) a.Last = at;
        return at;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>String, or the text parts of a parts array joined by newline.</summary>
    static string? Flatten(JsonElement c) => c.ValueKind switch
    {
        JsonValueKind.String => c.GetString(),
        JsonValueKind.Array => string.Join("\n", c.EnumerateArray().Select(p => Str(p, "text")).Where(t => t is not null)),
        _ => null,
    };

    static string Cut(string t) => t.Length <= TextMax ? t : t[..(TextMax - 1)] + "…";
}
