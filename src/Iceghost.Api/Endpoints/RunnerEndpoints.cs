using Iceghost.Api.Data;
using Iceghost.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iceghost.Api.Endpoints;

public static class RunnerEndpoints
{
    const string MachineKey = "ig.machineId";

    public static int ClampWait(int s) => Math.Clamp(s, 0, 50);

    public static IEndpointRouteBuilder MapRunnerEndpoints(this IEndpointRouteBuilder app)
    {
        var time = app.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

        var g = app.MapGroup("/api/runner");
        g.AddEndpointFilter(async (ctx, next) =>
        {
            var h = ctx.HttpContext.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (!h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
            var id = ctx.HttpContext.RequestServices.GetRequiredService<IceStore>().Authenticate(h[prefix.Length..].Trim());
            if (id is null) return Results.Unauthorized();
            ctx.HttpContext.Items[MachineKey] = id.Value;
            return await next(ctx);
        });

        g.MapPost("/report", async (HttpContext http, IceStore store) =>
        {
            RunnerReport? r;
            try { r = await http.Request.ReadFromJsonAsync<RunnerReport>(IceJson.Options, http.RequestAborted); }
            catch (System.Text.Json.JsonException) { r = null; }
            if (r is null) return Results.BadRequest();
            store.ApplyReport((long)http.Items[MachineKey]!, r, time.GetUtcNow());
            return Results.NoContent();
        });

        g.MapGet("/commands", async (HttpContext http, IceStore store, int? waitSeconds) =>
        {
            var id = (long)http.Items[MachineKey]!;
            var deadline = time.GetTimestamp() + TimeSpan.FromSeconds(ClampWait(waitSeconds ?? 0)).Ticks * time.TimestampFrequency / TimeSpan.TicksPerSecond;
            while (true)
            {
                var cmds = store.TakeCommands(id, time.GetUtcNow());
                if (cmds.Count > 0 || time.GetTimestamp() >= deadline) return Results.Json(cmds, IceJson.Options);
                // ponytail: 1 s DB polling, signal per machine if latency matters
                try { await Task.Delay(1000, http.RequestAborted); }
                catch (OperationCanceledException) { return Results.Json(Array.Empty<RunnerCommand>(), IceJson.Options); }
            }
        });

        g.MapPost("/commands/{id:long}/result", async (HttpContext http, IceStore store, long id) =>
        {
            CommandResult? r;
            try { r = await http.Request.ReadFromJsonAsync<CommandResult>(IceJson.Options, http.RequestAborted); }
            catch (System.Text.Json.JsonException) { r = null; }
            if (r is null) return Results.BadRequest();
            return store.Complete((long)http.Items[MachineKey]!, id, r, time.GetUtcNow())
                ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}
