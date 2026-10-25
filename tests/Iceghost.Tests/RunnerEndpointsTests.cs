using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Iceghost.Api.Data;
using Iceghost.Api.Endpoints;
using Iceghost.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Iceghost.Tests;

public sealed class RunnerEndpointsTests : IDisposable
{
    static readonly object Gate = new();
    readonly string dir = Path.Combine(Path.GetTempPath(), "ig-" + Guid.NewGuid().ToString("N"));
    readonly WebApplicationFactory<Program> factory;
    readonly HttpClient http;
    readonly IceStore store;

    public RunnerEndpointsTests()
    {
        lock (Gate)
        {
            Environment.SetEnvironmentVariable("ICEGHOST_DATA", dir);
            Environment.SetEnvironmentVariable("ICEGHOST_ADMIN_PASSWORD", "test-password-123456");
            factory = new WebApplicationFactory<Program>();
            http = factory.CreateClient(); // starts host, which reads the env var
        }
        store = factory.Services.GetRequiredService<IceStore>();
    }

    public void Dispose()
    {
        http.Dispose();
        factory.Dispose();
        try { Directory.Delete(dir, true); } catch { }
    }

    HttpRequestMessage Req(HttpMethod m, string url, string? token, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body };
        if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    static SessionReport Sess() => new(Runtime.Claude, "s1", "C:\\p", null, SessionState.Done, DateTimeOffset.UtcNow, []);

    [Fact]
    public async Task No_bearer_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/runner/commands")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/runner/report", new RunnerReport("h", "linux", null), IceJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/runner/commands/1/result", new CommandResult(true, "x"), IceJson.Options)).StatusCode);
    }

    [Fact]
    public async Task Wrong_token_401()
    {
        store.CreateMachine("a");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(HttpMethod.Get, "/api/runner/commands", "nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(HttpMethod.Get, "/api/runner/commands", ""))).StatusCode);
    }

    [Fact]
    public async Task Report_then_poll_and_result()
    {
        var (id, token) = store.CreateMachine("a");
        var rep = JsonContent.Create(new RunnerReport("h", "linux", [Sess()]), options: IceJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Req(HttpMethod.Post, "/api/runner/report", token, rep))).StatusCode);
        var cid = store.Enqueue(id, "s1", "hi", DateTimeOffset.UtcNow)!.Value;

        var resp = await http.SendAsync(Req(HttpMethod.Get, "/api/runner/commands", token));
        var cmds = await resp.Content.ReadFromJsonAsync<RunnerCommand[]>(IceJson.Options);
        Assert.Equal(new RunnerCommand(cid, Runtime.Claude, "s1", "C:\\p", "hi"), Assert.Single(cmds!));

        var res = JsonContent.Create(new CommandResult(true, "echo hi"), options: IceJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Req(HttpMethod.Post, $"/api/runner/commands/{cid}/result", token, res))).StatusCode);
    }

    [Fact]
    public async Task Result_other_machine_404()
    {
        var (a, ta) = store.CreateMachine("a");
        var (_, tb) = store.CreateMachine("b");
        store.ApplyReport(a, new RunnerReport("h", "linux", [Sess()]), DateTimeOffset.UtcNow);
        var cid = store.Enqueue(a, "s1", "hi", DateTimeOffset.UtcNow)!.Value;
        store.TakeCommands(a, DateTimeOffset.UtcNow);

        var res = () => JsonContent.Create(new CommandResult(true, "x"), options: IceJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req(HttpMethod.Post, $"/api/runner/commands/{cid}/result", tb, res()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req(HttpMethod.Post, "/api/runner/commands/999/result", ta, res()))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Req(HttpMethod.Post, $"/api/runner/commands/{cid}/result", ta, res()))).StatusCode);
    }

    [Fact]
    public async Task Poll_returns_early_when_enqueued()
    {
        var (id, token) = store.CreateMachine("a");
        store.ApplyReport(id, new RunnerReport("h", "linux", [Sess()]), DateTimeOffset.UtcNow);
        var sw = Stopwatch.StartNew();
        var poll = http.SendAsync(Req(HttpMethod.Get, "/api/runner/commands?waitSeconds=25", token));
        await Task.Delay(300);
        store.Enqueue(id, "s1", "hi", DateTimeOffset.UtcNow);
        var cmds = await (await poll).Content.ReadFromJsonAsync<RunnerCommand[]>(IceJson.Options);
        Assert.Single(cmds!);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task Wait_clamped()
    {
        Assert.Equal(0, RunnerEndpoints.ClampWait(-5));
        Assert.Equal(50, RunnerEndpoints.ClampWait(1000));
        Assert.Equal(7, RunnerEndpoints.ClampWait(7));
        var (_, token) = store.CreateMachine("a");
        var sw = Stopwatch.StartNew();
        var resp = await http.SendAsync(Req(HttpMethod.Get, "/api/runner/commands?waitSeconds=-5", token));
        Assert.Empty((await resp.Content.ReadFromJsonAsync<RunnerCommand[]>(IceJson.Options))!);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }
}
