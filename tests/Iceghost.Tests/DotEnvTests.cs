using Iceghost.Api;
using Xunit;

namespace Iceghost.Tests;

[Collection("ApiEnv")]
public class DotEnvTests : IDisposable
{
    private static readonly string[] Keys = ["DOTENV_T_A", "DOTENV_T_B", "DOTENV_T_C", "DOTENV_T_D", "DOTENV_T_E", "ICEGHOST_ENV_FILE"];
    private readonly Dictionary<string, string?> _saved = Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
    private readonly string _dir = Directory.CreateTempSubdirectory("dotenv").FullName;

    public DotEnvTests()
    {
        foreach (var k in Keys) Environment.SetEnvironmentVariable(k, null);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Parses_quotes_comments_export()
    {
        var f = Path.Combine(_dir, ".env");
        File.WriteAllText(f, "# comment\n\nDOTENV_T_A=plain\nDOTENV_T_B=\"dq val\"\nexport DOTENV_T_C='sq val'\n  DOTENV_T_D = spaced \nbadline\n");
        DotEnv.LoadFile(f);
        Assert.Equal("plain", Environment.GetEnvironmentVariable("DOTENV_T_A"));
        Assert.Equal("dq val", Environment.GetEnvironmentVariable("DOTENV_T_B"));
        Assert.Equal("sq val", Environment.GetEnvironmentVariable("DOTENV_T_C"));
        Assert.Equal("spaced", Environment.GetEnvironmentVariable("DOTENV_T_D"));
    }

    [Fact]
    public void Env_var_wins()
    {
        var f = Path.Combine(_dir, ".env");
        File.WriteAllText(f, "DOTENV_T_A=fromfile\n");
        Environment.SetEnvironmentVariable("DOTENV_T_A", "real");
        DotEnv.LoadFile(f);
        Assert.Equal("real", Environment.GetEnvironmentVariable("DOTENV_T_A"));
    }

    [Fact]
    public void Missing_file_is_noop()
    {
        DotEnv.Load(_dir);
        Environment.SetEnvironmentVariable("ICEGHOST_ENV_FILE", Path.Combine(_dir, "nope.env"));
        DotEnv.Load(_dir);
    }

    [Fact]
    public void Load_finds_env_in_start_dir_and_honors_override_file()
    {
        File.WriteAllText(Path.Combine(_dir, ".env"), "DOTENV_T_A=cwd\n");
        DotEnv.Load(_dir);
        Assert.Equal("cwd", Environment.GetEnvironmentVariable("DOTENV_T_A"));

        var other = Path.Combine(_dir, "other.env");
        File.WriteAllText(other, "DOTENV_T_E=explicit\n");
        Environment.SetEnvironmentVariable("ICEGHOST_ENV_FILE", other);
        DotEnv.Load(_dir);
        Assert.Equal("explicit", Environment.GetEnvironmentVariable("DOTENV_T_E"));
    }
}
