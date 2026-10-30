using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;

namespace PortBridge.Host;

/// <summary>
/// One WebSocket per open dashboard. Pushes a status snapshot every second, events as they happen,
/// and captured traffic (batched every 100 ms) for the bridges the page is monitoring.
/// </summary>
public static class LiveSocket
{
    private const int MaxTrafficBytesPerBatch = 256 * 1024;

    public static async Task HandleAsync(HttpContext ctx, HostRuntime rt)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var engine = rt.Engine;
        var subscriptions = new ConcurrentDictionary<string, byte>();
        var traffic = new ConcurrentQueue<TrafficRecord>();
        var events = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        long trafficDropped = 0;

        void OnEvent(EngineEvent e) => events.Writer.TryWrite(e);
        void OnTraffic(TrafficRecord r)
        {
            if (!subscriptions.ContainsKey(r.BridgeId)) return;
            if (traffic.Count > 5000) Interlocked.Increment(ref trafficDropped);
            else traffic.Enqueue(r);
        }

        engine.Journal.Added += OnEvent;
        engine.Traffic.Recorded += OnTraffic;
        // Closes when the dashboard stops (restart, service stop): an open socket would otherwise hold the server's
        // shutdown for its full 30 s timeout. The page reconnects by itself.
        var stopping = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        var sendLock = new SemaphoreSlim(1, 1);

        async Task Send(object payload)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json.Options);
            await sendLock.WaitAsync(cts.Token);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token); }
            finally { sendLock.Release(); }
        }

        var sender = Task.Run(async () =>
        {
            var lastSnapshot = DateTime.MinValue;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            await Send(new { type = "snapshot", bridges = engine.Snapshot().Select(ApiEndpoints.Mask) });
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                var batch = new List<EngineEvent>();
                while (events.Reader.TryRead(out var e)) batch.Add(e);
                if (batch.Count > 0) await Send(new { type = "events", events = batch });

                if (!traffic.IsEmpty)
                {
                    var records = new List<TrafficRecord>();
                    var size = 0;
                    while (size < MaxTrafficBytesPerBatch && traffic.TryDequeue(out var r))
                    {
                        records.Add(r);
                        size += r.Data.Length;
                    }
                    var dropped = Interlocked.Exchange(ref trafficDropped, 0);
                    await Send(new { type = "traffic", records, dropped });
                }

                if (DateTime.UtcNow - lastSnapshot >= TimeSpan.FromSeconds(1))
                {
                    lastSnapshot = DateTime.UtcNow;
                    await Send(new { type = "snapshot", bridges = engine.Snapshot().Select(ApiEndpoints.Mask) });
                }
            }
        });

        try
        {
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (!result.EndOfMessage || result.Count == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                    var type = doc.RootElement.GetProperty("type").GetString();
                    var id = doc.RootElement.TryGetProperty("bridgeId", out var b) ? b.GetString() : null;
                    if (id is null) continue;
                    if (type == "subscribe") subscriptions[id] = 0;
                    else if (type == "unsubscribe") subscriptions.TryRemove(id, out _);
                }
                catch (JsonException) { /* ignore malformed */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            engine.Journal.Added -= OnEvent;
            engine.Traffic.Recorded -= OnTraffic;
            cts.Cancel();
            try { await sender; } catch { /* ignore */ }
            if (ws.State == WebSocketState.Open)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var status = stopping.IsCancellationRequested ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure;
                try { await ws.CloseAsync(status, null, closing.Token); } catch { /* ignore */ }
            }
        }
    }
}
