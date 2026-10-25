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
    public void Roots_roundtrip()
    {
        var c = new RunnerConfig("https://x", "t", new(), 30, new() { [Runtime.Pi] = ["/a", "/b"] });
        RunnerConfig.Save(_dir, c);
        Assert.Equal(["/a", "/b"], RunnerConfig.Load(_dir)!.Roots![Runtime.Pi]);
        RunnerConfig.Save(_dir, c with { Roots = null });
        Assert.Null(RunnerConfig.Load(_dir)!.Roots);
    }

    [Fact]
    public void Parallel_and_turn_cap_roundtrip_and_old_config_loads()
    {
        RunnerConfig.Save(_dir, new RunnerConfig("https://x", "t", new(), 30, null, 2, 90));
        var c = RunnerConfig.Load(_dir)!;
        Assert.Equal((2, 90), (c.MaxParallel, c.MaxTurnMinutes));

        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"server":"https://x","token":"t","extraArgs":{},"timeoutMinutes":30}""");
        c = RunnerConfig.Load(_dir)!;
        Assert.Equal((4, 360), (c.MaxParallel, c.MaxTurnMinutes));
    }

    [Fact]
    public async Task Corrupt_config_exits_2_with_message()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ not json");
        var err = new StringWriter();
        var old = Console.Error;
        Console.SetError(err);
        try
        {
            Assert.Equal(2, await Iceghost.Runner.Program.Main(["run", "--state-dir", _dir]));
        }
        finally { Console.SetError(old); }
        Assert.Contains("config.json is invalid: " + Path.Combine(_dir, "config.json"), err.ToString());
    }

    [Fact]
    public void Save_replaces_stale_tmp_and_leaves_no_tmp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json.tmp"), "stale");
        RunnerConfig.Save(_dir, new RunnerConfig("https://x", "t", new Dictionary<Runtime, string[]>()));
        Assert.False(File.Exists(Path.Combine(_dir, "config.json.tmp")));
        Assert.Equal("t", RunnerConfig.Load(_dir)!.Token);
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
