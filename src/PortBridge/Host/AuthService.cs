using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.AspNetCore.Http;
using PortBridge.Core.Config;
using PortBridge.Core.Security;

namespace PortBridge.Host;

/// <summary>
/// Every API call needs a session. Sessions come from either
///  - the desktop launcher: a random token in a file only Administrators/SYSTEM can read (or handed over in-process), or
///  - the admin password (needed for browser and remote access).
/// The engine can run commands (on-error command) and the service runs as SYSTEM, so an unauthenticated
/// localhost API would let any local user escalate privileges.
/// </summary>
public sealed class AuthService
{
    public const string CookieName = "pb_session";
    private const int Iterations = 210_000;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, TimeSpan Sliding, bool Local)> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil)> _attempts = new();
    private readonly Func<AppSettings> _settings;

    public AuthService(Func<AppSettings> settings)
    {
        _settings = settings;
        LocalToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Single-use-per-run secret that lets the desktop window sign in without a password.</summary>
    public string LocalToken { get; }

    /// <summary>Writes the token into a new file object readable only by SYSTEM, Administrators and this process's
    /// user. The token is never written into an existing file object: a handle opened earlier (e.g. while a standard
    /// user could still read the desktop-owned folder) would otherwise read it. Returns false if it was not written.</summary>
    public bool WriteLocalTokenFile()
    {
        try
        {
            AppPaths.EnsureDataDir();
            SecretFile.WriteNew(AppPaths.LocalTokenFile, LocalToken,
            [
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                WindowsIdentity.GetCurrent().User!,
            ]);
            return true;
        }
        catch
        {
            // Without the file, the desktop window falls back to the password login.
            return false;
        }
    }

    public static string? ReadLocalTokenFile()
    {
        try { return File.ReadAllText(AppPaths.LocalTokenFile).Trim(); }
        catch { return null; }
    }

    // ---- passwords ----

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iter)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool PasswordSet => !string.IsNullOrEmpty(_settings().PasswordHash);

    // ---- brute-force protection ----

    public TimeSpan? LockedOut(IPAddress? ip)
    {
        var key = ip?.ToString() ?? "?";
        if (_attempts.TryGetValue(key, out var a) && a.LockedUntil > DateTimeOffset.Now) return a.LockedUntil - DateTimeOffset.Now;
        return null;
    }

    /// <summary>Counts a wrong password; returns how long this address is now locked out, if it is.</summary>
    public TimeSpan? RecordFailure(IPAddress? ip)
    {
        var key = ip?.ToString() ?? "?";
        TimeSpan? locked = null;
        _attempts.AddOrUpdate(key, _ => (1, DateTimeOffset.MinValue), (_, a) =>
        {
            var failures = a.Failures + 1;
            var lockFor = failures >= 5 ? TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, failures - 5))) : TimeSpan.Zero;
            locked = lockFor > TimeSpan.Zero ? lockFor : null;
            return (failures, DateTimeOffset.Now + lockFor);
        });
        return locked;
    }

    public void RecordSuccess(IPAddress? ip) => _attempts.TryRemove(ip?.ToString() ?? "?", out _);

    // ---- sessions ----

    public string CreateSession(bool remember, bool local)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var sliding = remember ? TimeSpan.FromDays(30) : TimeSpan.FromHours(12);
        _sessions[token] = (DateTimeOffset.Now + sliding, sliding, local);
        return token;
    }

    public void IssueCookie(HttpContext ctx, string token, bool remember)
    {
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            Expires = remember ? DateTimeOffset.Now.AddDays(30) : null,
        });
    }

    public bool IsAuthenticated(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var s)) return false;
        if (s.Expires < DateTimeOffset.Now)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        _sessions[token] = (DateTimeOffset.Now + s.Sliding, s.Sliding, s.Local);
        return true;
    }

    public void EndSession(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[CookieName];
        if (token is not null) _sessions.TryRemove(token, out _);
        ctx.Response.Cookies.Delete(CookieName);
    }

    /// <summary>After a password change everyone except the caller signs in again.</summary>
    public void EndOtherSessions(HttpContext ctx)
    {
        var mine = ctx.Request.Cookies[CookieName];
        foreach (var key in _sessions.Keys)
            if (key != mine) _sessions.TryRemove(key, out _);
    }

    public static bool IsLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
}
