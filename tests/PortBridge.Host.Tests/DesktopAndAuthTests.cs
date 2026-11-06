using System.Net;
using System.Xml.Linq;
using PortBridge.Desktop;
using PortBridge.Host;

namespace PortBridge.Host.Tests;

public class SignInTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Definition_starts_the_exe_minimized_at_this_users_sign_in_with_highest_privileges()
    {
        var task = XDocument.Parse(SignInTask.Definition(@"C:\Program Files\PortBridge & Co\PortBridge.exe", "S-1-5-21-1-2-3-1001"));

        Assert.Equal("S-1-5-21-1-2-3-1001", task.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        var principal = task.Descendants(Ns + "Principal").Single();
        Assert.Equal("S-1-5-21-1-2-3-1001", principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value); // no stored password
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);  // elevated without a UAC prompt
        var exec = task.Descendants(Ns + "Exec").Single();
        Assert.Equal(@"C:\Program Files\PortBridge & Co\PortBridge.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal("--minimized", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal("false", task.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
    }
}

public class WindowSignInAgainTests
{
    private const string Base = "http://localhost:8470";

    [Theory]
    [InlineData("#/monitor/1836bd3e3842")]
    [InlineData("#/settings")]
    [InlineData("#/monitor/a%20b")]
    public void Keeps_the_dashboard_page_the_window_was_on(string hash) =>
        Assert.Equal($"{Base}/auth/local?token=ABC123{hash}", DesktopApp.LocalSignInUrl(Base, "ABC123", hash));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#/")]
    [InlineData("#/login")]            // would land on the sign-in page again
    [InlineData("monitor")]            // not a dashboard fragment
    [InlineData("#/x?token=evil")]     // only plain path characters are passed on
    [InlineData("#/x\"><script>")]
    public void Drops_anything_else(string? hash) =>
        Assert.Equal($"{Base}/auth/local?token=ABC123", DesktopApp.LocalSignInUrl(Base, "ABC123", hash));
}

public class SignInLockoutTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.0.2.10");

    [Fact]
    public void Fifth_wrong_password_locks_the_address_and_reports_how_long()
    {
        var auth = new AuthService(() => new PortBridge.Core.Config.AppSettings());
        for (var i = 1; i <= 4; i++) Assert.Null(auth.RecordFailure(Ip));
        Assert.Null(auth.LockedOut(Ip));

        Assert.Equal(TimeSpan.FromSeconds(15), auth.RecordFailure(Ip));
        Assert.NotNull(auth.LockedOut(Ip));
        Assert.Null(auth.LockedOut(IPAddress.Parse("192.0.2.11"))); // per address

        Assert.Equal(TimeSpan.FromSeconds(30), auth.RecordFailure(Ip)); // doubles until a success
        auth.RecordSuccess(Ip);
        Assert.Null(auth.LockedOut(Ip));
        Assert.Null(auth.RecordFailure(Ip));
    }
}
