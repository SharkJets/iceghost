using Microsoft.EntityFrameworkCore;
using Iceghost.Contracts;

namespace Iceghost.Api.Data;

public sealed class MachineEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? Hostname { get; set; }
    public string? Os { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
}

public sealed class SessionEntity
{
    public long MachineId { get; set; }
    public string SessionId { get; set; } = "";
    public Runtime Runtime { get; set; }
    public string Cwd { get; set; } = "";
    public string? Title { get; set; }
    public SessionState State { get; set; }
    public DateTimeOffset LastActivity { get; set; }
    public string TailJson { get; set; } = "[]";
    public string? LastAction { get; set; }
}

public sealed class CommandEntity
{
    public long Id { get; set; }
    public long MachineId { get; set; }
    public string SessionId { get; set; } = "";
    public Runtime Runtime { get; set; }
    public string Cwd { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued|delivered|running|done|failed
    public string? Output { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public byte[]? Image { get; set; } // only while pending; cleared when the command finishes
    public string? ImageType { get; set; }
}

public sealed class IceDb(DbContextOptions<IceDb> options) : DbContext(options)
{
    public DbSet<MachineEntity> Machines => Set<MachineEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<CommandEntity> Commands => Set<CommandEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MachineEntity>().HasIndex(m => m.TokenHash).IsUnique();
        b.Entity<SessionEntity>().HasKey(s => new { s.MachineId, s.SessionId });
        b.Entity<SessionEntity>().HasOne<MachineEntity>().WithMany().HasForeignKey(s => s.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CommandEntity>().HasOne<MachineEntity>().WithMany().HasForeignKey(c => c.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CommandEntity>().HasIndex(c => new { c.MachineId, c.Status });
    }
}
