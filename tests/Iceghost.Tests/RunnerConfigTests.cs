using System.IO;
using Iceghost.Contracts;
using Iceghost.Runner.Cli;
using Xunit;

namespace Iceghost.Tests;

public class RunnerConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ig-cfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        var c = new RunnerConfig("https://x", "t", new Dictionary<Runtime, string[]>(), 30);

        RunnerConfig.Save(_dir, c);
        var loaded = RunnerConfig.Load(_dir);

        Assert.NotNull(loaded);
        Assert.Equal(c.Server, loaded.Server);
        Assert.Equal(c.Token, loaded.Token);
        Assert.Empty(loaded.ExtraArgs);
        Assert.Equal(c.TimeoutMinutes, loaded.TimeoutMinutes);
    }

    [Fact]
    public void Load_missing_returns_null()
    {
        Assert.Null(RunnerConfig.Load(_dir));
    }

    [Fact]
    public void Unix_mode_0600()
    {
        // xunit 2.9 has no runtime Skip; on Windows this body is a no-op pass
        if (OperatingSystem.IsWindows()) return;

        RunnerConfig.Save(_dir, new RunnerConfig("https://x", "t", new Dictionary<Runtime, string[]>()));

        var mode = File.GetUnixFileMode(Path.Combine(_dir, "config.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
