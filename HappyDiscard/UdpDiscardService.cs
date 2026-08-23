/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

using HappyDiscard.Events;
using JoyfulReaperLib.JRNet;
using JoyfulReaperLib.MissionControl;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization.Metadata;

namespace HappyDiscard;

public sealed class UdpDiscardService(
    ILogger<UdpDiscardService> logger,
    IMissionControlClient missionControlClient,
    IOptions<HappyDiscardOptions> options)
    : BackgroundService
{
    private const int MaximumUdpPayloadBytes = 65_507;
    private static readonly TimeSpan TelemetryPublishTimeout = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HappyDiscardOptions value = options.Value;

        if (!value.UdpEnabled)
        {
            logger.LogInformation("HappyDiscard UDP listener disabled.");
            return;
        }

        IPAddress listenAddress = IPAddressUtils.ParseListenAddress(
            string.IsNullOrWhiteSpace(value.UdpListenAddress)
                ? value.ListenAddress
                : value.UdpListenAddress);

        int port = value.UdpPort ?? value.Port;
        int maxDatagramBytes = Math.Clamp(
            value.MaxUdpDatagramBytes,
            1,
            MaximumUdpPayloadBytes);

        using UdpClient udp = CreateUdpClient(listenAddress, port);
        string listenEndpoint = udp.Client.LocalEndPoint!.ToString()!;
        var stopwatch = Stopwatch.StartNew();
        long datagramsReceived = 0;
        long datagramsDiscarded = 0;
        long datagramsDropped = 0;
        long bytesDiscarded = 0;

        logger.LogInformation(
            "HappyDiscard UDP listener started on {Endpoint}",
            udp.Client.LocalEndPoint);

        await PublishStartedAsync(listenEndpoint, maxDatagramBytes, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult received;

                try
                {
                    received = await udp.ReceiveAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException exception)
                {
                    logger.LogWarning(
                        exception,
                        "Socket error while receiving UDP Discard datagram.");

                    // UdpClient does not expose a remote endpoint when ReceiveAsync fails.
                    continue;
                }
                catch (ObjectDisposedException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                datagramsReceived++;

                if (received.Buffer.Length > maxDatagramBytes)
                {
                    datagramsDropped++;
                    logger.LogWarning(
                        "Dropped oversized UDP Discard datagram from {Remote}: {Bytes} bytes.",
                        received.RemoteEndPoint,
                        received.Buffer.Length);

                    await PublishDroppedAsync(
                        received.RemoteEndPoint.ToString(),
                        received.Buffer.Length,
                        "oversized",
                        stoppingToken);

                    continue;
                }

                datagramsDiscarded++;
                bytesDiscarded += received.Buffer.Length;
                logger.LogDebug(
                    "Discarded UDP datagram from {Remote}: {Bytes} bytes.",
                    received.RemoteEndPoint,
                    received.Buffer.Length);

                await PublishDiscardedAsync(
                    received.RemoteEndPoint.ToString(),
                    received.Buffer.Length,
                    stoppingToken);
            }
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation("HappyDiscard UDP listener stopped.");
            await PublishStoppedAsync(
                listenEndpoint,
                datagramsReceived,
                datagramsDiscarded,
                datagramsDropped,
                bytesDiscarded,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private Task PublishStartedAsync(
        string listenEndpoint,
        int maxDatagramBytes,
        CancellationToken cancellationToken) =>
        PublishTelemetrySafelyAsync(
            HappyDiscardEventTypes.UdpStarted,
            new UdpDiscardStartedEvent(listenEndpoint, maxDatagramBytes),
            HappyDiscardJsonContext.Default.UdpDiscardStartedEvent,
            cancellationToken);

    private Task PublishDiscardedAsync(
        string remote,
        int bytesDiscarded,
        CancellationToken cancellationToken) =>
        PublishTelemetrySafelyAsync(
            HappyDiscardEventTypes.UdpDatagramDiscarded,
            new UdpDatagramDiscardedEvent(remote, bytesDiscarded),
            HappyDiscardJsonContext.Default.UdpDatagramDiscardedEvent,
            cancellationToken);

    private Task PublishDroppedAsync(
        string remote,
        int bytesReceived,
        string reason,
        CancellationToken cancellationToken) =>
        PublishTelemetrySafelyAsync(
            HappyDiscardEventTypes.UdpDatagramDropped,
            new UdpDatagramDroppedEvent(remote, bytesReceived, reason),
            HappyDiscardJsonContext.Default.UdpDatagramDroppedEvent,
            cancellationToken);

    private Task PublishStoppedAsync(
        string listenEndpoint,
        long datagramsReceived,
        long datagramsDiscarded,
        long datagramsDropped,
        long bytesDiscarded,
        long durationMilliseconds) =>
        PublishTelemetrySafelyAsync(
            HappyDiscardEventTypes.UdpStopped,
            new UdpDiscardStoppedEvent(
                listenEndpoint,
                datagramsReceived,
                datagramsDiscarded,
                datagramsDropped,
                bytesDiscarded,
                durationMilliseconds),
            HappyDiscardJsonContext.Default.UdpDiscardStoppedEvent,
            CancellationToken.None);

    private async Task PublishTelemetrySafelyAsync<TPayload>(
        string eventType,
        TPayload payload,
        JsonTypeInfo<TPayload> payloadTypeInfo,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TelemetryPublishTimeout);

        try
        {
            bool published = await missionControlClient.TryPublishAsync(
                eventType,
                payload,
                payloadTypeInfo,
                DateTimeOffset.UtcNow,
                correlationId: null,
                timeout.Token);

            if (!published)
            {
                logger.LogWarning("Mission Control did not accept {EventType}.", eventType);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Mission Control publication for {EventType} was cancelled during shutdown.", eventType);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Mission Control publication for {EventType} timed out.", eventType);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to publish Mission Control event {EventType}.", eventType);
        }
    }

    private static UdpClient CreateUdpClient(IPAddress address, int port)
    {
        var udp = new UdpClient(address.AddressFamily);

        if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
            address.Equals(IPAddress.IPv6Any))
        {
            udp.Client.DualMode = true;
        }

        udp.Client.Bind(new IPEndPoint(address, port));

        return udp;
    }
}
