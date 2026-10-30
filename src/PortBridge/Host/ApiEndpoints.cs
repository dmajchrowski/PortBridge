using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;
using PortBridge.Core.Serial;
using PortBridge.Core.Util;

namespace PortBridge.Host;

public sealed record LoginRequest(string Password, bool Remember);
public sealed record SendRequest(string EndpointId, string Data, string Format, bool AppendNewline);
public sealed record PasswordRequest(string? Current, string? NewPassword);
public sealed record HostRequest(string Host, int Port = 0, int Count = 4);
public sealed record WebhookTest(string Url);
public sealed record AdoptRequest(string? ComName, bool Confirm);

public static class ApiEndpoints
{
    public const string SecretMask = "********";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0";

    public static void Map(WebApplication app, HostRuntime rt)
    {
        var engine = rt.Engine;
        var auth = rt.Auth;

        // ---------------- auth ----------------
        app.MapGet("/api/auth/status", (HttpContext ctx) => new
        {
            authenticated = auth.IsAuthenticated(ctx),
            passwordSet = auth.PasswordSet,
            local = AuthService.IsLoopback(ctx),
            requirePasswordLocally = engine.Settings.RequirePasswordLocally,
        });

        app.MapPost("/api/auth/login", (HttpContext ctx, LoginRequest req) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4(); // the dashboard listens dual-stack
            if (auth.LockedOut(ip) is { } wait)
                return Results.Json(new { error = $"Too many attempts. Try again in {Math.Ceiling(wait.TotalSeconds)} s." }, statusCode: 429);
            if (!auth.PasswordSet)
                return Results.Json(new { error = "No password is set. Open PortBridge from the Start menu or tray on this PC and set one under Settings." }, statusCode: 400);
            if (!AuthService.VerifyPassword(req.Password ?? "", engine.Settings.PasswordHash))
            {
                var locked = auth.RecordFailure(ip);
                engine.Journal.Add(EventLevel.Warning, $"Failed sign-in from {ip}" +
                    (locked is { } t ? $" — too many attempts, sign-ins from this address are refused for {t.TotalSeconds:0} s" : ""),
                    category: EventCategory.Config);
                return Results.Json(new { error = "Wrong password." }, statusCode: 401);
            }
            auth.RecordSuccess(ip);
            auth.IssueCookie(ctx, auth.CreateSession(req.Remember, AuthService.IsLoopback(ctx)), req.Remember);
            if (!AuthService.IsLoopback(ctx)) engine.Journal.Add(EventLevel.Info, $"Signed in from {ip}", category: EventCategory.Config);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            auth.EndSession(ctx);
            return Results.Ok();
        });

        // ---------------- info ----------------
        app.MapGet("/api/info", () => new
        {
            version = Version,
            machine = Environment.MachineName,
            mode = rt.Mode.ToString().ToLowerInvariant(),
            startedAt = engine.StartedAt,
            elevated = Environment.IsPrivilegedProcess,
            dataDir = AppPaths.DataDir,
            logDir = AppPaths.LogDir,
            webPort = rt.BoundPort,
            https = rt.BoundHttps,
            remote = engine.Settings.AllowRemote,
            com0com = new { installed = engine.Com0Com.IsInstalled, path = engine.Com0Com.SetupcPath },
            addresses = LocalAddresses(),
        });

        // ---------------- bridges ----------------
        app.MapGet("/api/bridges", () => engine.Snapshot().Select(Mask));

        app.MapGet("/api/bridges/{id}", (string id) =>
            engine.Get(id) is { } v ? Results.Ok(Mask(v)) : Results.NotFound());

        app.MapPost("/api/bridges", async (BridgeConfig cfg) =>
        {
            Unmask(cfg, engine.Get(cfg.Id)?.Config);
            try
            {
                return Results.Ok(Mask(await engine.SaveBridgeAsync(cfg)));
            }
            catch (ValidationException ex)
            {
                return Results.Json(new { error = "Please fix the highlighted problems.", errors = ex.Errors }, statusCode: 400);
            }
        });

        app.MapDelete("/api/bridges/{id}", (string id) => Guard(() => engine.DeleteBridgeAsync(id)));
        app.MapPost("/api/bridges/{id}/start", (string id) => Guard(() => engine.SetActiveAsync(id, true)));
        app.MapPost("/api/bridges/{id}/stop", (string id) => Guard(() => engine.SetActiveAsync(id, false)));
        app.MapPost("/api/bridges/{id}/restart", (string id) => Guard(() => engine.RestartAsync(id)));
        app.MapPost("/api/bridges/{id}/duplicate", async (string id) =>
        {
            try { return Results.Ok(Mask(await engine.DuplicateAsync(id))); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        app.MapPost("/api/bridges/start-all", () => Guard(() => engine.SetAllActiveAsync(true)));
        app.MapPost("/api/bridges/stop-all", () => Guard(() => engine.SetAllActiveAsync(false)));

        app.MapPost("/api/bridges/{id}/reset-stats", (string id) =>
        {
            engine.Runtime(id)?.Stats.Reset();
            return Results.Ok();
        });

        app.MapPost("/api/bridges/{id}/send", (string id, SendRequest req) =>
        {
            var bridge = engine.Runtime(id);
            if (bridge is null) return Results.Json(new { error = "Start the bridge first." }, statusCode: 409);
            byte[] data;
            if (req.Format == "hex")
            {
                if (!DataFormat.TryParseHex(req.Data, out data))
                    return Results.Json(new { error = "That is not valid hex (example: 02 41 42 03)." }, statusCode: 400);
            }
            else
            {
                data = DataFormat.Unescape(req.Data);
            }
            if (req.AppendNewline) data = [.. data, 0x0D, 0x0A];
            if (data.Length == 0) return Results.Json(new { error = "Nothing to send." }, statusCode: 400);
            try
            {
                var n = bridge.Inject(req.EndpointId, data);
                return Results.Ok(new { delivered = n, bytes = data.Length });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        app.MapPost("/api/bridges/{id}/sessions/{seq:long}/kick", (string id, long seq) =>
            engine.Runtime(id)?.Kick(seq) == true ? Results.Ok() : Results.NotFound());

        app.MapGet("/api/bridges/{id}/traffic", (string id) => engine.Traffic.Recent(id));

        // ---------------- ports ----------------
        app.MapGet("/api/ports", async () =>
        {
            var usage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in engine.Snapshot())
                foreach (var e in b.Config.AllEndpoints().Where(e => e.IsSerial))
                {
                    if (!usage.TryGetValue(e.PortName, out var l)) usage[e.PortName] = l = [];
                    l.Add(b.Name);
                }

            var c0c = engine.Com0Com;
            List<Com0ComPair> pairs = [];
            string? pairsError = null;
            // setupc requires elevation; never pop a UAC prompt just for listing.
            if (c0c.IsInstalled && Environment.IsPrivilegedProcess)
            {
                try { pairs = await c0c.ListPairsAsync(); }
                catch (Exception ex) { pairsError = ex.Message; }
            }
            string suggestion;
            try { suggestion = await c0c.SuggestFreeNameAsync(usage.Keys, CancellationToken.None); }
            catch { suggestion = "COM10"; }

            return new
            {
                ports = PortEnumerator.List().Select(p => new
                {
                    p.Name, p.Description, p.Manufacturer, p.IsCom0Com,
                    usedBy = usage.TryGetValue(p.Name, out var u) ? u : [],
                }),
                configured = usage.Select(kv => new { name = kv.Key, usedBy = kv.Value }),
                com0com = new
                {
                    installed = c0c.IsInstalled,
                    path = c0c.SetupcPath,
                    canList = Environment.IsPrivilegedProcess,
                    pairs = pairs.Select(p => new
                    {
                        p.Number, p.UserPort, p.BridgePort, p.Owned, p.Origin,
                        // Adoptable: looks like an older PortBridge pair but has no ownership record.
                        adoptable = !p.Owned && p.HasPortBridgeLayout,
                        usedBy = usage.TryGetValue(p.UserPort, out var u2) ? u2 : [],
                    }),
                    error = pairsError,
                },
                suggestedVirtual = suggestion,
            };
        });

        app.MapDelete("/api/virtual-pairs/{n:int}", async (int n) =>
        {
            var pairs = await engine.Com0Com.ListPairsAsync();
            var pair = pairs.FirstOrDefault(p => p.Number == n);
            if (pair is null) return Results.NotFound();
            if (engine.UsedPortNames().Contains(pair.UserPort.ToUpperInvariant()))
                return Results.Json(new { error = $"{pair.UserPort} is used by a bridge. Remove it from the bridge first." }, statusCode: 409);
            try
            {
                // Only pairs PortBridge owns (created or explicitly adopted) can be removed from here.
                await engine.Com0Com.RemovePairAsync(n);
            }
            catch (Com0ComOwnershipException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
            engine.Journal.Add(EventLevel.Info, $"Removed virtual port pair {pair.UserPort}", category: EventCategory.Config);
            return Results.Ok();
        });

        // Explicit migration of a pair made by an older PortBridge (no ownership record). Changes no driver settings.
        app.MapPost("/api/virtual-pairs/{n:int}/adopt", async (int n, AdoptRequest req) =>
        {
            if (!req.Confirm) return Results.Json(new { error = "Confirm the adoption." }, statusCode: 400);
            try
            {
                var rec = await engine.Com0Com.AdoptPairAsync(n, req.ComName ?? "");
                engine.Journal.Add(EventLevel.Info,
                    $"Adopted virtual port pair {rec.ComName} (CNCA{n}/CNCB{n}) — PortBridge may now manage it; no driver settings were changed",
                    category: EventCategory.Config);
                return Results.Ok(rec);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
        });

        app.MapPost("/api/virtual-pairs/{n:int}/release", (int n) =>
        {
            try
            {
                engine.Com0Com.ReleasePair(n);
                engine.Journal.Add(EventLevel.Info, $"Released virtual port pair CNCA{n}/CNCB{n} — PortBridge will no longer change or remove it", category: EventCategory.Config);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
        });

        // ---------------- events ----------------
        app.MapGet("/api/events", (int? limit, string? bridgeId, EventLevel? level, long? before) =>
            engine.Journal.Query(Math.Clamp(limit ?? 300, 1, 2000), bridgeId, level, before));

        app.MapDelete("/api/events", () =>
        {
            engine.Journal.Clear();
            return Results.Ok();
        });

        // ---------------- settings ----------------
        app.MapGet("/api/settings", () => SettingsView(engine.Settings, auth));

        app.MapPut("/api/settings", async (AppSettings incoming) =>
        {
            var s = engine.Settings;
            if (incoming.WebPort is < 1 or > 65535) return Results.Json(new { error = "Dashboard port must be 1–65535." }, statusCode: 400);
            if (incoming.AllowRemote && !auth.PasswordSet)
                return Results.Json(new { error = "Set an admin password before allowing remote access." }, statusCode: 400);
            if (!string.IsNullOrWhiteSpace(incoming.WebhookUrl) &&
                !(Uri.TryCreate(incoming.WebhookUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
                return Results.Json(new { error = "Webhook URL must start with http:// or https://" }, statusCode: 400);
            if (incoming.RequirePasswordLocally && !auth.PasswordSet)
                return Results.Json(new { error = "Set a password first." }, statusCode: 400);

            var restart = incoming.WebPort != rt.BoundPort || incoming.UseHttps != rt.BoundHttps || incoming.AllowRemote != s.AllowRemote;
            await engine.UpdateSettingsAsync(x =>
            {
                x.WebPort = incoming.WebPort;
                x.AllowRemote = incoming.AllowRemote;
                x.UseHttps = incoming.UseHttps;
                x.RequirePasswordLocally = incoming.RequirePasswordLocally;
                x.WriteEventLogFile = incoming.WriteEventLogFile;
                x.LogRetentionDays = Math.Clamp(incoming.LogRetentionDays, 1, 3650);
                x.OnErrorCommand = string.IsNullOrWhiteSpace(incoming.OnErrorCommand) ? null : incoming.OnErrorCommand.Trim();
                x.WebhookUrl = string.IsNullOrWhiteSpace(incoming.WebhookUrl) ? null : incoming.WebhookUrl.Trim();
                x.WebhookIncludeConnections = incoming.WebhookIncludeConnections;
                x.DesktopNotifications = incoming.DesktopNotifications;
                x.CloseToTray = incoming.CloseToTray;
                x.Com0ComPath = string.IsNullOrWhiteSpace(incoming.Com0ComPath) ? null : incoming.Com0ComPath.Trim();
                // An authenticated administrator saving settings reviews them: what was set aside stays aside unless
                // re-entered above, and the notice goes away.
                x.QuarantinedOnErrorCommand = null;
                x.QuarantinedCom0ComPath = null;
                x.QuarantineNote = null;
            });
            engine.Journal.Add(EventLevel.Info, "Settings updated", category: EventCategory.Config);
            return Results.Ok(new { restartRequired = restart, settings = SettingsView(engine.Settings, auth) });
        });

        app.MapPost("/api/settings/password", async (HttpContext ctx, PasswordRequest req) =>
        {
            if (auth.PasswordSet && !AuthService.VerifyPassword(req.Current ?? "", engine.Settings.PasswordHash))
                return Results.Json(new { error = "The current password is wrong." }, statusCode: 400);
            var pw = req.NewPassword ?? "";
            if (pw.Length == 0)
            {
                if (engine.Settings.AllowRemote) return Results.Json(new { error = "Turn off remote access before removing the password." }, statusCode: 400);
                await engine.UpdateSettingsAsync(x => { x.PasswordHash = null; x.RequirePasswordLocally = false; });
                engine.Journal.Add(EventLevel.Info, "Admin password removed", category: EventCategory.Config);
                return Results.Ok();
            }
            if (pw.Length < 8) return Results.Json(new { error = "Use at least 8 characters." }, statusCode: 400);
            await engine.UpdateSettingsAsync(x => x.PasswordHash = AuthService.HashPassword(pw));
            auth.EndOtherSessions(ctx);
            engine.Journal.Add(EventLevel.Info, "Admin password changed", category: EventCategory.Config);
            return Results.Ok();
        });

        app.MapPost("/api/settings/test-webhook", async (WebhookTest req) =>
        {
            var err = await engine.Notifier.TestWebhookAsync(req.Url);
            return err is null ? Results.Ok(new { ok = true }) : Results.Json(new { error = err }, statusCode: 502);
        });

        app.MapPost("/api/host/restart-web", () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                await rt.RestartWebAsync();
            });
            return Results.Ok(new { port = engine.Settings.WebPort, https = engine.Settings.UseHttps });
        });

        // ---------------- backup ----------------
        app.MapGet("/api/config/export", (HttpContext ctx) =>
        {
            var cfg = engine.ExportConfig();
            cfg.Settings.PasswordHash = null;
            foreach (var b in cfg.Bridges) b.Active = false;
            var json = JsonSerializer.SerializeToUtf8Bytes(cfg, Json.Indented);
            return Results.File(json, "application/json", $"portbridge-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.json");
        });

        app.MapPost("/api/config/import", async (HttpContext ctx, string? mode) =>
        {
            AppConfig? cfg;
            try { cfg = await JsonSerializer.DeserializeAsync<AppConfig>(ctx.Request.Body, Json.Options); }
            catch (JsonException ex) { return Results.Json(new { error = $"Not a PortBridge configuration file: {ex.Message}" }, statusCode: 400); }
            if (cfg?.Bridges is null) return Results.Json(new { error = "The file contains no bridges." }, statusCode: 400);
            var problems = new List<string>();
            foreach (var b in cfg.Bridges)
                foreach (var err in Validator.Validate(b, mode == "replace" ? cfg.Bridges.Where(x => x != b) : engine.ExportConfig().Bridges))
                    problems.Add($"{b.Name}: {err}");
            if (mode == "replace" && problems.Count > 0)
                return Results.Json(new { error = "The file has problems.", errors = problems }, statusCode: 400);
            await engine.ImportAsync(cfg, replace: mode == "replace");
            return Results.Ok(new { imported = cfg.Bridges.Count, warnings = problems });
        });

        // ---------------- tools ----------------
        app.MapPost("/api/tools/ping", async (HostRequest req) =>
        {
            var results = new List<object>();
            using var ping = new Ping();
            for (var i = 0; i < Math.Clamp(req.Count, 1, 10); i++)
            {
                try
                {
                    var r = await ping.SendPingAsync(req.Host, 2000);
                    results.Add(new { seq = i + 1, status = r.Status.ToString(), ms = r.RoundtripTime, address = r.Address?.ToString() });
                }
                catch (Exception ex)
                {
                    results.Add(new { seq = i + 1, status = ex.GetBaseException().Message, ms = (long?)null, address = (string?)null });
                }
                if (i + 1 < req.Count) await Task.Delay(500);
            }
            return results;
        });

        app.MapPost("/api/tools/resolve", async (HostRequest req) =>
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(req.Host);
                return Results.Ok(new { hostName = entry.HostName, addresses = entry.AddressList.Select(a => a.ToString()), aliases = entry.Aliases });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 404);
            }
        });

        app.MapPost("/api/tools/tcp-check", async (HostRequest req) =>
        {
            var sw = Stopwatch.StartNew();
            using var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(3000);
            try
            {
                await s.ConnectAsync(req.Host, req.Port, cts.Token);
                return Results.Ok(new { open = true, ms = sw.ElapsedMilliseconds, remote = s.RemoteEndPoint?.ToString() });
            }
            catch (OperationCanceledException)
            {
                return Results.Ok(new { open = false, ms = sw.ElapsedMilliseconds, error = "no answer within 3 s (filtered or host down)" });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { open = false, ms = sw.ElapsedMilliseconds, error = ex.Message });
            }
        });

        app.MapGet("/api/tools/interfaces", () =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => new
                {
                    name = n.Name,
                    description = n.Description,
                    type = n.NetworkInterfaceType.ToString(),
                    speedMbps = n.Speed > 0 ? n.Speed / 1_000_000 : 0,
                    mac = string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                    addresses = n.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString()),
                    gateways = n.GetIPProperties().GatewayAddresses.Select(g => g.Address.ToString()),
                }));

        app.MapGet("/api/tools/listeners", () =>
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Select(e => e.Port).Distinct().Order());

        // ---------------- live updates ----------------
        app.Map("/api/live", (HttpContext ctx) => LiveSocket.HandleAsync(ctx, rt));
    }

    private static async Task<IResult> Guard(Func<Task> action)
    {
        try
        {
            await action();
            return Results.Ok();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    }

    private static object SettingsView(AppSettings s, AuthService auth) => new
    {
        s.WebPort, s.AllowRemote, s.UseHttps, s.RequirePasswordLocally, s.WriteEventLogFile, s.LogRetentionDays,
        s.OnErrorCommand, s.WebhookUrl, s.WebhookIncludeConnections, s.DesktopNotifications, s.CloseToTray, s.Com0ComPath,
        s.QuarantinedOnErrorCommand, s.QuarantinedCom0ComPath, s.QuarantineNote,
        passwordSet = auth.PasswordSet,
    };

    private static string[] LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    // ---- secrets never leave the server ----

    public static BridgeView Mask(BridgeView v)
    {
        if (!v.Config.AllEndpoints().Any(e => !string.IsNullOrEmpty(e.Tls.CertificatePassword))) return v;
        var cfg = Json.Clone(v.Config);
        foreach (var e in cfg.AllEndpoints())
            if (!string.IsNullOrEmpty(e.Tls.CertificatePassword)) e.Tls.CertificatePassword = SecretMask;
        return v with { Config = cfg };
    }

    private static void Unmask(BridgeConfig incoming, BridgeConfig? existing)
    {
        foreach (var e in incoming.AllEndpoints())
        {
            if (e.Tls?.CertificatePassword != SecretMask) continue;
            e.Tls.CertificatePassword = existing?.AllEndpoints().FirstOrDefault(x => x.Id == e.Id)?.Tls.CertificatePassword;
        }
    }
}
