using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using PortBridge.Core.Config;

namespace PortBridge.Host;

public static class WebServer
{
    public static WebApplication Build(HostRuntime rt)
    {
        var settings = rt.Engine.Settings;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            foreach (var c in Json.Options.Converters) o.SerializerOptions.Converters.Add(c);
        });

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            void Https(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions lo)
            {
                if (settings.UseHttps) lo.UseHttps(rt.Engine.Certificates.GetSelfSigned());
            }
            if (settings.AllowRemote) k.ListenAnyIP(settings.WebPort, Https);
            else k.ListenLocalhost(settings.WebPort, Https);
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            await next();
        });

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (!AuthService.IsLoopback(ctx) && !rt.Engine.Settings.AllowRemote)
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !IsPublic(path))
            {
                if (!rt.Auth.IsAuthenticated(ctx))
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Sign in required" });
                    return;
                }
                // CSRF: browsers cannot add custom headers cross-site without a CORS preflight we never allow.
                if (!HttpMethods.IsGet(ctx.Request.Method) && !ctx.WebSockets.IsWebSocketRequest &&
                    ctx.Request.Headers["X-PortBridge"] != "1")
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-PortBridge header" });
                    return;
                }
                if (ctx.WebSockets.IsWebSocketRequest && !SameOrigin(ctx))
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }
            }
            await next();
        });

        // Local sign-in for the desktop window: /auth/local?token=...
        app.MapGet("/auth/local", (HttpContext ctx, string? token) =>
        {
            var expected = rt.Auth.LocalToken;
            var ok = !rt.Engine.Settings.RequirePasswordLocally && AuthService.IsLoopback(ctx) && token is not null &&
                     CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(expected));
            if (ok) rt.Auth.IssueCookie(ctx, rt.Auth.CreateSession(remember: false, local: true), remember: false);
            return Results.Redirect(ok ? "/" : "/#/login");
        });

        ApiEndpoints.Map(app, rt);

        var files = UiFiles();
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        });
        return app;
    }

    private static bool IsPublic(string path) =>
        path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase);

    private static bool SameOrigin(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // non-browser client
        return Uri.TryCreate(origin, UriKind.Absolute, out var o) &&
               string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The dashboard is embedded in the exe. Set PORTBRIDGE_UI_DIR to serve it from disk while developing.</summary>
    private static IFileProvider UiFiles()
    {
        var dev = Environment.GetEnvironmentVariable("PORTBRIDGE_UI_DIR");
        if (!string.IsNullOrEmpty(dev) && Directory.Exists(dev)) return new PhysicalFileProvider(Path.GetFullPath(dev));
        return new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
    }
}
