using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iceghost.Contracts;
using Iceghost.Runner.Adapters;
using Iceghost.Runner.Loop;
using Iceghost.Runner.Scanning;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Iceghost.Tests;

[Collection("ApiEnv")]
[Trait("Category", "Slow")]
public sealed class EndToEndTests : IDisposable
{
    const string Pw = "correct horse battery";
    static readonly object EnvLock = new();
    readonly string dir = Path.Combine(Path.GetTempPath(), "ig-e2e-" + Guid.NewGuid().ToString("N"));
    readonly WebApplicationFactory<Program> factory;
    readonly HttpClient ui;
    readonly long machineId;
    readonly string token;

    public EndToEndTests()
    {
        lock (EnvLock)
        {
            Environment.SetEnvironmentVariable("ICEGHOST_DATA", Path.Combine(dir, "data"));
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ICEGHOST_ADMIN_PASSWORD", Pw));
            factory.CreateClient().Dispose(); // build host while env is set
        }
        ui = factory.CreateClient();
        var login = ui.PostAsJsonAsync("/api/login", new { password = Pw }).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        // cookie is Secure and the test server is http, so carry it by hand
        ui.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("ig_auth=")).Split(';')[0]);

        var m = ui.PostAsJsonAsync("/api/machines", new { name = "desk" }).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Created, m.StatusCode);
        var j = m.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
        machineId = j.GetProperty("id").GetInt64();
        token = j.GetProperty("token").GetString()!;
    }

    public void Dispose()
    {
        ui.Dispose();
        factory.Dispose();
        try { Directory.Delete(dir, true); } catch { }
    }

    RunnerLoop StartableLoop()
    {
        var claude = Path.Combine(dir, "claude", "p");
        Directory.CreateDirectory(claude);
        var ts = DateTimeOffset.UtcNow.ToString("o");
        File.WriteAllLines(Path.Combine(claude, "s1.jsonl"), [
            $$$"""{"type":"user","sessionId":"s1","cwd":"C:\\p","timestamp":"{{{ts}}}","message":{"role":"user","content":"fix it"}}""",
            $$$"""{"type":"assistant","sessionId":"s1","cwd":"C:\\p","timestamp":"{{{ts}}}","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Done."}]}}""",
        ]);
        var scanner = new SessionScanner(new ScanRoots(Path.Combine(dir, "claude"), Path.Combine(dir, "none1"), Path.Combine(dir, "none2")), TranscriptReader.Read);
        var client = new ServerClient(factory.CreateClient(), token);
        return new RunnerLoop(client, scanner.Scan, _ => new FakeRuntimeAdapter(c => new(true, "echo " + c.Text)), TimeProvider.System);
    }

    static async Task<T> Until<T>(Func<Task<T?>> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } v) return v;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException("timed out waiting for " + what);
    }

    [Fact]
    public async Task Message_round_trip()
    {
        using var cts = new CancellationTokenSource();
        var run = StartableLoop().RunAsync(cts.Token);
        try
        {
            await Until(async () =>
            {
                var rows = await ui.GetFromJsonAsync<JsonElement>("/api/sessions", IceJson.Options);
                return rows.EnumerateArray().Any(r => r.GetProperty("sessionId").GetString() == "s1" && r.GetProperty("state").GetString() == "done") ? "ok" : null;
            }, "s1 done");

            var post = await ui.PostAsJsonAsync($"/api/sessions/{machineId}/s1/messages", new { text = "hi" });
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

            var output = await Until(async () =>
            {
                var d = await ui.GetFromJsonAsync<JsonElement>($"/api/sessions/{machineId}/s1", IceJson.Options);
                return d.GetProperty("commands").EnumerateArray()
                    .Where(c => c.GetProperty("status").GetString() == "done")
                    .Select(c => c.GetProperty("output").GetString()).FirstOrDefault();
            }, "command done");
            Assert.Equal("echo hi", output);
        }
        finally
        {
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(40)); // returns quietly on cancel
        }
    }

    [Fact]
    public async Task Revoked_machine_stops_runner()
    {
        using var cts = new CancellationTokenSource();
        var run = StartableLoop().RunAsync(cts.Token);
        try
        {
            await Until(async () =>
            {
                var ms = await ui.GetFromJsonAsync<JsonElement>("/api/machines", IceJson.Options);
                return ms.EnumerateArray().Any(m => m.GetProperty("lastSeen").ValueKind == JsonValueKind.String) ? "ok" : null;
            }, "runner seen");

            Assert.Equal(HttpStatusCode.NoContent, (await ui.DeleteAsync($"/api/machines/{machineId}")).StatusCode);
            // the in-flight long poll (25 s) must finish before the next report hits 401
            await Assert.ThrowsAsync<UnauthorizedRunnerException>(() => run.WaitAsync(TimeSpan.FromSeconds(40)));
        }
        finally { cts.Cancel(); }
    }
}
