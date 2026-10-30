using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;
using PortBridge.Core.Config;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;

namespace PortBridge.Core.Engine;

internal static class SocketTuning
{
    public static void Apply(Socket s, EndpointConfig c)
    {
        try
        {
            s.NoDelay = c.NoDelay;
            if (c.TcpKeepAlive)
            {
                var secs = Math.Clamp(c.KeepAliveSeconds, 1, 7200);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, secs);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, Math.Max(1, secs / 2));
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
        }
        catch (SocketException)
        {
            // Older stacks may not support every option; the connection still works.
        }
    }

    public static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "connection refused (nothing listening on that port?)",
        SocketException { SocketErrorCode: SocketError.ConnectionReset } => "connection reset by the remote side",
        SocketException { SocketErrorCode: SocketError.TimedOut } => "connection timed out",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "host name could not be resolved",
        SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable } => "network/host unreachable",
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => "port is already in use by another program",
        SocketException { SocketErrorCode: SocketError.AccessDenied } => "access denied (port reserved or blocked)",
        SocketException se => se.Message,
        IOException { InnerException: SocketException inner } => Describe(inner),
        AuthenticationException ae => "TLS handshake failed: " + (ae.InnerException?.Message ?? ae.Message),
        _ => ex.Message,
    };
}

internal static class TlsHelper
{
    public static async Task<(Stream Stream, string Info)> ServerAsync(Socket socket, EndpointConfig cfg, CertificateProvider certs, CancellationToken ct)
    {
        var net = new NetworkStream(socket, ownsSocket: false);
        if (!cfg.Tls.Enabled) return (net, "");
        var ssl = new SslStream(net, leaveInnerStreamOpen: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certs.GetServerCertificate(cfg.Tls),
            ClientCertificateRequired = false,
        }, timeout.Token);
        return (ssl, $"{ssl.SslProtocol}");
    }

    public static async Task<(Stream Stream, string Info)> ClientAsync(Socket socket, EndpointConfig cfg, CancellationToken ct)
    {
        var net = new NetworkStream(socket, ownsSocket: false);
        if (!cfg.Tls.Enabled) return (net, "");
        var ssl = new SslStream(net, leaveInnerStreamOpen: false);
        var target = string.IsNullOrWhiteSpace(cfg.Tls.ServerName) ? cfg.Host : cfg.Tls.ServerName!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = target,
            RemoteCertificateValidationCallback = (_, cert, _, errors) => cfg.Tls.Validation switch
            {
                TlsValidation.AcceptAny => true,
                TlsValidation.Pinned => cert is not null && MatchesPin(cert, cfg.Tls.PinnedThumbprint),
                _ => errors == SslPolicyErrors.None,
            },
        }, timeout.Token);
        return (ssl, $"{ssl.SslProtocol}");
    }

    private static bool MatchesPin(System.Security.Cryptography.X509Certificates.X509Certificate cert, string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return false;
        var clean = new string(pin.Where(Uri.IsHexDigit).ToArray());
        return clean.Equals(cert.GetCertHashString(), StringComparison.OrdinalIgnoreCase) ||
               clean.Equals(CertificateProvider.Sha256Thumbprint(cert), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A TCP connection (plain or TLS, optionally speaking RFC 2217).
/// Output: a data queue and a control queue (Telnet/RFC 2217 replies), written by two loops sharing one write lock.
/// The control queue is bounded by message count and bytes, and control + data bytes share the session's queue limit.
/// A peer that keeps sending reply-generating commands without reading the replies hits that bound and the session is
/// closed (reason logged) — replies are never silently dropped, the reader never blocks, and stop never waits forever.</summary>
internal sealed class TcpSession : Session
{
    internal const int DefaultMaxControlMessages = 1024;
    internal const long DefaultMaxControlBytes = 256 * 1024;

    private readonly Socket? _socket;
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<byte[]> _control = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly int _maxControlMessages;
    private readonly long _maxControlBytes;
    private readonly long _maxQueued;
    private int _controlCount;
    private long _controlBytes;
    private Task _writers = Task.CompletedTask;
    private readonly string _remote;
    private readonly string _tlsInfo;
    private readonly EndpointConfig _cfg;

    public TcpSession(Endpoint owner, Socket socket, Stream stream, string tlsInfo, CancellationToken parent)
        : this(owner, socket, stream, tlsInfo, parent, null) { }

    /// <summary>Tests pass a fake stream (no socket) and small limits.</summary>
    internal TcpSession(Endpoint owner, Socket? socket, Stream stream, string tlsInfo, CancellationToken parent, string? remote,
        int maxControlMessages = DefaultMaxControlMessages, long maxControlBytes = DefaultMaxControlBytes, long maxQueuedBytes = 16 * 1024 * 1024)
        : base(owner)
    {
        _socket = socket;
        _stream = stream;
        _tlsInfo = tlsInfo;
        _cfg = owner.Config;
        _maxControlMessages = maxControlMessages;
        _maxControlBytes = maxControlBytes;
        _maxQueued = maxQueuedBytes;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        RemoteEndPoint = socket?.RemoteEndPoint as IPEndPoint;
        var addr = RemoteEndPoint?.Address;
        if (addr is { IsIPv4MappedToIPv6: true }) addr = addr.MapToIPv4();
        _remote = remote ?? (addr is null ? "?" : $"{addr}:{RemoteEndPoint!.Port}");
    }

    protected override long MaxQueuedBytes => _maxQueued;

    protected override long OtherQueuedBytes => Interlocked.Read(ref _controlBytes);

    /// <summary>Test seam: called at "control:checked" (control admission decided, not yet committed). Null in production.</summary>
    internal Action<string>? InterleavingHook { get; set; }

    /// <summary>Control messages waiting to be written.</summary>
    internal int ControlQueueCount => Volatile.Read(ref _controlCount);

    public override Task WriterCompletion => _writers;

    public IPEndPoint? RemoteEndPoint { get; }
    public Rfc2217Protocol? Rfc2217 { get; private set; }

    public override string Remote => _remote;

    public override string? Detail
    {
        get
        {
            var parts = new List<string>();
            if (_tlsInfo.Length > 0) parts.Add(_tlsInfo);
            if (Rfc2217 is { } r)
            {
                parts.Add(r.ComPortActive ? "RFC 2217" : "RFC 2217 (negotiating)");
                if (r.ConfirmedSettings is { } s) parts.Add(s);
                if (r.PeerSignature is { } sig) parts.Add(sig);
            }
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    public void AttachRfc2217(Rfc2217Protocol protocol) => Rfc2217 = protocol;

    /// <summary>Queues Telnet commands (unescaped) outside the data queue. Returns false if the session is closed, or if
    /// admitting it would exceed the control bounds — in that case the session is closed (slow-peer policy).</summary>
    public bool SendControl(byte[] raw)
    {
        if (raw.Length == 0) return !IsClosed;
        string overflow;
        lock (AdmissionGate) // shared with TrySend: check and reservation are one step for the combined budget
        {
            if (IsClosed) return false;
            if (_controlCount + 1 > _maxControlMessages || _controlBytes + raw.Length > _maxControlBytes)
            {
                overflow = $"control reply queue full ({_controlCount} messages, {_controlBytes:N0} bytes) — the peer keeps sending commands but does not read the replies";
            }
            else if (DataQueuedBytes + _controlBytes + raw.Length > MaxQueuedBytes)
            {
                overflow = "send queue overflow — the receiver is not reading fast enough";
            }
            else
            {
                InterleavingHook?.Invoke("control:checked");
                if (!_control.Writer.TryWrite(raw)) return false; // completed by Close
                _controlCount++;
                Interlocked.Add(ref _controlBytes, raw.Length);
                return true;
            }
        }
        Close(overflow);
        return false;
    }

    public async Task RunAsync(Action<ReadOnlySpan<byte>> onData)
    {
        var ct = _cts.Token;
        var writer = Guard(WriterLoopAsync(ct), "send failed");
        var control = Guard(ControlLoopAsync(ct), "send failed");
        _writers = Task.WhenAll(writer, control);
        var watchdog = Guard(WatchdogAsync(ct), "watchdog");

        if (Rfc2217 is { } rfc)
        {
            rfc.Start();
            _ = Task.Delay(1500, ct).ContinueWith(_ => rfc.EnsureClientSettingsSent(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        var buffer = new byte[16 * 1024];
        var decoded = new ArrayBufferWriter<byte>(16 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    Close("closed by the remote side");
                    break;
                }
                if (Rfc2217 is { } p)
                {
                    decoded.ResetWrittenCount();
                    p.Decode(buffer.AsSpan(0, n), decoded);
                    if (decoded.WrittenCount > 0) onData(decoded.WrittenSpan);
                }
                else
                {
                    onData(buffer.AsSpan(0, n));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Close(SocketTuning.Describe(ex));
        }
        finally
        {
            Close("connection ended");
            await Task.WhenAll(writer, control, watchdog).ConfigureAwait(false);
        }
    }

    private async Task Guard(Task t, string what)
    {
        try { await t.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Close($"{what}: {SocketTuning.Describe(ex)}"); }
    }

    protected override async ValueTask WriteCoreAsync(byte[] data, CancellationToken ct)
    {
        if (Rfc2217 is { } rfc)
        {
            while (rfc.Suspended && !ct.IsCancellationRequested) await Task.Delay(20, ct).ConfigureAwait(false);
            data = rfc.Encode(data);
        }
        await WriteRawAsync(data, ct).ConfigureAwait(false);
    }

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        await foreach (var raw in _control.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await WriteRawAsync(raw, ct).ConfigureAwait(false);
            lock (AdmissionGate)
            {
                _controlCount--;
                Interlocked.Add(ref _controlBytes, -raw.Length);
            }
        }
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(data, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private async Task WatchdogAsync(CancellationToken ct)
    {
        var idle = _cfg.IdleTimeoutSeconds;
        var ping = _cfg.PingWatchdog && RemoteEndPoint is not null;
        if (idle <= 0 && !ping) return;

        var failures = 0;
        var lastPing = DateTimeOffset.Now;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = DateTimeOffset.Now;
            if (idle > 0 && now - LastReceive > TimeSpan.FromSeconds(idle))
            {
                Close($"no data for {idle} s (idle timeout)");
                return;
            }
            if (ping && now - lastPing >= TimeSpan.FromSeconds(Math.Max(1, _cfg.PingIntervalSeconds)))
            {
                lastPing = now;
                try
                {
                    using var p = new Ping();
                    var addr = RemoteEndPoint!.Address.IsIPv4MappedToIPv6 ? RemoteEndPoint.Address.MapToIPv4() : RemoteEndPoint.Address;
                    var reply = await p.SendPingAsync(addr, TimeSpan.FromMilliseconds(Math.Max(100, _cfg.PingTimeoutMs)), cancellationToken: ct).ConfigureAwait(false);
                    failures = reply.Status == IPStatus.Success ? 0 : failures + 1;
                }
                catch (OperationCanceledException) { throw; }
                catch { failures++; }

                if (failures >= Math.Max(1, _cfg.PingFailures))
                {
                    Close($"remote host stopped answering pings ({failures} missed)");
                    return;
                }
            }
        }
    }

    protected override void OnClose()
    {
        _control.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { /* already closed */ }
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _socket?.Dispose(); } catch { /* ignore */ }
    }

    internal void NotifyModemLines(ModemLines lines) => Rfc2217?.NotifyModemLines(lines);
}
