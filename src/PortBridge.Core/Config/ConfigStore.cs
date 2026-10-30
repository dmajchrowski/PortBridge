using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortBridge.Core.Config;

public static class AppPaths
{
    /// <summary>Shared by the desktop app and the Windows service so either can run the same configuration.</summary>
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PortBridge");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    /// <summary>Event and traffic logs: C:\Users\Public\PortBridgeLogs, a folder every user of the PC can open and change
    /// (the company convention for logs). Written through <see cref="Security.SafeLogFiles"/> so that links planted there
    /// cannot redirect the service's writes.</summary>
    public static string LogDir { get; set; } = DefaultLogDir();
    public const string TrafficSubfolder = "traffic";
    public static string TrafficLogDir => Path.Combine(LogDir, TrafficSubfolder);
    /// <summary>Administrator-only folder used when the shared log folder has been replaced by a link.</summary>
    public static string FallbackLogDir => Path.Combine(DataDir, "logs");

    private static string DefaultLogDir()
    {
        var publicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments); // C:\Users\Public\Documents
        return string.IsNullOrEmpty(publicDocuments) ? FallbackLogDir : Path.Combine(Path.GetDirectoryName(publicDocuments)!, "PortBridgeLogs");
    }
    public static string CertDir => Path.Combine(DataDir, "certs");
    public static string LocalTokenFile => Path.Combine(DataDir, "local-token");

    /// <summary>Desktop (embedded engine) only: creates the data folder readable/writable by Administrators, SYSTEM and
    /// the creating user, so the app keeps working unelevated. Such a folder is "desktop-owned" and is NOT trusted by
    /// the Windows service: the service secures and migrates it (<see cref="Security.DataDirMigration"/>) before reading
    /// it, resetting credentials and setting aside commands and ownership records a non-administrator could have written.</summary>
    public static void EnsureDataDir()
    {
        var dir = new DirectoryInfo(DataDir);
        if (!dir.Exists)
        {
            dir.Create();
            try
            {
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                foreach (var sid in new[]
                         {
                             new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                             new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                             WindowsIdentity.GetCurrent().User!,
                         })
                {
                    sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit,
                        PropagationFlags.None, AccessControlType.Allow));
                }
                dir.SetAccessControl(sec);
            }
            catch
            {
                // Best effort: on failure the folder keeps inherited ProgramData permissions.
            }
        }
        Directory.CreateDirectory(CertDir);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public sealed class ConfigStore
{
    private readonly Lock _gate = new();
    private readonly string _path;

    public ConfigStore(string? path = null) => _path = path ?? AppPaths.ConfigFile;

    public string Path => _path;

    public AppConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new AppConfig();
            var json = File.ReadAllText(_path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Json.Options) ?? new AppConfig();
            cfg.Settings ??= new AppSettings();
            cfg.Bridges ??= [];
            return cfg;
        }
    }

    /// <summary>
    /// Moves settings that make the service run a program (on-error command, custom setupc path) aside so they are not
    /// used, because a non-administrator may have written them. Returns true if anything was set aside.
    /// </summary>
    public bool QuarantineCommandSettings(string reason)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return false;
            var cfg = Load();
            var s = cfg.Settings;
            if (string.IsNullOrWhiteSpace(s.OnErrorCommand) && string.IsNullOrWhiteSpace(s.Com0ComPath)) return false;
            if (!string.IsNullOrWhiteSpace(s.OnErrorCommand)) s.QuarantinedOnErrorCommand = s.OnErrorCommand;
            if (!string.IsNullOrWhiteSpace(s.Com0ComPath)) s.QuarantinedCom0ComPath = s.Com0ComPath;
            s.OnErrorCommand = null;
            s.Com0ComPath = null;
            s.QuarantineNote = reason;
            Save(cfg);
            return true;
        }
    }

    /// <summary>
    /// Makes configuration written by an untrusted party safe for a privileged process: commands are set aside for
    /// review (<see cref="QuarantineCommandSettings"/>), and everything that grants access is reset — the dashboard
    /// password hash is discarded (never reused, not even for review), remote access and "password in the desktop
    /// window" are turned off. An unreadable file is set aside. Returns what was reset (empty = nothing to do).
    /// Idempotent, so an interrupted migration can simply run again.
    /// </summary>
    public List<string> SanitizeUntrusted(string reason)
    {
        lock (_gate)
        {
            var reset = new List<string>();
            if (!File.Exists(_path)) return reset;
            AppConfig cfg;
            try { cfg = Load(); }
            catch (JsonException)
            {
                File.Move(_path, $"{_path}.untrusted-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
                reset.Add("the configuration file (unreadable; set aside)");
                return reset;
            }
            var s = cfg.Settings;
            if (!string.IsNullOrWhiteSpace(s.OnErrorCommand)) reset.Add("on-error command (set aside for review)");
            if (!string.IsNullOrWhiteSpace(s.Com0ComPath)) reset.Add("custom com0com location (set aside for review)");
            if (!string.IsNullOrWhiteSpace(s.OnErrorCommand)) s.QuarantinedOnErrorCommand = s.OnErrorCommand;
            if (!string.IsNullOrWhiteSpace(s.Com0ComPath)) s.QuarantinedCom0ComPath = s.Com0ComPath;
            s.OnErrorCommand = null;
            s.Com0ComPath = null;
            if (!string.IsNullOrEmpty(s.PasswordHash)) reset.Add("dashboard password (discarded)");
            s.PasswordHash = null;
            if (s.AllowRemote) reset.Add("remote dashboard access (turned off)");
            s.AllowRemote = false;
            if (s.RequirePasswordLocally) reset.Add("password prompt in the desktop window (turned off)");
            s.RequirePasswordLocally = false;
            if (reset.Count == 0) return reset;
            s.QuarantineNote = $"{reason} Reset: {string.Join("; ", reset)}.";
            Save(cfg);
            return reset;
        }
    }

    /// <summary>Atomic write (temp file + replace) so a crash never leaves a half-written config.</summary>
    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            var dir = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, Json.Indented));
            if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, _path);
        }
    }
}
