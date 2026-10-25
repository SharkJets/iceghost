using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Iceghost.Contracts;

namespace Iceghost.Runner.Loop;

public sealed class UnauthorizedRunnerException : Exception
{
    public UnauthorizedRunnerException() : base("server rejected the machine token") { }
}

public sealed class ServerClient(HttpClient http, string token)
{
    public async Task ReportAsync(RunnerReport r, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, "api/runner/report", r, ct);
    }

    private bool _fresh = true; // first poll tells the server we hold nothing

    public async Task<IReadOnlyList<RunnerCommand>> PollAsync(CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, "api/runner/commands?waitSeconds=25" + (_fresh ? "&fresh=true" : ""), null, ct);
        _fresh = false;
        return await res.Content.ReadFromJsonAsync<RunnerCommand[]>(IceJson.Options, ct) ?? [];
    }

    public async Task ResultAsync(long id, CommandResult r, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"api/runner/commands/{id}/result", r, ct);
    }

    /// <summary>False when the server no longer tracks the command (404: dismissed or expired).</summary>
    public async Task<bool> HeartbeatAsync(long id, CancellationToken ct)
    {
        try { using var _ = await SendAsync(HttpMethod.Post, $"api/runner/commands/{id}/heartbeat", null, ct); return true; }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: IceJson.Options);
        var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) { res.Dispose(); throw new UnauthorizedRunnerException(); }
        try { return res.EnsureSuccessStatusCode(); } catch { res.Dispose(); throw; }
    }
}
