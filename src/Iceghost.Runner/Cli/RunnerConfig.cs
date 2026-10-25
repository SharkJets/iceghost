using System.Text.Json;
using Iceghost.Contracts;

namespace Iceghost.Runner.Cli;

public record RunnerConfig(string Server, string Token, Dictionary<Runtime, string[]> ExtraArgs, int TimeoutMinutes = 30, Dictionary<Runtime, string[]>? Roots = null)
{
    private const string FileName = "config.json";

    public static string StateDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iceghost Runner");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "Iceghost Runner");
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        return string.IsNullOrEmpty(xdg)
            ? Path.Combine(home, ".local", "state", "iceghost-runner")
            : Path.Combine(xdg, "iceghost-runner");
    }

    public static RunnerConfig? Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<RunnerConfig>(File.ReadAllText(path), IceJson.Options)
            : null;
    }

    public static void Save(string dir, RunnerConfig c)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        // write-then-move so a crash mid-write never leaves a torn config.json
        var tmp = path + ".tmp";
        File.Delete(tmp); // a stale tmp would keep its old mode
        var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600 from creation
        using (var fs = new FileStream(tmp, opts))
            fs.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(c, IceJson.Options)));
        File.Move(tmp, path, overwrite: true);
    }
}
