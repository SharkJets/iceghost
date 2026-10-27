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
    public void Foreground_flag_is_valueless()
    {
        var f = Iceghost.Runner.Program.ParseFlags(new[] { "--foreground", "--headless", "--state-dir", "d" });

        Assert.True(f.ContainsKey("--foreground"));
        Assert.True(f.ContainsKey("--headless"));
        Assert.Equal("d", f["--state-dir"]);
    }

    [Fact]
    public void ShouldDetach_only_from_a_unix_terminal()
    {
        var none = new Dictionary<string, string>();
        Assert.True(Headless.ShouldDetach(false, true, none, false));
        Assert.False(Headless.ShouldDetach(true, true, none, false));
        Assert.False(Headless.ShouldDetach(false, false, none, false));
        Assert.False(Headless.ShouldDetach(false, true, none, true));
        Assert.False(Headless.ShouldDetach(false, true, new Dictionary<string, string> { ["INVOCATION_ID"] = "x" }, false));
        Assert.False(Headless.ShouldDetach(false, true, new Dictionary<string, string> { ["XPC_SERVICE_NAME"] = "x" }, false));
        Assert.False(Headless.ShouldDetach(false, true, new Dictionary<string, string> { ["ICEGHOST_DETACHED"] = "1" }, false));
    }

    [Fact]
    public void DetachCommand_prefers_setsid_on_linux_and_nohup_on_mac()
    {
        var args = new[] { "run", "--headless" };

        var expect = new[] { "/x/ig", "run", "--headless" };
        var (f, a1) = Headless.DetachCommand(false, "/usr/bin/setsid", "/x/ig", args);
        Assert.Equal("/usr/bin/setsid", f);
        Assert.Equal(expect, a1);
        (f, a1) = Headless.DetachCommand(false, null, "/x/ig", args);
        Assert.Equal("nohup", f);
        Assert.Equal(expect, a1);
        (f, a1) = Headless.DetachCommand(true, "/usr/bin/setsid", "/x/ig", args);
        Assert.Equal("nohup", f);
        Assert.Equal(expect, a1);
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
