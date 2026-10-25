using System.Text.Json;

namespace Iceghost.Runner.Adapters;

/// <summary>Extract the final assistant text from a CLI's structured output. Throws FormatException when there is none.</summary>
public static class OutputParsers
{
    public static string Claude(string output)
    {
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        var result = Str(root, "result");
        if (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
            throw new FormatException(result ?? "Claude Code reported an unsuccessful result.");
        return string.IsNullOrWhiteSpace(result)
            ? throw new FormatException("Claude Code output did not contain a final result.")
            : result;
    }

    public static string Codex(string output)
    {
        string? response = null;
        foreach (var root in Lines(output))
        {
            if (Str(root, "type") is "item.completed" or "item.updated"
                && root.TryGetProperty("item", out var item)
                && Str(item, "type") is "agent_message" or "assistant_message")
            {
                var text = Str(item, "text");
                if (string.IsNullOrWhiteSpace(text) && item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    text = string.Concat(c.EnumerateArray().Select(p => Str(p, "text")));
                if (!string.IsNullOrWhiteSpace(text)) response = text;
            }
        }
        return response ?? throw new FormatException("Codex output did not contain a final assistant message.");
    }

    public static string Pi(string output)
    {
        string? response = null;
        foreach (var root in Lines(output))
        {
            if (Str(root, "type") != "message_end" || !root.TryGetProperty("message", out var m) || Str(m, "role") != "assistant"
                || !m.TryGetProperty("content", out var c)) continue;
            var text = c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : c.ValueKind == JsonValueKind.Array
                    ? string.Concat(c.EnumerateArray().Where(p => Str(p, "type") == "text").Select(p => Str(p, "text")))
                    : null;
            if (!string.IsNullOrWhiteSpace(text)) response = text;
        }
        return response ?? throw new FormatException("Pi output did not contain a final assistant message.");
    }

    private static IEnumerable<JsonElement> Lines(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
