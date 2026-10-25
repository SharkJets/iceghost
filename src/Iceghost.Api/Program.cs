using System.Threading.RateLimiting;
using Iceghost.Api.Data;
using Iceghost.Api.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var dev = Environment.GetEnvironmentVariable("ICEGHOST_DEV") == "1";

var dataDir = Environment.GetEnvironmentVariable("ICEGHOST_DATA") ?? "./data";
Directory.CreateDirectory(dataDir);
builder.Services.AddSingleton(new IceStore(Path.Combine(dataDir, "iceghost.db")));

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
    o.AddFixedWindowLimiter("login", p =>
    {
        p.PermitLimit = 5;
        p.Window = TimeSpan.FromMinutes(1);
    });
});

var app = builder.Build();

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
