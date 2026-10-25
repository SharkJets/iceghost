using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Iceghost.Tests;

public class SecretScanTests
{
    private static readonly Regex[] Strong =
    [
        new(@"ghp_[A-Za-z0-9]{30,}"),
        new(@"github_pat_"),
        new(@"sk-ant-[A-Za-z0-9-]{20,}"),
        new(@"sk-[A-Za-z0-9]{40,}"),
        new(@"AKIA[0-9A-Z]{16}"),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
        new(@"xox[baprs]-"),
    ];

    private static readonly Regex Generic = new(@"(?i)(password|secret|token)\s*[:=]\s*[""']?[A-Za-z0-9+/_-]{16,}");
    private static readonly string[] Ignored = ["REPLACE_ME", "secrets.", "example", "${{"];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Iceghost.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Iceghost.slnx not found");
    }

    private static string[]? GitFiles(string root)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "ls-files -z") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    [Fact]
    public void No_secrets_in_tracked_files()
    {
        var root = FindRepoRoot();
        var files = GitFiles(root);
        if (files is null) return; // git unavailable or not a repo: nothing to scan

        var hits = new List<string>();
        foreach (var rel in files)
        {
            var path = Path.Combine(root, rel);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length >= 1_000_000) continue;
            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0) >= 0) continue; // binary
            var isTest = rel.Replace('\\', '/').StartsWith("tests/", StringComparison.Ordinal);
            if (rel.Replace('\\', '/') == "tests/Iceghost.Tests/SecretScanTests.cs") continue; // holds the patterns themselves
            var n = 0;
            foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                n++;
                if (Strong.Any(r => r.IsMatch(line)))
                    hits.Add($"{rel}:{n}");
                else if (!isTest && Generic.IsMatch(line) && !Ignored.Any(i => line.Contains(i, StringComparison.OrdinalIgnoreCase)))
                    hits.Add($"{rel}:{n}");
            }
        }
        Assert.True(hits.Count == 0, "Possible secrets (values not shown): " + string.Join(", ", hits));
    }
}
