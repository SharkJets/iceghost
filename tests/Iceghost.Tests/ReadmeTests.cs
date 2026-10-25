namespace Iceghost.Tests;

public class ReadmeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Iceghost.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Iceghost.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Readme_headings()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "README.md"));
        foreach (var heading in new[] { "## Deploy", "## Enroll a machine", "## Run at startup", "## Desktop app", "## Phone" })
            Assert.Contains(heading, lines);
    }
}
