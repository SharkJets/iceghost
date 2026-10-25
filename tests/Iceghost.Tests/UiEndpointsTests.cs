using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Iceghost.Api.Data;
using Iceghost.Contracts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iceghost.Tests;

[Collection("ApiEnv")]
public class UiEndpointsTests
{
    const string Pw = "correct horse battery";
    static readonly object EnvLock = new();

    static WebApplicationFactory<Program> Make(string? password = Pw, string? dir = null)
    {
        dir ??= Path.Combine(Path.GetTempPath(), "ig-ui-" + Guid.NewGuid().ToString("N"));
        lock (EnvLock)
        {
            Environment.SetEnvironmentVariable("ICEGHOST_DATA", dir);
            var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                if (password != null) b.UseSetting("ICEGHOST_ADMIN_PASSWORD", password);
            });
            f.CreateClient().Dispose(); // build host while env is set
            return f;
        }
    }

    // cookie is Secure and the test server is http, so carry it by hand
    static async Task<(HttpClient c, string setCookie)> LoggedIn(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        var r = await c.PostAsJsonAsync("/api/login", new { password = Pw });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var sc = r.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("ig_auth="));
        c.DefaultRequestHeaders.Add("Cookie", sc.Split(';')[0]);
        return (c, sc);
    }

    [Fact]
    public async Task Every_ui_route_401_without_cookie()
    {
        using var f = Make();
        var c = f.CreateClient();
        var body = JsonContent.Create(new { text = "x", name = "n" });
        foreach (var (m, u) in new[] {
            ("GET", "/api/sessions"), ("GET", "/api/sessions/1/s1"), ("POST", "/api/sessions/1/s1/messages"),
            ("GET", "/api/machines"), ("POST", "/api/machines"), ("DELETE", "/api/machines/1"), ("POST", "/api/logout") })
        {
            var r = await c.SendAsync(new HttpRequestMessage(new HttpMethod(m), u) { Content = m == "POST" ? body : null });
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{m} {u} -> {r.StatusCode}");
        }
    }

    [Fact]
    public async Task Login_wrong_401()
    {
        using var f = Make();
        var r = await f.CreateClient().PostAsJsonAsync("/api/login", new { password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Cross_site_mutation_403_and_machine_list_has_no_token()
    {
        using var f = Make();
        var (c, _) = await LoggedIn(f);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/machines") { Content = JsonContent.Create(new { name = "m" }) };
        req.Headers.Add("Sec-Fetch-Site", "same-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);
        var ok = new HttpRequestMessage(HttpMethod.Post, "/api/machines") { Content = JsonContent.Create(new { name = "m" }) };
        ok.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(ok)).StatusCode);
        Assert.DoesNotContain("token", (await c.GetStringAsync("/api/machines")).ToLowerInvariant());
    }

    [Fact]
    public async Task Login_sixth_try_429()
    {
        using var f = Make();
        var c = f.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/login", new { password = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/login", new { password = "nope" })).StatusCode);
    }

    [Fact]
    public async Task Cookie_flags()
    {
        using var f = Make();
        var (_, sc) = await LoggedIn(f);
        Assert.Contains("httponly", sc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", sc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Message_empty_400()
    {
        using var f = Make();
        var (c, _) = await LoggedIn(f);
        foreach (var t in new[] { "", "   \n" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/sessions/1/s1/messages", new { text = t })).StatusCode);
    }

    [Fact]
    public async Task Message_20001_400()
    {
        using var f = Make();
        var (c, _) = await LoggedIn(f);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/sessions/1/s1/messages", new { text = new string('a', 20001) })).StatusCode);
    }

    [Fact]
    public async Task Message_unknown_session_404()
    {
        using var f = Make();
        var (c, _) = await LoggedIn(f);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/sessions/1/nope/messages", new { text = "hi" })).StatusCode);
    }

    [Fact]
    public async Task Token_shown_once()
    {
        using var f = Make();
        var (c, _) = await LoggedIn(f);
        var created = await c.PostAsJsonAsync("/api/machines", new { name = "desk" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var j = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(j.GetProperty("token").GetString()));
        var list = await c.GetStringAsync("/api/machines");
        Assert.Contains("desk", list);
        Assert.DoesNotContain("token", list, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/machines/" + j.GetProperty("id").GetInt64())).StatusCode);
    }

    [Fact]
    public void Short_or_missing_password_refuses_start()
    {
        foreach (var pw in new string?[] { null, "short" })
        {
            var ex = Assert.ThrowsAny<Exception>(() => Make(pw ?? ""));
            Assert.Contains("ICEGHOST_ADMIN_PASSWORD", ex.ToString());
        }
    }

    static async Task<HttpResponseMessage> MessageTo(DateTimeOffset lastSeen)
    {
        using var f = Make();
        var store = f.Services.GetRequiredService<IceStore>();
        var (id, _) = store.CreateMachine("desk");
        store.ApplyReport(id, new("h", "linux", [new(Runtime.Claude, "s1", "/p", "t", SessionState.Done, lastSeen, [])]), lastSeen);
        var (c, _) = await LoggedIn(f);
        return await c.PostAsJsonAsync($"/api/sessions/{id}/s1/messages", new { text = "hi" });
    }

    [Fact]
    public async Task Message_to_offline_machine_409()
    {
        var r = await MessageTo(DateTimeOffset.UtcNow.AddMinutes(-3));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.StartsWith("machine offline: desk last seen ", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Message_to_online_machine_202() =>
        Assert.Equal(HttpStatusCode.Accepted, (await MessageTo(DateTimeOffset.UtcNow.AddSeconds(-10))).StatusCode);

    [Fact]
    public async Task Cookie_survives_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ig-ui-" + Guid.NewGuid().ToString("N"));
        string cookie;
        using (var f1 = Make(dir: dir)) cookie = (await LoggedIn(f1)).setCookie.Split(';')[0];
        using var f2 = Make(dir: dir);
        var c = f2.CreateClient();
        c.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/machines")).StatusCode);
    }
}
