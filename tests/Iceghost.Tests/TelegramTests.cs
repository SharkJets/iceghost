using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Iceghost.Api;
using Iceghost.Api.Data;
using Iceghost.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iceghost.Tests;

[Collection("ApiEnv")]
public sealed class TelegramTests : IDisposable
{
    const string Pw = "correct horse battery", Key = "123456:SECRET-bot-token";
    static readonly object EnvLock = new();
    static readonly DateTimeOffset T = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ig-tg-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch { } }

    sealed class Fake(Func<int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Url, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Enqueue((r.RequestUri!.ToString(), await r.Content!.ReadAsStringAsync(ct)));
            return reply(Calls.Count);
        }
    }
    sealed class Factory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, false); }
    sealed class Logs : ILoggerProvider, ILogger
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public ILogger CreateLogger(string c) => this;
        public IDisposable? BeginScope<TState>(TState s) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => true;
        public void Log<TState>(LogLevel l, EventId id, TState s, Exception? e, Func<TState, Exception?, string> f) => Lines.Enqueue(f(s, e) + e);
        public void Dispose() { }
    }

    static HttpResponseMessage Json(HttpStatusCode c, string body) => new(c) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    static HttpResponseMessage Ok() => Json(HttpStatusCode.OK, """{"ok":true}""");

    static TelegramNotifier Notifier(Fake f, string? key = Key, string? id = "42", string? url = null, Logs? logs = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ICEGHOST_TELEGRAM_KEY"] = key, ["ICEGHOST_TELEGRAM_ID"] = id, ["ICEGHOST_PUBLIC_URL"] = url }).Build(),
            new Factory(f), logs is null ? NullLogger<TelegramNotifier>.Instance : new LoggerFactory([logs]).CreateLogger<TelegramNotifier>());

    static SessionEvent Ev(SessionState s) => new(7, "desk", Runtime.Claude, "a b", @"C:\work\proj", "My fix", s, "All green.");

    static SessionReport Sess(string id, SessionState s, string reply = "hello") =>
        new(Runtime.Claude, id, "/p", "t", s, T, [new("user", "hi", T), new("assistant", reply, T)]);

    [Fact]
    public void Transitions_decision()
    {
        var old = new Dictionary<string, SessionState> { ["a"] = SessionState.Working, ["d"] = SessionState.Done };
        static IEnumerable<string> Ids(IEnumerable<SessionReport> r) => r.Select(s => s.SessionId);
        Assert.Equal(["a"], Ids(IceStore.Transitions(old, [Sess("a", SessionState.NeedsInput)], true, false)));
        Assert.Equal(["a"], Ids(IceStore.Transitions(old, [Sess("a", SessionState.Done)], false, true)));
        Assert.Empty(IceStore.Transitions(old, [Sess("new", SessionState.NeedsInput)], true, true)); // first sighting
        Assert.Empty(IceStore.Transitions(old, [Sess("d", SessionState.Done)], true, true)); // no change
        Assert.Empty(IceStore.Transitions(old, [Sess("a", SessionState.NeedsInput)], false, true)); // flag off
        Assert.Empty(IceStore.Transitions(old, [Sess("a", SessionState.Done)], true, false));
        Assert.Empty(IceStore.Transitions(old, [Sess("a", SessionState.Stalled)], true, true));
    }

    [Fact]
    public void ApplyReport_returns_events_on_state_change_only()
    {
        Directory.CreateDirectory(_dir);
        var s = new IceStore(Path.Combine(_dir, "t.db"));
        var (id, _) = s.CreateMachine("desk");
        s.SetNotify(id, true, false);
        Assert.Empty(s.ApplyReport(id, new("h", "linux", [Sess("s1", SessionState.NeedsInput)]), T)); // first sighting
        Assert.Empty(s.ApplyReport(id, new("h", "linux", [Sess("s1", SessionState.NeedsInput)]), T)); // same state
        Assert.Empty(s.ApplyReport(id, new("h", "linux", [Sess("s1", SessionState.Done)]), T)); // done flag off
        s.RenameSession(id, "s1", "Mine");
        var e = Assert.Single(s.ApplyReport(id, new("h", "linux", [Sess("s1", SessionState.NeedsInput, new string('x', 400))]), T));
        Assert.Equal(("desk", "Mine", SessionState.NeedsInput, 300), (e.Machine, e.Name, e.State, e.Reply.Length));
        Assert.Empty(s.ApplyReport(id, new("h", "linux", null), T)); // heartbeat
        Assert.True(s.ListMachines()[0].NotifyNeedsInput);
        Assert.False(s.ListMachines()[0].NotifyDone);
    }

    [Fact]
    public async Task Sends_both_messages_to_the_bot_url()
    {
        var f = new Fake(_ => Ok());
        var n = Notifier(f, url: "https://ice.example.com/");
        await n.Send(Ev(SessionState.NeedsInput));
        await n.Send(Ev(SessionState.Done));
        var calls = f.Calls.ToArray();
        Assert.All(calls, c => Assert.Equal($"https://api.telegram.org/bot{Key}/sendMessage", c.Url));
        var a = JsonDocument.Parse(calls[0].Body).RootElement;
        Assert.Equal("42", a.GetProperty("chat_id").GetString());
        Assert.True(a.GetProperty("disable_web_page_preview").GetBoolean());
        Assert.False(a.TryGetProperty("parse_mode", out _));
        Assert.Equal("❓ Needs you: proj · My fix\ndesk · Claude Code\n\nAll green.\nhttps://ice.example.com/#/s/7/a%20b", a.GetProperty("text").GetString());
        Assert.StartsWith("✅ Done: proj · My fix\n", JsonDocument.Parse(calls[1].Body).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void No_link_without_public_url_and_text_capped()
    {
        var n = Notifier(new Fake(_ => Ok()));
        Assert.DoesNotContain("#/s/", n.Format(Ev(SessionState.Done)));
        Assert.Equal(4096, n.Format(Ev(SessionState.Done) with { Reply = new string('x', 9000) }).Length);
    }

    [Fact]
    public async Task Retries_once_on_429()
    {
        var f = new Fake(i => i == 1 ? Json((HttpStatusCode)429, """{"ok":false,"description":"Too Many Requests","parameters":{"retry_after":0}}""") : Ok());
        await Notifier(f).Send(Ev(SessionState.Done));
        Assert.Equal(2, f.Calls.Count);
        var f2 = new Fake(_ => Json((HttpStatusCode)429, """{"ok":false,"parameters":{"retry_after":0}}"""));
        await Notifier(f2).Send(Ev(SessionState.Done));
        Assert.Equal(2, f2.Calls.Count); // one retry only
    }

    [Fact]
    public void Enabled_needs_key_and_id()
    {
        Assert.False(Notifier(new Fake(_ => Ok()), key: "").Enabled);
        Assert.False(Notifier(new Fake(_ => Ok()), id: null).Enabled);
        Assert.True(Notifier(new Fake(_ => Ok())).Enabled);
    }

    [Fact]
    public async Task Not_configured_makes_no_http_call()
    {
        var f = new Fake(_ => Ok());
        var n = Notifier(f, key: null);
        n.Enqueue([Ev(SessionState.Done)]);
        await n.StartAsync(default);
        await Task.Delay(200);
        await n.StopAsync(default);
        Assert.Empty(f.Calls);
    }

    WebApplicationFactory<Program> Make(Fake? fake, Logs? logs = null, string? key = Key)
    {
        lock (EnvLock)
        {
            Environment.SetEnvironmentVariable("ICEGHOST_DATA", _dir);
            var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("ICEGHOST_ADMIN_PASSWORD", Pw);
                if (key != null) { b.UseSetting("ICEGHOST_TELEGRAM_KEY", key); b.UseSetting("ICEGHOST_TELEGRAM_ID", "42"); }
                if (fake != null) b.ConfigureServices(s => s.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => fake));
                if (logs != null) b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(logs)));
            });
            f.CreateClient().Dispose();
            return f;
        }
    }

    static async Task<HttpClient> LoggedIn(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        var r = await c.PostAsJsonAsync("/api/login", new { password = Pw });
        c.DefaultRequestHeaders.Add("Cookie", r.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("ig_auth=")).Split(';')[0]);
        return c;
    }

    [Fact]
    public async Task Config_and_notify_endpoints()
    {
        using var f = Make(null);
        var store = f.Services.GetRequiredService<IceStore>();
        var (id, _) = store.CreateMachine("desk");
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync($"/api/machines/{id}/notify", new { needsInput = true, done = true })).StatusCode);
        var c = await LoggedIn(f);
        Assert.True(JsonDocument.Parse(await c.GetStringAsync("/api/config")).RootElement.GetProperty("telegram").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/machines/{id}/notify", new { needsInput = true, done = false })).StatusCode);
        var m = JsonDocument.Parse(await c.GetStringAsync("/api/machines")).RootElement[0];
        Assert.True(m.GetProperty("notifyNeedsInput").GetBoolean());
        Assert.False(m.GetProperty("notifyDone").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/machines/999/notify", new { needsInput = true, done = true })).StatusCode);
    }

    [Fact]
    public async Task Config_false_when_unconfigured()
    {
        using var f = Make(null, key: null);
        var c = await LoggedIn(f);
        Assert.False(JsonDocument.Parse(await c.GetStringAsync("/api/config")).RootElement.GetProperty("telegram").GetBoolean());
    }

    static async Task Report(HttpClient c, string token, SessionState s)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/runner/report") { Content = JsonContent.Create(new RunnerReport("h", "linux", [Sess("s1", s)]), options: IceJson.Options) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(req)).StatusCode);
    }

    static async Task WaitFor(Func<bool> cond) { for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(50); Assert.True(cond()); }

    [Fact]
    public async Task Report_state_change_reaches_telegram()
    {
        var fake = new Fake(_ => Ok());
        using var f = Make(fake);
        var store = f.Services.GetRequiredService<IceStore>();
        var (id, tok) = store.CreateMachine("desk");
        store.SetNotify(id, true, true);
        var c = f.CreateClient();
        await Report(c, tok, SessionState.Working);
        await Report(c, tok, SessionState.NeedsInput);
        await WaitFor(() => !fake.Calls.IsEmpty);
        var call = fake.Calls.Single();
        Assert.Equal($"https://api.telegram.org/bot{Key}/sendMessage", call.Url);
        Assert.Contains("Needs you", call.Body);
    }

    [Fact]
    public async Task Token_never_logged_even_on_failure()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: chat not found"}"""));
        var logs = new Logs();
        using var f = Make(fake, logs);
        var store = f.Services.GetRequiredService<IceStore>();
        var (id, tok) = store.CreateMachine("desk");
        store.SetNotify(id, true, true);
        var c = f.CreateClient();
        await Report(c, tok, SessionState.Working);
        await Report(c, tok, SessionState.Done);
        await WaitFor(() => logs.Lines.Any(l => l.Contains("chat not found")));
        var all = string.Join("\n", logs.Lines);
        Assert.Contains("400", all);
        Assert.DoesNotContain("SECRET", all);
        Assert.DoesNotContain(Key, all);
    }
}
