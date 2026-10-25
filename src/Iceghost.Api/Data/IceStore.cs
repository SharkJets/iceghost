using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iceghost.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iceghost.Api.Data;

public sealed record SessionRow(long MachineId, string Machine, Runtime Runtime, string SessionId, string Cwd, string? Title, SessionState State, DateTimeOffset LastActivity, int PendingCommands, string? LastAction = null);
public sealed record CommandRow(long Id, string Text, string Status, string? Output, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt = null, DateTimeOffset? HeartbeatAt = null);
public sealed record SessionDetail(SessionRow Session, IReadOnlyList<TranscriptEntry> Tail, IReadOnlyList<CommandRow> Commands);
public sealed record MachineRow(long Id, string Name, string? Hostname, string? Os, DateTimeOffset? LastSeen);
public sealed class MachineOfflineException(string message) : Exception(message);

public sealed class IceStore
{
    static readonly TimeSpan DeliveredTimeout = TimeSpan.FromHours(6);
    static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);
    static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(3);
    readonly DbContextOptions<IceDb> _opts;

    public IceStore(string dbPath)
    {
        _opts = new DbContextOptionsBuilder<IceDb>().UseSqlite($"Data Source={dbPath}").Options;
        // ponytail: EnsureCreated + add-missing-columns, add migrations at the next real schema change
        using var db = Db();
        db.Database.EnsureCreated();
        var cols = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Commands')").ToList();
        foreach (var col in new[] { "StartedAt", "HeartbeatAt" })
            if (!cols.Contains(col)) db.Database.ExecuteSqlRaw($"ALTER TABLE Commands ADD COLUMN {col} TEXT NULL");
        if (!db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Sessions')").ToList().Contains("LastAction"))
            db.Database.ExecuteSqlRaw("ALTER TABLE Sessions ADD COLUMN LastAction TEXT NULL");
    }

    IceDb Db() => new(_opts);

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public (long Id, string Token) CreateMachine(string name)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var db = Db();
        var m = new MachineEntity { Name = name, TokenHash = Hash(token) };
        db.Machines.Add(m);
        db.SaveChanges();
        return (m.Id, token);
    }

    public long? Authenticate(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var h = Hash(token);
        using var db = Db();
        return db.Machines.Where(m => m.TokenHash == h).Select(m => (long?)m.Id).FirstOrDefault();
    }

    public void ApplyReport(long machineId, RunnerReport r, DateTimeOffset now)
    {
        using var db = Db();
        var m = db.Machines.Find(machineId);
        if (m is null) return;
        m.Hostname = r.Hostname;
        m.Os = r.Os;
        m.LastSeen = now;
        if (r.Sessions is not null)
        {
            db.Sessions.RemoveRange(db.Sessions.Where(s => s.MachineId == machineId));
            // a re-enrolled PC leaves a stale twin (same hostname) whose sessions can never be served: drop them, keep the machine row
            var ghosts = db.Machines.AsEnumerable()
                .Where(o => o.Id != machineId && string.Equals(o.Hostname, r.Hostname, StringComparison.OrdinalIgnoreCase) && o.Hostname is not null && !(o.LastSeen > now - OnlineWindow))
                .Select(o => o.Id).ToList();
            if (ghosts.Count > 0) db.Sessions.RemoveRange(db.Sessions.Where(s => ghosts.Contains(s.MachineId)));
            foreach (var s in r.Sessions.GroupBy(s => s.SessionId).Select(g => g.Last()))
                db.Sessions.Add(new SessionEntity
                {
                    MachineId = machineId, SessionId = s.SessionId, Runtime = s.Runtime, Cwd = s.Cwd, Title = s.Title,
                    State = s.State, LastActivity = s.LastActivity, LastAction = Cap(s.LastAction),
                    TailJson = JsonSerializer.Serialize(s.Tail, IceJson.Options),
                });
        }
        db.SaveChanges();
    }

    static string? Cap(string? a) => a is { Length: > 140 } ? a[..139] + "…" : a;

    static SessionRow ToRow(SessionEntity s, string machine, int pending) =>
        new(s.MachineId, machine, s.Runtime, s.SessionId, s.Cwd, s.Title, s.State, s.LastActivity, pending, Cap(s.LastAction));

    public IReadOnlyList<SessionRow> ListSessions(DateTimeOffset now)
    {
        using var db = Db();
        Expire(db, now, null);
        var names = db.Machines.ToDictionary(m => m.Id, m => m.Name);
        var pend = db.Commands.Where(c => c.Status == "queued" || c.Status == "delivered" || c.Status == "running")
            .GroupBy(c => new { c.MachineId, c.SessionId }).Select(g => new { g.Key.MachineId, g.Key.SessionId, N = g.Count() })
            .ToList().ToDictionary(x => (x.MachineId, x.SessionId), x => x.N);
        return db.Sessions.AsEnumerable()
            .Select(s => ToRow(s, names.GetValueOrDefault(s.MachineId, ""), pend.GetValueOrDefault((s.MachineId, s.SessionId))))
            .OrderByDescending(r => r.LastActivity).ToList();
    }

    public SessionDetail? GetSession(long machineId, string sessionId, DateTimeOffset now)
    {
        using var db = Db();
        var s = db.Sessions.Find(machineId, sessionId);
        if (s is null) return null;
        Expire(db, now, machineId);
        var name = db.Machines.Where(m => m.Id == machineId).Select(m => m.Name).FirstOrDefault() ?? "";
        var cmds = db.Commands.Where(c => c.MachineId == machineId && c.SessionId == sessionId).OrderBy(c => c.Id).ToList();
        var pending = cmds.Count(c => c.Status is "queued" or "delivered" or "running");
        var tail = JsonSerializer.Deserialize<List<TranscriptEntry>>(s.TailJson, IceJson.Options) ?? [];
        return new(ToRow(s, name, pending), tail, cmds.Select(c => new CommandRow(c.Id, c.Text, c.Status, c.Output, c.CreatedAt, c.StartedAt, c.HeartbeatAt)).ToList());
    }

    public long? Enqueue(long machineId, string sessionId, string text, DateTimeOffset now)
    {
        using var db = Db();
        var s = db.Sessions.Find(machineId, sessionId);
        if (s is null) return null;
        var m = db.Machines.Find(machineId);
        if (m?.LastSeen is not { } seen || now - seen > OnlineWindow)
            throw new MachineOfflineException($"machine offline: {m?.Name} last seen {(m?.LastSeen is { } l ? l.ToString("o") : "never")}");
        var c = new CommandEntity { MachineId = machineId, SessionId = sessionId, Runtime = s.Runtime, Cwd = s.Cwd, Text = text, CreatedAt = now };
        db.Commands.Add(c);
        db.SaveChanges();
        return c.Id;
    }

    static void Expire(IceDb db, DateTimeOffset now, long? machineId)
    {
        var live = db.Commands.Where(c => (c.Status == "delivered" || c.Status == "running") && (machineId == null || c.MachineId == machineId)).AsNoTracking().ToList();
        foreach (var c in live.Where(c => c.Status == "delivered" && now - c.DeliveredAt >= DeliveredTimeout))
            db.Commands.Where(x => x.Id == c.Id && x.Status == "delivered")
                .ExecuteUpdate(u => u.SetProperty(x => x.Status, "failed").SetProperty(x => x.Output, "no result from runner"));
        foreach (var c in live.Where(c => c.Status == "running" && now - (c.HeartbeatAt ?? c.StartedAt ?? c.DeliveredAt) >= HeartbeatTimeout))
            db.Commands.Where(x => x.Id == c.Id && x.Status == "running")
                .ExecuteUpdate(u => u.SetProperty(x => x.Status, "failed").SetProperty(x => x.Output, "runner stopped responding (killed or offline)"));
    }

    public bool Heartbeat(long machineId, long commandId, DateTimeOffset now)
    {
        using var db = Db();
        // atomic: only this machine's delivered|running command; first beat flips delivered -> running
        return db.Commands.Where(c => c.Id == commandId && c.MachineId == machineId && (c.Status == "delivered" || c.Status == "running"))
            .ExecuteUpdate(u => u.SetProperty(c => c.Status, "running").SetProperty(c => c.StartedAt, c => c.StartedAt ?? now).SetProperty(c => c.HeartbeatAt, now)) == 1;
    }

    public IReadOnlyList<RunnerCommand> TakeCommands(long machineId, DateTimeOffset now, bool fresh = false)
    {
        using var db = Db();
        // a freshly started runner holds nothing: whatever it had taken died with the old process
        if (fresh)
            db.Commands.Where(c => c.MachineId == machineId && (c.Status == "delivered" || c.Status == "running"))
                .ExecuteUpdate(u => u.SetProperty(x => x.Status, "failed").SetProperty(x => x.Output, "runner restarted before finishing"));
        Expire(db, now, machineId);
        var taken = new List<CommandEntity>();
        foreach (var c in db.Commands.Where(c => c.MachineId == machineId && c.Status == "queued").OrderBy(c => c.Id).ToList())
            // atomic claim: only one concurrent poll flips queued -> delivered
            if (db.Commands.Where(x => x.Id == c.Id && x.Status == "queued").ExecuteUpdate(u => u.SetProperty(x => x.Status, "delivered").SetProperty(x => x.DeliveredAt, now)) == 1)
                taken.Add(c);
        return taken.Select(c => new RunnerCommand(c.Id, c.Runtime, c.SessionId, c.Cwd, c.Text)).ToList();
    }

    public bool Complete(long machineId, long commandId, CommandResult r, DateTimeOffset now)
    {
        using var db = Db();
        var c = db.Commands.FirstOrDefault(c => c.Id == commandId && c.MachineId == machineId && (c.Status == "delivered" || c.Status == "running"));
        if (c is null) return false;
        c.Status = r.Ok ? "done" : "failed";
        c.Output = r.Output;
        db.SaveChanges();
        return true;
    }

    /// <summary>Fails a pending command. A turn already running on the PC is not stopped; its result is dropped (Complete then returns false).</summary>
    public bool Dismiss(long machineId, string sessionId, long commandId)
    {
        using var db = Db();
        return db.Commands.Where(c => c.Id == commandId && c.MachineId == machineId && c.SessionId == sessionId && (c.Status == "queued" || c.Status == "delivered" || c.Status == "running"))
            .ExecuteUpdate(u => u.SetProperty(c => c.Status, "failed").SetProperty(c => c.Output, "dismissed")) == 1;
    }

    public IReadOnlyList<MachineRow> ListMachines()
    {
        using var db = Db();
        return db.Machines.OrderBy(m => m.Id).AsEnumerable().Select(m => new MachineRow(m.Id, m.Name, m.Hostname, m.Os, m.LastSeen)).ToList();
    }

    public bool DeleteMachine(long id)
    {
        using var db = Db();
        // explicit deletes too, so cascade never depends on the FK pragma
        db.Sessions.Where(s => s.MachineId == id).ExecuteDelete();
        db.Commands.Where(c => c.MachineId == id).ExecuteDelete();
        return db.Machines.Where(m => m.Id == id).ExecuteDelete() > 0;
    }
}
