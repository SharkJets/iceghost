using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Iceghost.Api.Data;
using Iceghost.Contracts;

namespace Iceghost.Api;

/// <summary>Sends done / needs-input messages to Telegram from a bounded queue, so /report never waits on Telegram. The bot token is never logged.</summary>
public sealed class TelegramNotifier(IConfiguration cfg, IHttpClientFactory http, ILogger<TelegramNotifier> log) : BackgroundService
{
    readonly string? _key = cfg["ICEGHOST_TELEGRAM_KEY"], _chat = cfg["ICEGHOST_TELEGRAM_ID"], _url = cfg["ICEGHOST_PUBLIC_URL"]?.TrimEnd('/');
    readonly Channel<SessionEvent> _q = Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

    public bool Enabled => !string.IsNullOrEmpty(_key) && !string.IsNullOrEmpty(_chat);

    public void Enqueue(IEnumerable<SessionEvent> events)
    {
        if (!Enabled) return;
        foreach (var e in events) _q.Writer.TryWrite(e);
    }

    public string Format(SessionEvent e)
    {
        var folder = e.Cwd.Split('/', '\\').LastOrDefault(p => p.Length > 0) ?? e.Cwd;
        var agent = e.Runtime switch { Runtime.Claude => "Claude Code", Runtime.Codex => "Codex", _ => "pi" };
        var text = $"{(e.State == SessionState.NeedsInput ? "❓ Needs you" : "✅ Done")}: {folder}{(string.IsNullOrEmpty(e.Name) ? "" : " · " + e.Name)}\n{e.Machine} · {agent}\n\n{e.Reply}";
        if (!string.IsNullOrEmpty(_url)) text += $"\n{_url}/#/s/{e.MachineId}/{Uri.EscapeDataString(e.SessionId)}";
        return text.Length > 4096 ? text[..4095] + "…" : text; // ponytail: a pathological title could cut the link; shrink the head if it ever happens
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var e in _q.Reader.ReadAllAsync(ct))
            try { await Send(e, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Telegram send failed: {Error}", ex.GetType().Name); } // no ex.ToString(): keep the token-bearing URL out
    }

    /// <summary>Posts one message; on 429 waits retry_after (max 60 s) and tries once more.</summary>
    public async Task Send(SessionEvent e, CancellationToken ct = default)
    {
        var text = Format(e);
        for (var attempt = 0; ; attempt++)
        {
            using var c = http.CreateClient("telegram");
            using var r = await c.PostAsJsonAsync($"https://api.telegram.org/bot{_key}/sendMessage", new { chat_id = _chat, text, disable_web_page_preview = true }, ct);
            if (r.IsSuccessStatusCode) return;
            string? desc = null; var retry = 0;
            try
            {
                using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                if (d.RootElement.TryGetProperty("description", out var ds)) desc = ds.GetString();
                if (d.RootElement.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var ra)) retry = ra.GetInt32();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
            if (r.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retry, 0, 60)), ct);
                continue;
            }
            log.LogWarning("Telegram send failed: {Status} {Description}", (int)r.StatusCode, desc);
            return;
        }
    }
}
