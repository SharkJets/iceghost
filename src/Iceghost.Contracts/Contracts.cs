using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iceghost.Contracts;

public enum Runtime { Claude, Codex, Pi }
public enum SessionState { Working, NeedsInput, Done, Stalled }
public sealed record TranscriptEntry(string Role, string Text, DateTimeOffset At); // Role: user|assistant|tool
public sealed record SessionSnapshot(Runtime Runtime, string SessionId, string Cwd, string? Title, DateTimeOffset LastActivity, bool TurnEnded, bool PendingTool, IReadOnlyList<TranscriptEntry> Tail);
public sealed record SessionReport(Runtime Runtime, string SessionId, string Cwd, string? Title, SessionState State, DateTimeOffset LastActivity, IReadOnlyList<TranscriptEntry> Tail);
public sealed record RunnerReport(string Hostname, string Os, IReadOnlyList<SessionReport>? Sessions); // null = heartbeat, no change
public sealed record RunnerCommand(long Id, Runtime Runtime, string SessionId, string Cwd, string Text);
public sealed record CommandResult(bool Ok, string Output);
public static class IceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
