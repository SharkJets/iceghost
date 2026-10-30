using System.Threading.RateLimiting;
using Iceghost.Api.Data;
using Iceghost.Api.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

Iceghost.Api.DotEnv.Load();
var builder = WebApplication.CreateBuilder(args);
var dev = Environment.GetEnvironmentVariable("ICEGHOST_DEV") == "1";

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 8 * 1024 * 1024);

var dataDir = Environment.GetEnvironmentVariable("ICEGHOST_DATA") ?? "./data";
Directory.CreateDirectory(dataDir);
builder.Services.AddSingleton(new IceStore(Path.Combine(dataDir, "iceghost.db")));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("iceghost");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "ig_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = dev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Global (unpartitioned) on purpose: behind cloudflared+nginx RemoteIpAddress may be the ingress for everyone,
    // so per-IP would be bypassable or shared. Cost: an attacker can lock out the admin for the window.
    o.AddFixedWindowLimiter("login", p =>
    {
        p.PermitLimit = 5;
        p.Window = TimeSpan.FromMinutes(1);
    });
});

var app = builder.Build();

// CSRF defence in depth on top of SameSite=Lax: reject cross-site browser mutations of /api.
app.Use(async (ctx, next) =>
{
    var r = ctx.Request;
    if (!HttpMethods.IsGet(r.Method) && !HttpMethods.IsHead(r.Method) && !HttpMethods.IsOptions(r.Method)
        && r.Path.StartsWithSegments("/api")
        && r.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin" && site != "none")
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapRunnerEndpoints();
app.MapUiEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { }
