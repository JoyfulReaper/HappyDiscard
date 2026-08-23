/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

using JoyfulReaperLib.JRNet;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

namespace HappyDiscard;

public sealed class UdpDiscardService(
    ILogger<UdpDiscardService> logger,
    IOptions<HappyDiscardOptions> options)
    : BackgroundService
{
    private const int MaximumUdpPayloadBytes = 65_507;

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

        logger.LogInformation(
            "HappyDiscard UDP listener started on {Endpoint}",
            udp.Client.LocalEndPoint);

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

                continue;
            }
            catch (ObjectDisposedException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (received.Buffer.Length > maxDatagramBytes)
            {
                logger.LogWarning(
                    "Dropped oversized UDP Discard datagram from {Remote}: {Bytes} bytes.",
                    received.RemoteEndPoint,
                    received.Buffer.Length);

                continue;
            }

            logger.LogDebug(
                "Discarded UDP datagram from {Remote}: {Bytes} bytes.",
                received.RemoteEndPoint,
                received.Buffer.Length);
        }

        logger.LogInformation("HappyDiscard UDP listener stopped.");
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