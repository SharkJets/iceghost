using Iceghost.Contracts;

namespace Iceghost.Runner.Scanning;

public static class StateClassifier
{
    public static SessionState Classify(SessionSnapshot s, DateTimeOffset now)
    {
        var age = now - s.LastActivity;
        if (s.TurnEnded)
        {
            var last = s.Tail.LastOrDefault(e => e.Role == "assistant")?.Text.Trim();
            return last is not null && last.EndsWith('?') ? SessionState.NeedsInput : SessionState.Done;
        }
        if (s.PendingTool && age > TimeSpan.FromMinutes(2)) return SessionState.NeedsInput;
        return age > TimeSpan.FromMinutes(10) ? SessionState.Stalled : SessionState.Working;
    }
}
