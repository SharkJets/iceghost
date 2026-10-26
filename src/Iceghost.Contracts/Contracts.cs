using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iceghost.Contracts;

public enum Runtime { Claude, Codex, Pi }
public enum SessionState { Working, NeedsInput, Done, Stalled }
public sealed record TranscriptEntry(string Role, string Text, DateTimeOffset At); // Role: user|assistant|tool
public sealed record SessionSnapshot(Runtime Runtime, string SessionId, string Cwd, string? Title, DateTimeOffset LastActivity, bool TurnEnded, bool PendingTool, IReadOnlyList<TranscriptEntry> Tail)
{
    /// <summary>Permission mode the user chose for the session (null = default).</summary>
    public string? Permission { get; init; }
    public string? LastAction { get; init; }
}
public sealed record SessionReport(Runtime Runtime, string SessionId, string Cwd, string? Title, SessionState State, DateTimeOffset LastActivity, IReadOnlyList<TranscriptEntry> Tail)
{
    public string? Permission { get; init; }
    public string? LastAction { get; init; }
}
public sealed record RunnerReport(string Hostname, string Os, IReadOnlyList<SessionReport>? Sessions) // null = heartbeat, no change
{
    /// <summary>Runner build version (release tag without "v", or "dev"); old runners omit it.</summary>
    public string? RunnerVersion { get; init; }
}
public sealed record RunnerCommand(long Id, Runtime Runtime, string SessionId, string Cwd, string Text)
{
    public string? Permission { get; init; }
    public string? LastAction { get; init; }
    /// <summary>Optional screenshot: base64 bytes + mime. Old runners ignore them.</summary>
    public string? Image { get; init; }
    public string? ImageType { get; init; }
    /// <summary>Runner-local (the server never sets it; the runner overwrites any incoming value): dir the agent may read the attachment from.</summary>
    public string? AttachDir { get; init; }
}
public sealed record CommandResult(bool Ok, string Output);
public static class IceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
public static class Attachment
{
    /// <summary>File extension for an allowlisted image type whose magic bytes match, else null.</summary>
    public static string? Ext(string? type, ReadOnlySpan<byte> b) => type switch
    {
        "image/png" when b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) => ".png",
        "image/jpeg" when b.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) => ".jpg",
        "image/gif" when b.StartsWith("GIF8"u8) => ".gif",
        "image/webp" when b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) => ".webp",
        _ => null,
    };
}
