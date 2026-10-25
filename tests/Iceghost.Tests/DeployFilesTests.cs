using Xunit;

namespace Iceghost.Tests;

public class DeployFilesTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Iceghost.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Iceghost.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    [Fact]
    public void Dockerfile_entrypoint()
    {
        Assert.Contains("ENTRYPOINT [\"./Iceghost.Api\"]", Read("Dockerfile"));
    }

    [Fact]
    public void Manifest_host()
    {
        Assert.Contains("host: iceghost.example.com", Read("deploy/kubernetes.yaml"));
    }

    [Fact]
    public void Manifest_recreate()
    {
        Assert.Contains("type: Recreate", Read("deploy/kubernetes.yaml"));
    }

    [Fact]
    public void Compose_uses_env_file()
    {
        var text = Read("docker-compose.yml");
        Assert.Contains("env_file:", text);
        Assert.Contains(".env", text);
        Assert.DoesNotContain("ICEGHOST_ADMIN_PASSWORD=", text);
    }

    [Fact]
    public void Gitignore_ignores_env()
    {
        var lines = Read(".gitignore").Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains(".env", lines);
        Assert.Contains("!.env.example", lines);
    }

    [Fact]
    public void Release_six_rids()
    {
        var text = Read(".github/workflows/runner-release.yml");
        foreach (var rid in new[] { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64" })
            Assert.Contains(rid, text);
    }

    [Fact]
    public void Ci_builds_six_rids()
    {
        var text = Read(".github/workflows/ci.yml");
        foreach (var rid in new[] { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64" })
            Assert.Contains(rid, text);
        Assert.Contains("dotnet test Iceghost.slnx", text);
    }
}
