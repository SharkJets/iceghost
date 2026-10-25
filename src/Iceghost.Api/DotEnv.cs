namespace Iceghost.Api;

/// <summary>Tiny .env loader. Real environment variables always win; values are never logged.</summary>
public static class DotEnv
{
    /// <summary>Loads ICEGHOST_ENV_FILE if set, else .env in startDir (default cwd) or in a parent (up to 3) that holds Iceghost.slnx.</summary>
    public static void Load(string? startDir = null)
    {
        var explicitPath = Environment.GetEnvironmentVariable("ICEGHOST_ENV_FILE");
        var path = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath : Find(startDir ?? Directory.GetCurrentDirectory());
        if (path is not null && File.Exists(path)) LoadFile(path);
    }

    private static string? Find(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        var own = Path.Combine(dir.FullName, ".env");
        if (File.Exists(own)) return own;
        for (var i = 0; i < 3 && dir.Parent is not null; i++)
        {
            dir = dir.Parent;
            if (File.Exists(Path.Combine(dir.FullName, "Iceghost.slnx")) && File.Exists(Path.Combine(dir.FullName, ".env")))
                return Path.Combine(dir.FullName, ".env");
        }
        return null;
    }

    public static void LoadFile(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (val.Length >= 2 && (val[0] == '"' || val[0] == '\'') && val[^1] == val[0]) val = val[1..^1];
            if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, val);
        }
    }
}
