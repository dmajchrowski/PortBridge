using System.Net;
using System.Net.Sockets;
using System.Text;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;

namespace PortBridge.Tests;

public sealed class EngineFixture : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "portbridge-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public BridgeEngine Engine { get; }

    public EngineFixture()
    {
        AppPaths.DataDir = Dir;
        AppPaths.LogDir = Path.Combine(Dir, "shared-logs");
        Directory.CreateDirectory(Dir);
        Engine = new BridgeEngine(new ConfigStore(Path.Combine(Dir, "config.json")));
        Engine.StartAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch { /* ignore */ }
    }
}

public class EngineTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static EndpointConfig Server(int port, Action<EndpointConfig>? tweak = null)
    {
        var e = new EndpointConfig { Kind = EndpointKind.TcpServer, Port = port, Host = "127.0.0.1" };
        tweak?.Invoke(e);
        return e;
    }

    private static EndpointConfig Client(int port, Action<EndpointConfig>? tweak = null)
    {
        var e = new EndpointConfig { Kind = EndpointKind.TcpClient, Host = "127.0.0.1", Port = port, ReconnectDelayMs = 200 };
        tweak?.Invoke(e);
        return e;
    }

    private static async Task<TcpClient> Connect(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var c = new TcpClient();
            try
            {
                await c.ConnectAsync(IPAddress.Loopback, port);
                return c;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                c.Dispose();
                await Task.Delay(50);
            }
        }
    }

    private static async Task<string> ReadText(TcpClient c, int expectedLength, int timeoutMs = 5000)
    {
        var buf = new byte[4096];
        var sb = new List<byte>();
        using var cts = new CancellationTokenSource(timeoutMs);
        while (sb.Count < expectedLength)
        {
            var n = await c.GetStream().ReadAsync(buf, cts.Token);
            if (n == 0) break;
            sb.AddRange(buf.AsSpan(0, n).ToArray());
        }
        return Encoding.Latin1.GetString(sb.ToArray());
    }

    private static async Task<bool> NothingArrives(TcpClient c, int ms = 300)
    {
        using var cts = new CancellationTokenSource(ms);
        try
        {
            var n = await c.GetStream().ReadAsync(new byte[16], cts.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(25);
        }
    }

    private static Task Send(TcpClient c, string text) => c.GetStream().WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

    [Fact]
    public async Task Tcp_hub_forwards_both_ways()
    {
        await using var f = new EngineFixture();
        int p1 = FreePort(), p2 = FreePort();
        var view = await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "hub",
            Active = true,
            Primary = Server(p1),
            Targets = [Server(p2)],
        });

        using var x = await Connect(p1);
        using var y = await Connect(p2);
        await Until(() => f.Engine.Get(view.Id)!.State == "connected");

        await Send(x, "hello");
        Assert.Equal("hello", await ReadText(y, 5));
        await Send(y, "world");
        Assert.Equal("world", await ReadText(x, 5));

        var stats = f.Engine.Get(view.Id)!.Stats!;
        Assert.Equal(5, stats.ForwardBytes);
        Assert.Equal(5, stats.ReverseBytes);
    }

    [Fact]
    public async Task Port_forwarder_reaches_echo_server()
    {
        await using var f = new EngineFixture();
        int p1 = FreePort(), echoPort = FreePort();
        var echo = new TcpListener(IPAddress.Loopback, echoPort);
        echo.Start();
        _ = Task.Run(async () =>
        {
            using var s = await echo.AcceptTcpClientAsync();
            var buf = new byte[1024];
            int n;
            while ((n = await s.GetStream().ReadAsync(buf)) > 0) await s.GetStream().WriteAsync(buf.AsMemory(0, n));
        });

        await f.Engine.SaveBridgeAsync(new BridgeConfig { Name = "fwd", Active = true, Primary = Server(p1), Targets = [Client(echoPort)] });
        using var c = await Connect(p1);
        await Task.Delay(300); // let the outbound connection come up
        await Send(c, "pingÿ");
        Assert.Equal("pingÿ", await ReadText(c, 5));
        echo.Stop();
    }

    [Fact]
    public async Task First_client_only_input_and_broadcast_output()
    {
        await using var f = new EngineFixture();
        int p1 = FreePort(), p2 = FreePort();
        var view = await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "first",
            Active = true,
            Primary = Server(p2),
            Targets = [Server(p1, e => e.ReceiveFrom = SessionSelect.First)],
        });

        using var device = await Connect(p2);
        using var first = await Connect(p1);
        await Until(() => f.Engine.Get(view.Id)!.Targets[0].Sessions.Count == 1);
        using var second = await Connect(p1);
        await Until(() => f.Engine.Get(view.Id)!.Targets[0].Sessions.Count == 2);

        await Send(second, "nope");
        Assert.True(await NothingArrives(device));
        await Send(first, "yes");
        Assert.Equal("yes", await ReadText(device, 3));

        await Send(device, "all");
        Assert.Equal("all", await ReadText(first, 3));
        Assert.Equal("all", await ReadText(second, 3));
    }

    [Fact]
    public async Task Hold_buffer_delivers_after_connect()
    {
        await using var f = new EngineFixture();
        int p1 = FreePort(), p2 = FreePort();
        var view = await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "hold",
            Active = true,
            Primary = Server(p1),
            Targets = [Server(p2, e => e.HoldWhileDisconnected = true)],
        });

        using var x = await Connect(p1);
        await Until(() => f.Engine.Get(view.Id)!.Primary.Sessions.Count == 1);
        await Send(x, "kept");
        await Until(() => f.Engine.Get(view.Id)!.Stats!.HeldBytes == 4);

        using var y = await Connect(p2);
        Assert.Equal("kept", await ReadText(y, 4));
    }

    [Fact]
    public async Task Connect_on_demand_follows_the_other_side()
    {
        await using var f = new EngineFixture();
        int p1 = FreePort(), upstream = FreePort();
        var accepted = 0;
        var upstreamListener = new TcpListener(IPAddress.Loopback, upstream);
        upstreamListener.Start();
        var conns = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient s;
                try { s = await upstreamListener.AcceptTcpClientAsync(); }
                catch { return; }
                Interlocked.Increment(ref accepted);
                lock (conns) conns.Add(s);
            }
        });

        var view = await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "ondemand",
            Active = true,
            Primary = Server(p1),
            Targets = [Client(upstream, e => e.ConnectOnDemand = true)],
        });

        await Task.Delay(500);
        Assert.Equal(0, accepted);

        var x = await Connect(p1);
        await Until(() => accepted == 1);
        await Until(() => f.Engine.Get(view.Id)!.State == "connected");

        x.Dispose();
        await Until(() => f.Engine.Get(view.Id)!.Targets[0].Sessions.Count == 0);
        upstreamListener.Stop();
    }

    [Fact]
    public async Task Rfc2217_link_between_two_bridges_is_transparent()
    {
        await using var f = new EngineFixture();
        int telnetPort = FreePort(), devicePort = FreePort(), appPort = FreePort();

        // "Device server" side: RFC 2217 server in front of a plain TCP endpoint standing in for the serial device.
        await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "server",
            Active = true,
            Primary = Server(devicePort),
            Targets = [Server(telnetPort, e => e.Rfc2217 = true)],
        });
        // "Remote PC" side: RFC 2217 client.
        var client = await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "client",
            Active = true,
            Primary = Server(appPort),
            Targets = [Client(telnetPort, e => { e.Rfc2217 = true; e.BaudRate = 19200; })],
        });

        using var device = await Connect(devicePort);
        using var app = await Connect(appPort);
        await Until(() => f.Engine.Get(client.Id)!.State == "connected");
        await Task.Delay(300);

        var payload = "AÿBÿÿC"; // IAC bytes must survive the Telnet escaping
        await Send(app, payload);
        Assert.Equal(payload, await ReadText(device, payload.Length));
        await Send(device, payload);
        Assert.Equal(payload, await ReadText(app, payload.Length));

        await Until(() => f.Engine.Get(client.Id)!.Targets[0].Sessions.FirstOrDefault()?.Detail?.Contains("19200") == true);
    }

    [Fact]
    public async Task Tls_link_carries_data_and_rejects_wrong_pin()
    {
        await using var f = new EngineFixture();
        int device = FreePort(), tlsPort = FreePort(), app = FreePort(), app2 = FreePort();
        await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "tls-server", Active = true,
            Primary = Server(device),
            Targets = [Server(tlsPort, e => e.Tls = new TlsOptions { Enabled = true })],
        });
        var good = await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "tls-client", Active = true,
            Primary = Server(app),
            Targets = [Client(tlsPort, e => e.Tls = new TlsOptions { Enabled = true, Validation = TlsValidation.AcceptAny })],
        });
        var bad = await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "tls-wrong-pin", Active = true,
            Primary = Server(app2),
            Targets = [Client(tlsPort, e => e.Tls = new TlsOptions { Enabled = true, Validation = TlsValidation.Pinned, PinnedThumbprint = "00112233" })],
        });

        using var dev = await Connect(device);
        using var a = await Connect(app);
        await Until(() => f.Engine.Get(good.Id)!.State == "connected", 10_000);
        Assert.Contains("Tls", f.Engine.Get(good.Id)!.Targets[0].Sessions[0].Detail);

        await Send(a, "secret");
        Assert.Equal("secret", await ReadText(dev, 6));
        await Send(dev, "reply");
        Assert.Equal("reply", await ReadText(a, 5));

        await Until(() => f.Engine.Get(bad.Id)!.Targets[0].LastError?.Contains("TLS handshake failed") == true, 10_000);
    }

    [Fact]
    public async Task Rejects_clients_outside_allow_list_and_over_limit()
    {
        await using var f = new EngineFixture();
        int p1 = FreePort(), p2 = FreePort(), p3 = FreePort();
        var view = await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "acl",
            Active = true,
            Primary = Server(p1, e => e.AllowedClients = ["10.0.0.0/8"]),
            Targets = [Server(p2, e => e.MaxClients = 1)],
        });

        using var denied = await Connect(p1);
        Assert.Equal("", await ReadText(denied, 1)); // closed immediately
        using var one = await Connect(p2);
        await Until(() => f.Engine.Get(view.Id)!.Targets[0].Sessions.Count == 1);
        using var two = await Connect(p2);
        Assert.Equal("", await ReadText(two, 1));
        Assert.Contains(f.Engine.Journal.Query(), e => e.Message.Contains("limit of 1 client"));
    }

    [Fact]
    public async Task Udp_endpoint_replies_to_last_sender()
    {
        await using var f = new EngineFixture();
        int tcp = FreePort();
        var udpPort = FreePort();
        await f.Engine.SaveBridgeAsync(new BridgeConfig
        {
            Name = "udp",
            Active = true,
            Primary = Server(tcp),
            Targets = [new EndpointConfig { Kind = EndpointKind.Udp, Port = udpPort }],
        });

        using var app = await Connect(tcp);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await Task.Delay(300);
        await udp.SendAsync("dgram"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, udpPort));
        Assert.Equal("dgram", await ReadText(app, 5));

        await Send(app, "reply");
        using var cts = new CancellationTokenSource(3000);
        var r = await udp.ReceiveAsync(cts.Token);
        Assert.Equal("reply", Encoding.ASCII.GetString(r.Buffer));
    }
}
