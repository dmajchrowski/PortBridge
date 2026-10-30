using System.Text.Json.Serialization;

namespace PortBridge.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter<EndpointKind>))]
public enum EndpointKind { Serial, VirtualSerial, TcpServer, TcpClient, Udp }

[JsonConverter(typeof(JsonStringEnumConverter<SerialParity>))]
public enum SerialParity { None, Odd, Even, Mark, Space }

[JsonConverter(typeof(JsonStringEnumConverter<SerialStopBits>))]
public enum SerialStopBits { One, OnePointFive, Two }

[JsonConverter(typeof(JsonStringEnumConverter<FlowControl>))]
public enum FlowControl { None, RtsCts, DtrDsr, XonXoff }

/// <summary>Which sessions of a multi-client endpoint (TCP server) take part in a data direction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionSelect>))]
public enum SessionSelect { All, First, None }

[JsonConverter(typeof(JsonStringEnumConverter<PacketMode>))]
public enum PacketMode { Immediate, IdleTimeout, Delimiter }

[JsonConverter(typeof(JsonStringEnumConverter<TlsValidation>))]
public enum TlsValidation { Strict, AcceptAny, Pinned }

[JsonConverter(typeof(JsonStringEnumConverter<TrafficLogFormat>))]
public enum TrafficLogFormat { Hex, Text }

public sealed class TlsOptions
{
    public bool Enabled { get; set; }
    /// <summary>Server: PFX file. Empty = auto-generated self-signed certificate.</summary>
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    /// <summary>Client: how the server certificate is checked.</summary>
    public TlsValidation Validation { get; set; } = TlsValidation.Strict;
    /// <summary>Client, Pinned mode: SHA-1 or SHA-256 thumbprint (hex).</summary>
    public string? PinnedThumbprint { get; set; }
    /// <summary>Client: SNI / name to validate. Empty = the host name.</summary>
    public string? ServerName { get; set; }
}

public sealed class EndpointConfig
{
    public string Id { get; set; } = NewId();
    public EndpointKind Kind { get; set; } = EndpointKind.Serial;
    public string? Label { get; set; }

    // ---- Serial / virtual serial (also used as the remote settings of an RFC 2217 client) ----
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public SerialParity Parity { get; set; } = SerialParity.None;
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;
    public FlowControl FlowControl { get; set; } = FlowControl.None;
    public bool Dtr { get; set; } = true;
    public bool Rts { get; set; } = true;
    public int ReopenDelayMs { get; set; } = 3000;
    /// <summary>Virtual port: raise DSR/DCD for the application only while the other side is connected.</summary>
    public bool SignalPeerState { get; set; }
    /// <summary>Virtual port: emulate baud-rate timing (com0com EmuBR).</summary>
    public bool EmulateBaudRate { get; set; }
    /// <summary>Virtual port: pass control/modem lines through an RFC 2217 link on the other side. The application's
    /// DTR/RTS are sent to the remote port; the remote CTS drives the application's CTS and the remote DSR drives its
    /// DSR+DCD (com0com wires both to one pin). RI, break and baud/format changes are not passed (see README).</summary>
    public bool ModemPassthrough { get; set; }

    // ---- Network ----
    /// <summary>TCP client / UDP: remote host. TCP server: bind address (empty = all interfaces).</summary>
    public string Host { get; set; } = "";
    /// <summary>TCP server: listen port. TCP client / UDP: remote port.</summary>
    public int Port { get; set; } = 4001;
    /// <summary>UDP: local port to bind (0 = same as Port).</summary>
    public int LocalPort { get; set; }
    public bool Broadcast { get; set; }
    public int MaxClients { get; set; }
    /// <summary>TCP server: IPs or CIDR ranges allowed to connect. Empty = everyone.</summary>
    public List<string> AllowedClients { get; set; } = [];
    public int ReconnectDelayMs { get; set; } = 2000;
    public int ConnectTimeoutMs { get; set; } = 5000;
    /// <summary>TCP client: connect only while the other side of the bridge has an active session
    /// (e.g. an application has the virtual COM port open).</summary>
    public bool ConnectOnDemand { get; set; }
    public bool NoDelay { get; set; } = true;
    public bool TcpKeepAlive { get; set; } = true;
    public int KeepAliveSeconds { get; set; } = 10;
    public bool PingWatchdog { get; set; }
    public int PingIntervalSeconds { get; set; } = 10;
    public int PingTimeoutMs { get; set; } = 2000;
    public int PingFailures { get; set; } = 3;
    /// <summary>Drop (and reconnect) a session that has received nothing for this long. 0 = off.</summary>
    public int IdleTimeoutSeconds { get; set; }
    /// <summary>Telnet COM Port Control (RFC 2217).</summary>
    public bool Rfc2217 { get; set; }
    public TlsOptions Tls { get; set; } = new();

    // ---- Routing ----
    /// <summary>Forward data received from this endpoint to the other side.</summary>
    public bool Receive { get; set; } = true;
    /// <summary>Deliver data from the other side to this endpoint.</summary>
    public bool Transmit { get; set; } = true;
    public SessionSelect ReceiveFrom { get; set; } = SessionSelect.All;
    public SessionSelect TransmitTo { get; set; } = SessionSelect.All;

    // ---- Buffering & packetizing ----
    /// <summary>Keep data destined for this endpoint while it has no session, deliver on (re)connect.</summary>
    public bool HoldWhileDisconnected { get; set; }
    public int HoldBufferKb { get; set; } = 512;
    /// <summary>How data read from this endpoint is grouped into packets before forwarding.</summary>
    public PacketMode PacketMode { get; set; } = PacketMode.Immediate;
    public int PacketTimeoutMs { get; set; } = 150;
    public int MaxPacketBytes { get; set; } = 2000;
    /// <summary>Delimiter with C-style escapes, e.g. "\r\n".</summary>
    public string Delimiter { get; set; } = "\\r\\n";

    [JsonIgnore]
    public bool IsMultiSession => Kind == EndpointKind.TcpServer;

    [JsonIgnore]
    public bool IsSerial => Kind is EndpointKind.Serial or EndpointKind.VirtualSerial;

    public string Describe() => Kind switch
    {
        EndpointKind.Serial => $"{PortName} {BaudRate}-{DataBits}-{Parity.ToString()[0]}-{StopBitsText}",
        EndpointKind.VirtualSerial => $"Virtual {PortName}",
        EndpointKind.TcpServer => $"TCP server :{Port}",
        EndpointKind.TcpClient => $"TCP client {Host}:{Port}",
        EndpointKind.Udp => $"UDP {(string.IsNullOrEmpty(Host) ? "" : Host)}:{Port}",
        _ => Kind.ToString(),
    };

    [JsonIgnore]
    public string StopBitsText => StopBits switch
    {
        SerialStopBits.OnePointFive => "1.5",
        SerialStopBits.Two => "2",
        _ => "1",
    };

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public sealed class TrafficLogOptions
{
    public bool Enabled { get; set; }
    public TrafficLogFormat Format { get; set; } = TrafficLogFormat.Hex;
    public int MaxFileMb { get; set; } = 20;
}

public sealed class BridgeConfig
{
    public string Id { get; set; } = EndpointConfig.NewId();
    public string Name { get; set; } = "New bridge";
    public string? Notes { get; set; }
    /// <summary>Desired state; remembered across restarts like TCP-Com's "re-activate on load".</summary>
    public bool Active { get; set; }
    public EndpointConfig Primary { get; set; } = new();
    public List<EndpointConfig> Targets { get; set; } = [];
    public TrafficLogOptions TrafficLog { get; set; } = new();

    public IEnumerable<EndpointConfig> AllEndpoints()
    {
        yield return Primary;
        foreach (var t in Targets) yield return t;
    }
}

public sealed class AppSettings
{
    public int WebPort { get; set; } = 8470;
    /// <summary>Listen on all interfaces so the dashboard can be used from other machines (requires a password).</summary>
    public bool AllowRemote { get; set; }
    public bool UseHttps { get; set; }
    public string? PasswordHash { get; set; }
    /// <summary>Ask for the password in the desktop window too (TCP-Com's "password protect" behaviour).</summary>
    public bool RequirePasswordLocally { get; set; }
    public bool WriteEventLogFile { get; set; } = true;
    public int LogRetentionDays { get; set; } = 30;
    /// <summary>Command line run when an error occurs. %BRIDGE% and %MESSAGE% are substituted.</summary>
    public string? OnErrorCommand { get; set; }
    /// <summary>JSON POST target for events (compatible with Slack-style {"text": ...} webhooks).</summary>
    public string? WebhookUrl { get; set; }
    public bool WebhookIncludeConnections { get; set; }
    public bool DesktopNotifications { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public string? Com0ComPath { get; set; }

    // Settings that make the service run a program are set aside (not used) when the service took over a data folder
    // that non-administrators could edit. An administrator re-enters them in Settings to use them again.
    public string? QuarantinedOnErrorCommand { get; set; }
    public string? QuarantinedCom0ComPath { get; set; }
    public string? QuarantineNote { get; set; }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<BridgeConfig> Bridges { get; set; } = [];
}
