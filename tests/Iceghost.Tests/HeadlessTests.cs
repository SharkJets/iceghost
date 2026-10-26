using Iceghost.Runner.Cli;
using Xunit;

namespace Iceghost.Tests;

public class HeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ig-headless-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Headless_flag_parses()
    {
        var f = Iceghost.Runner.Program.ParseFlags(new[] { "--server", "https://x", "--headless", "--token", "t", "--state-dir", "d" });

        Assert.True(f.ContainsKey("--headless"));
        Assert.Equal("https://x", f["--server"]);
        Assert.Equal("t", f["--token"]);
        Assert.Equal("d", f["--state-dir"]);
        Assert.False(Iceghost.Runner.Program.ParseFlags(new[] { "--token", "t" }).ContainsKey("--headless"));
    }

    [Fact]
    public void Log_rotates_over_10MiB()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "runner.log"), new string('x', 100));

        using (var w = Headless.OpenLog(_dir, 50)) w.WriteLine("hello");

        Assert.Equal(new string('x', 100), File.ReadAllText(Path.Combine(_dir, "runner.log.1")));
        var lines = File.ReadAllLines(Path.Combine(_dir, "runner.log"));
        Assert.StartsWith("--- iceghost-runner started ", lines[0]);
        Assert.Equal("hello", lines[1]);
    }
}
