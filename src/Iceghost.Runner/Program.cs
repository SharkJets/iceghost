using Iceghost.Contracts;
using Iceghost.Runner.Cli;
using Iceghost.Runner.Loop;
using Iceghost.Runner.Adapters;
using Iceghost.Runner.Scanning;

namespace Iceghost.Runner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "run")
        {
            Console.Error.WriteLine("usage: iceghost-runner run [--server URL --token TOKEN] [--state-dir PATH] [--headless]");
            return 2;
        }

        var flags = ParseFlags(args.Skip(1));
        var dir = flags.GetValueOrDefault("--state-dir") ?? RunnerConfig.StateDir();
        if (flags.ContainsKey("--headless")) Headless.Enable(dir);
        var server = flags.GetValueOrDefault("--server");
        var token = flags.GetValueOrDefault("--token");

        RunnerConfig? cfg;
        try
        {
            cfg = RunnerConfig.Load(dir);
        }
        catch (System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"config.json is invalid: {Path.Combine(dir, "config.json")}");
            return 2;
        }
        if (server is not null && token is not null)
        {
            cfg = (cfg ?? new RunnerConfig(server, token, new())) with { Server = server, Token = token };
            RunnerConfig.Save(dir, cfg);
        }
        else if (cfg is null)
        {
            Console.Error.WriteLine("run with --server and --token first");
            return 2;
        }

        var maxTurn = TimeSpan.FromMinutes(cfg.MaxTurnMinutes); // hard cap handed to the adapter; TimeoutMinutes is the idle limit
        using var http = new HttpClient { BaseAddress = new Uri(cfg.Server) };
        var client = new ServerClient(http, cfg.Token);
        var roots = ScanRoots.Default(cfg.Roots);
        foreach (var rt in new[] { Runtime.Claude, Runtime.Codex, Runtime.Pi })
            foreach (var r in roots.For(rt))
                Console.WriteLine($"{rt}: {r}{(Directory.Exists(r) ? "" : " (missing)")}");
        foreach (var rt in new[] { Runtime.Claude, Runtime.Codex, Runtime.Pi })
        {
            var key = rt.ToString().ToLowerInvariant();
            var o = cfg.Exe?.GetValueOrDefault(rt);
            var shown = o ?? ProcessAdapter.ResolveExecutable(key);
            Console.WriteLine($"{key}: {shown ?? "not found"}{(o is not null && !(Path.IsPathRooted(o) && File.Exists(o)) ? " (missing)" : "")}");
        }
        var attach = Path.Combine(dir, "attachments");
        try { foreach (var f in Directory.EnumerateFiles(attach).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7))) File.Delete(f); } catch { } // best effort
        var scanner = new SessionScanner(roots, TranscriptReader.Read);
        var loop = new RunnerLoop(client, scanner.Scan,
            runtime => RuntimeAdapters.For(runtime, cfg.ExtraArgs.GetValueOrDefault(runtime) ?? Array.Empty<string>(), maxTurn, cfg.Exe?.GetValueOrDefault(runtime)),
            TimeProvider.System, Math.Max(1, cfg.MaxParallel), TimeSpan.FromMinutes(cfg.TimeoutMinutes), attach);

        using var cts = new CancellationTokenSource();
        try
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
        }
        catch (Exception) when (flags.ContainsKey("--headless")) { } // no console to hook once detached

        try
        {
            await loop.RunAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 0;
        }
        catch (UnauthorizedRunnerException)
        {
            Console.Error.WriteLine("token rejected: re-enroll this machine");
            return 3;
        }
    }

    // ponytail: flag pairs plus --headless, no CLI library; add one when subcommands grow
    public static Dictionary<string, string> ParseFlags(IEnumerable<string> args)
    {
        var list = args.ToList();
        var flags = new Dictionary<string, string>();
        for (var i = 0; i < list.Count; i++)
            if (list[i] == "--headless") flags[list[i]] = "true";
            else if (i + 1 < list.Count) flags[list[i]] = list[++i];
        return flags;
    }
}
