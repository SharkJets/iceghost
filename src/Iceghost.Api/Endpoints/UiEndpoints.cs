using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Iceghost.Api.Data;
using Iceghost.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iceghost.Api.Endpoints;

public static class UiEndpoints
{
    public sealed record LoginBody(string? Password);
    public sealed record MessageBody(string? Text, string? Image = null, string? ImageType = null);
    public sealed record MachineBody(string? Name);

    const int MaxImage = 3 * 1024 * 1024;

    /// <summary>Allowlisted type, valid base64, size cap, and magic bytes that match the declared type.</summary>
    static bool ImageOk(string b64, string? type, out byte[]? bytes, out string why)
    {
        bytes = null; why = "";
        if (type is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) { why = "unsupported image type"; return false; }
        if (b64.Length > MaxImage / 3 * 4 + 4) { why = "image too large (max 3 MB)"; return false; }
        Span<byte> buf = new byte[b64.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(b64, buf, out var n)) { why = "image is not valid base64"; return false; }
        if (n > MaxImage) { why = "image too large (max 3 MB)"; return false; }
        if (Attachment.Ext(type, buf[..n]) is null) { why = "image content does not match its type"; return false; }
        bytes = buf[..n].ToArray();
        return true;
    }

    public static IEndpointRouteBuilder MapUiEndpoints(this IEndpointRouteBuilder app)
    {
        var password = app.ServiceProvider.GetRequiredService<IConfiguration>()["ICEGHOST_ADMIN_PASSWORD"];
        if (string.IsNullOrEmpty(password) || password.Length < 12)
            throw new InvalidOperationException("ICEGHOST_ADMIN_PASSWORD must be set (12+ chars)");
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        var scheme = CookieAuthenticationDefaults.AuthenticationScheme;

        app.MapPost("/api/login", async (HttpContext ctx, LoginBody? body) =>
        {
            var given = SHA256.HashData(Encoding.UTF8.GetBytes(body?.Password ?? ""));
            if (!CryptographicOperations.FixedTimeEquals(given, expected)) return Results.Unauthorized();
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], scheme));
            await ctx.SignInAsync(scheme, user, new AuthenticationProperties { IsPersistent = true });
            return Results.NoContent();
        }).RequireRateLimiting("login");

        var api = app.MapGroup("/api").RequireAuthorization();
        static IResult Json(object? v, int status = 200) => Results.Json(v, IceJson.Options, statusCode: status);

        api.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(scheme);
            return Results.NoContent();
        });

        api.MapGet("/sessions", (IceStore s) => Json(s.ListSessions(DateTimeOffset.UtcNow)));
        api.MapGet("/sessions/{machineId:long}/{sessionId}", (IceStore s, long machineId, string sessionId) =>
            s.GetSession(machineId, sessionId, DateTimeOffset.UtcNow) is { } d ? Json(d) : Results.NotFound());
        api.MapPost("/sessions/{machineId:long}/{sessionId}/messages", (IceStore s, long machineId, string sessionId, MessageBody? body) =>
        {
            var text = body?.Text ?? "";
            if (text.Length > 20000) return Results.BadRequest();
            byte[]? img = null;
            if (body?.Image is { } b64)
            {
                if (!ImageOk(b64, body.ImageType, out img, out var why)) return Json(new { error = why }, 400);
            }
            else if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest();
            try
            {
                return s.Enqueue(machineId, sessionId, text, DateTimeOffset.UtcNow, img, body?.ImageType) is { } id
                    ? Json(new { id }, 202) : Results.NotFound();
            }
            catch (MachineOfflineException e) { return Json(new { error = e.Message }, 409); }
        });

        api.MapPost("/sessions/{machineId:long}/{sessionId}/commands/{id:long}/dismiss", (IceStore s, long machineId, string sessionId, long id) =>
            s.Dismiss(machineId, sessionId, id) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/machines",(IceStore s) => Json(s.ListMachines()));
        api.MapPost("/machines", (IceStore s, MachineBody? body) =>
        {
            var name = body?.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100) return Results.BadRequest();
            var (id, token) = s.CreateMachine(name);
            return Json(new { id, token }, 201);
        });
        api.MapDelete("/machines/{id:long}", (IceStore s, long id) =>
            s.DeleteMachine(id) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
