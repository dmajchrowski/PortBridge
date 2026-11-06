using System.Diagnostics;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;

namespace PortBridge.Tests;

/// <summary>Logs go to a folder every user may change (C:\Users\Public\PortBridgeLogs). Links planted there must never
/// redirect PortBridge's writes or deletes — as a service it would do them as SYSTEM — so it falls back instead.</summary>
public class SharedLogFolderTests : IDisposable
{
    private readonly string _dir = Scratch.NewDir("logs");
    private string Shared => Path.Combine(_dir, "PortBridgeLogs");
    private string Fallback => Path.Combine(_dir, "data", "logs");
    private static string Today => $"events-{DateTime.Now:yyyyMMdd}.log";

    public SharedLogFolderTests() => Directory.CreateDirectory(Path.Combine(_dir, "data"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private static void MkLink(string kind, string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink {kind} \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"mklink {kind} failed");
    }

    /// <summary>What renaming or deleting the folder needs: opening it with DELETE access.</summary>
    private static bool CanRenameOrDelete(string dir)
    {
        using var h = CreateFileW(dir, 0x10000 /* DELETE */, 7 /* share all */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 /* BACKUP_SEMANTICS */, IntPtr.Zero);
        return !h.IsInvalid;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>Reads a file the journal may have open.</summary>
    private static string Read(string path)
    {
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public async Task Events_go_to_the_shared_folder_and_old_files_are_removed()
    {
        Directory.CreateDirectory(Shared);
        var old = Path.Combine(Shared, "events-20200101.log");
        File.WriteAllText(old, "old");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-40));

        await using var journal = new EventJournal(Shared, Fallback) { RetentionDays = 30 };
        journal.Add(EventLevel.Error, "port COM3 disappeared");

        await Wait.Until(() => Read(Path.Combine(Shared, Today)).Contains("port COM3 disappeared"), what: "line in the shared log");
        await Wait.Until(() => !File.Exists(old), what: "old log removed");
        Assert.False(Directory.Exists(Fallback));
    }

    [Fact]
    public async Task A_shared_folder_replaced_by_a_junction_is_not_written_or_cleaned_through()
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var victim = Path.Combine(elsewhere, "events-20200101.log");
        File.WriteAllText(victim, "not PortBridge's");
        File.SetLastWriteTime(victim, DateTime.Now.AddDays(-40));
        MkLink("/J", Shared, elsewhere);

        await using var journal = new EventJournal(Shared, Fallback) { RetentionDays = 30 };
        journal.Add(EventLevel.Error, "redirect me");

        await Wait.Until(() => Read(Path.Combine(Fallback, Today)).Contains("redirect me"), what: "line in the fallback log");
        Assert.False(File.Exists(Path.Combine(elsewhere, Today)));
        Assert.True(File.Exists(victim), "cleanup must not delete through the junction");
        Assert.Contains(journal.Query(), e => e.Level == EventLevel.Warning && e.Message.Contains("cannot be used"));
    }

    [Fact]
    public async Task A_log_file_hard_linked_to_another_file_is_not_appended_to()
    {
        Directory.CreateDirectory(Shared);
        var victim = Path.Combine(_dir, "victim.txt");
        File.WriteAllText(victim, "original");
        MkLink("/H", Path.Combine(Shared, Today), victim);

        await using var journal = new EventJournal(Shared, Fallback);
        journal.Add(EventLevel.Info, "not into the victim");

        await Wait.Until(() => Read(Path.Combine(Fallback, Today)).Contains("not into the victim"), what: "line in the fallback log");
        Assert.Equal("original", File.ReadAllText(victim));
    }

    [Fact]
    public async Task Traffic_logs_go_to_the_shared_traffic_folder_or_fall_back_when_it_is_a_junction()
    {
        var bridge = new BridgeConfig { Name = "Scale", TrafficLog = new TrafficLogOptions { Enabled = true, Format = TrafficLogFormat.Text } };

        await using (var hub = new TrafficHub { LogRoot = Shared, FallbackRoot = Fallback })
        {
            hub.Record(bridge, TrafficDirection.Forward, "COM3", "12.5 kg"u8.ToArray());
            await Wait.Until(() => Directory.Exists(Path.Combine(Shared, "traffic")) &&
                Directory.GetFiles(Path.Combine(Shared, "traffic")).Any(f => Read(f).Contains("12.5 kg")), what: "shared traffic log");
        }

        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(Path.Combine(Shared, "traffic"), true);
        MkLink("/J", Path.Combine(Shared, "traffic"), elsewhere);
        var warnings = new List<string>();
        await using (var hub = new TrafficHub { LogRoot = Shared, FallbackRoot = Fallback, Warn = w => { lock (warnings) warnings.Add(w); } })
        {
            hub.Record(bridge, TrafficDirection.Reverse, "TCP", "TARE"u8.ToArray());
            await Wait.Until(() => Directory.Exists(Path.Combine(Fallback, "traffic")) &&
                Directory.GetFiles(Path.Combine(Fallback, "traffic")).Any(f => Read(f).Contains("TARE")), what: "fallback traffic log");
        }
        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData(TrafficLogFormat.Hex, "<< [RPP transmitter (COM2)] (3) 0D 06 0D  |<CR><ACK><CR>|")]
    [InlineData(TrafficLogFormat.Text, "<< [RPP transmitter (COM2)] (3) <CR><ACK><CR>")]
    public async Task Traffic_logs_show_control_characters_by_name(TrafficLogFormat format, string expected)
    {
        var bridge = new BridgeConfig { Name = "Pager", TrafficLog = new TrafficLogOptions { Enabled = true, Format = format } };
        await using (var hub = new TrafficHub { LogRoot = Shared, FallbackRoot = Fallback, LogBatchDelay = TimeSpan.FromMilliseconds(50) })
        {
            hub.Record(bridge, TrafficDirection.Reverse, "RPP transmitter (COM2)", [0x0D, 0x06, 0x0D]);
        }
        var line = Read(Directory.GetFiles(Path.Combine(Shared, "traffic")).Single()).TrimEnd();
        Assert.EndsWith(expected, line);
    }

    [Theory]
    [InlineData(ProtocolDecode.Tap, "(2) 04 0D  |<EOT><CR>|  TAP: Log off: no more pages")]
    [InlineData(ProtocolDecode.None, "(2) 04 0D  |<EOT><CR>|")]
    public async Task Traffic_logs_explain_TAP_only_on_bridges_set_to_TAP(ProtocolDecode decode, string expectedEnd)
    {
        var bridge = new BridgeConfig { Name = "Pager", Decode = decode, TrafficLog = new TrafficLogOptions { Enabled = true } };
        TrafficRecord? recorded = null;
        await using (var hub = new TrafficHub { LogRoot = Shared, FallbackRoot = Fallback, LogBatchDelay = TimeSpan.FromMilliseconds(50) })
        {
            hub.Recorded += r => recorded = r;
            hub.Record(bridge, TrafficDirection.Forward, "Virtual COM5", [0x04, 0x0D]);
        }
        Assert.EndsWith(expectedEnd, Read(Directory.GetFiles(Path.Combine(Shared, "traffic")).Single()).TrimEnd());
        Assert.Equal(decode == ProtocolDecode.Tap ? "Log off: no more pages" : null, recorded?.Decoded); // what the Monitor gets
    }

    [Fact]
    public async Task Traffic_logs_older_than_the_keep_days_are_deleted()
    {
        var traffic = Path.Combine(Shared, "traffic");
        Directory.CreateDirectory(traffic);
        var old = Path.Combine(traffic, "Pager-1836bd3e3842-20200101.log");
        var recent = Path.Combine(traffic, $"Pager-1836bd3e3842-{DateTime.Now.AddDays(-2):yyyyMMdd}.log");
        File.WriteAllText(old, "old");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-40));
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTime(recent, DateTime.Now.AddDays(-2));

        var bridge = new BridgeConfig { Name = "Scale", TrafficLog = new TrafficLogOptions { Enabled = true } };
        await using (var hub = new TrafficHub { LogRoot = Shared, FallbackRoot = Fallback, RetentionDays = 30, LogBatchDelay = TimeSpan.FromMilliseconds(50) })
        {
            hub.Record(bridge, TrafficDirection.Forward, "COM3", "12.5 kg"u8.ToArray());
            await Wait.Until(() => !File.Exists(old), what: "the 40-day-old traffic log deleted");
        }
        Assert.True(File.Exists(recent), "a 2-day-old traffic log must stay");
        Assert.Contains(Directory.GetFiles(traffic), f => Read(f).Contains("12.5 kg"));
    }

    [Fact]
    public async Task Traffic_log_cleanup_does_not_follow_a_junction_planted_as_the_traffic_folder()
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var victim = Path.Combine(elsewhere, "important.log");
        File.WriteAllText(victim, "keep");
        File.SetLastWriteTime(victim, DateTime.Now.AddDays(-400));
        Directory.CreateDirectory(Shared);
        MkLink("/J", Path.Combine(Shared, "traffic"), elsewhere);

        var bridge = new BridgeConfig { Name = "Scale", TrafficLog = new TrafficLogOptions { Enabled = true } };
        await using (var hub = new TrafficHub { LogRoot = Shared, FallbackRoot = Fallback, RetentionDays = 30, Warn = _ => { }, LogBatchDelay = TimeSpan.FromMilliseconds(50) })
        {
            hub.Record(bridge, TrafficDirection.Forward, "COM3", "12.5 kg"u8.ToArray());
            await Wait.Until(() => Directory.Exists(Path.Combine(Fallback, "traffic")) && Directory.GetFiles(Path.Combine(Fallback, "traffic")).Length > 0,
                what: "traffic log in the fallback folder");
            await Task.Delay(300); // the cleanup runs right after that write
        }
        Assert.True(File.Exists(victim), "nothing behind the junction may be deleted");
    }

    [Fact]
    public async Task Logs_and_folders_stay_usable_while_PortBridge_writes_them()
    {
        var bridge = new BridgeConfig { Name = "Scale", TrafficLog = new TrafficLogOptions { Enabled = true, Format = TrafficLogFormat.Text } };
        var traffic = Path.Combine(Shared, "traffic");
        await using var hub = new TrafficHub { LogRoot = Shared, FallbackRoot = Fallback, LogBatchDelay = TimeSpan.FromMilliseconds(50) };
        hub.Record(bridge, TrafficDirection.Forward, "COM3", "first"u8.ToArray());
        await Wait.Until(() => Directory.Exists(traffic) && Directory.GetFiles(traffic).Any(f => Read(f).Contains("first")), what: "traffic log");
        var path = Directory.GetFiles(traffic).Single();

        // Logging is still on. A viewer that only shares reading (File.ReadAllText, many editors) can open it, so can an
        // editor that writes, and neither folder is held.
        Assert.Contains("first", File.ReadAllText(path));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
        Assert.True(CanRenameOrDelete(Shared), "PortBridgeLogs must not be locked");
        Assert.True(CanRenameOrDelete(traffic), "the traffic folder must not be locked");

        // Someone deletes it meanwhile: the next line starts a new file instead of being lost.
        File.Delete(path);
        Assert.False(File.Exists(path));
        hub.Record(bridge, TrafficDirection.Forward, "COM3", "second"u8.ToArray());
        await Wait.Until(() => Read(path).Contains("second"), what: "new traffic log after the deletion");
        Assert.DoesNotContain("first", Read(path));
    }
}
