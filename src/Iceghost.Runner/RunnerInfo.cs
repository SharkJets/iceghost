using System.Reflection;

namespace Iceghost.Runner;

public static class RunnerInfo
{
    /// <summary>Release version stamped by runner-release.yml (InformationalVersion, "+commit" stripped); unstamped builds are "dev".</summary>
    public static readonly string Version = Strip(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Strip(string? v)
    {
        var s = v?.Split('+')[0];
        return string.IsNullOrWhiteSpace(s) ? "dev" : s;
    }
}
