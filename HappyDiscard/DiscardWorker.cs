/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

using HappyDiscard.Events;
using JoyfulReaperLib.JRNet;
using JoyfulReaperLib.MissionControl;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace HappyDiscard;

public class DiscardWorker(
    ILogger<DiscardWorker> logger,
    IMissionControlClient missionControlClient,
    IOptions<HappyDiscardOptions> options) : BackgroundService
{
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<long, Task> _activeConnections = new();
    private volatile bool _stopRequested;
    private readonly SemaphoreSlim _connectionLimit = new(
        options.Value.MaxConcurrentConnections,
        options.Value.MaxConcurrentConnections
    );
    private long _nextConnectionId;
    private IPAddress? _localBoundAddress;

    private static readonly TimeSpan TelemetryPublishTimeout =
        TimeSpan.FromSeconds(2); // TODO: make configurable

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _localBoundAddress = IPAddressUtils.ParseListenAddress(options.Value.ListenAddress);
        _listener = new TcpListener(_localBoundAddress, options.Value.Port);
        _listener.Start();

        logger.LogInformation("HappyDiscard service started on {IPAddress}:{Port}", _localBoundAddress, options.Value.Port);

        var occurredAt = DateTimeOffset.UtcNow;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TelemetryPublishTimeout);

            bool published = await missionControlClient.TryPublishAsync(
                eventType: HappyDiscardEventTypes.ServiceStarted,
                payload: new DiscardServiceStartedEvent(
                    $"{_localBoundAddress}:{options.Value.Port}"),
                payloadTypeInfo: HappyDiscardJsonContext.Default.DiscardServiceStartedEvent,
                occurredAt: occurredAt,
                correlationId: null,
                cancellationToken: timeout.Token);

            if (!published)
            {
                logger.LogWarning(
                    "Mission Control did not accept {EventType}",
                    HappyDiscardEventTypes.ServiceStarted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "Mission Control event publication for Discard Service Started was cancelled during shutdown.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Mission Control event publication for Discard Service Started timed out.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to publish Mission Control event for Discard Service Started");
        }

        try
        {
            while (!_stopRequested && !stoppingToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken);
                    await _connectionLimit.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested || _stopRequested)
                {
                    client?.Dispose();
                    break;
                }
                catch (SocketException)
                    when (stoppingToken.IsCancellationRequested || _stopRequested)
                {
                    client?.Dispose();
                    break;
                }
                catch (ObjectDisposedException)
                    when (stoppingToken.IsCancellationRequested || _stopRequested)
                {
                    client?.Dispose();
                    break;
                }

                long connectionId = Interlocked.Increment(ref _nextConnectionId);
                Task task = HandleClientAsync(connectionId, client, stoppingToken);
                _activeConnections[connectionId] = task;

                _ = task.ContinueWith(ct =>
                {
                    _activeConnections.TryRemove(connectionId, out _);

                    if (ct.IsFaulted)
                    {
                        logger.LogError(
                            ct.Exception,
                            "Connection {ConnectionId} handler failed unexpectedly.",
                            connectionId);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            }
        }
        finally
        {
            _listener.Stop();
            Task[] remaining = _activeConnections.Values.ToArray();
            if (remaining.Length > 0)
            {
                try
                {
                    await Task.WhenAll(remaining);
                }
                catch
                {
                    // Normal Shutdown
                }
            }
        }
    }

    private async Task HandleClientAsync(long connectionId, TcpClient client, CancellationToken stoppingToken)
    {
        Task? startedTelemetryTask = null;
        DiscardSessionTelemetryResult? telemetry = null;

        try
        {
            using (client)
            {
                client.NoDelay = true;
                EndPoint? remote = client.Client.RemoteEndPoint;

                bool isIgnoredTelemetrySource = IsIgnoredTelemetrySource(remote);
                string correlationId = Guid.NewGuid().ToString("N");

                if (!isIgnoredTelemetrySource)
                {
                    startedTelemetryTask = PublishDiscardStartedAsync(
                        remote?.ToString() ?? "unknown",
                        DateTimeOffset.UtcNow,
                        correlationId,
                        stoppingToken);
                }

                try
                {
                    logger.LogDebug("Received request: request from {Remote}.", client.Client.RemoteEndPoint);
                    await using NetworkStream stream = client.GetStream();
                    DiscardProtocolResult result =
                        await DiscardConnectionHandler.ProcessAsync(
                            stream,
                            remote,
                            options.Value,
                            logger,
                            stoppingToken);

                    if (!isIgnoredTelemetrySource)
                    {
                        telemetry =
                            new DiscardSessionTelemetryResult(
                                Remote: remote?.ToString() ?? "unknown",
                                BytesDiscarded: result.BytesDiscarded,
                                DurationMilliseconds:
                                result.DurationMilliseconds,
                                Outcome: result.Outcome,
                                Succeeded: result.Succeeded,
                                OccurredAt: DateTimeOffset.UtcNow,
                                CorrelationId: correlationId);
                    }

                }
                catch (OperationCanceledException)
                {
                    logger.LogDebug(
                        "Connection {ConnectionId} from {Remote} timed out.",
                        connectionId,
                        remote);
                }
                catch (InvalidDataException exception)
                {
                    logger.LogInformation(
                        exception,
                        "Rejected malformed request on connection {ConnectionId} from {Remote}.",
                        connectionId,
                        remote);
                }
                catch (IOException exception)
                {
                    logger.LogDebug(
                        exception,
                        "Connection {ConnectionId} from {Remote} ended early.",
                        connectionId,
                        remote);
                }
                catch (SocketException exception)
                {
                    logger.LogDebug(
                        exception,
                        "Socket error on connection {ConnectionId} from {Remote}.",
                        connectionId,
                        remote);
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Unhandled error on connection {ConnectionId} from {Remote}.",
                        connectionId,
                        remote);
                }
            }
            if (startedTelemetryTask is not null)
            {
                try
                {
                    await startedTelemetryTask;
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug(
                        "Discard-started telemetry was cancelled during shutdown.");
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning(
                        "Discard-started telemetry timed out.");
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to publish discard-started telemetry.");
                }
            }
        }
        finally
        {
            _connectionLimit.Release();
        }

        if (telemetry is not null)
        {
            await PublishTelemetryAsync(connectionId, telemetry, stoppingToken);
        }
    }

    private async Task PublishTelemetryAsync(
    long connectionId,
    DiscardSessionTelemetryResult result,
    CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            bool published = await missionControlClient.TryPublishAsync(
                eventType: HappyDiscardEventTypes.DiscardStopped,
                payload: new DiscardStoppedEvent(
                    Remote: result.Remote,
                    BytesDiscarded: result.BytesDiscarded,
                    DurationMilliseconds: result.DurationMilliseconds,
                    Outcome: result.Outcome,
                    Succeeded: result.Succeeded),
                payloadTypeInfo: HappyDiscardJsonContext.Default.DiscardStoppedEvent,
                occurredAt: result.OccurredAt,
                correlationId: result.CorrelationId,
                cancellationToken: timeout.Token);

            if (!published)
            {
                logger.LogWarning(
                    "Mission Control did not accept {EventType} for connection {ConnectionId}.",
                    HappyDiscardEventTypes.DiscardStopped,
                    connectionId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "Mission Control event publication for connection {ConnectionId} was cancelled during shutdown.",
                connectionId);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Mission Control event publication for connection {ConnectionId} timed out.",
                connectionId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to publish Mission Control event for connection {ConnectionId}.",
                connectionId);
        }
    }

    private async Task PublishDiscardStartedAsync(
        string remote,
        DateTimeOffset occurredAt,
        string correlationId,
        CancellationToken stoppingToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TelemetryPublishTimeout);

        bool published = await missionControlClient.TryPublishAsync(
                eventType: HappyDiscardEventTypes.DiscardStarted,
                payload: new DiscardStartedEvent(
                    remote,
                    options.Value.RequestTimeoutSeconds,
                    options.Value.MaxBytesPerConnection),
                payloadTypeInfo: HappyDiscardJsonContext.Default.DiscardStartedEvent,
                occurredAt,
                correlationId,
                timeout.Token);

        if (!published)
        {
            logger.LogWarning(
                "Mission Control did not accept {EventType}",
                HappyDiscardEventTypes.DiscardStarted);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Happy Discard Service Stopping...");
        _stopRequested = true;
        _listener?.Stop();

        return base.StopAsync(cancellationToken);
    }

    internal static TimeSpan GetRequestTimeout(HappyDiscardOptions options) =>
        DiscardConnectionHandler.GetRequestTimeout(options);


    private bool IsIgnoredTelemetrySource(
        EndPoint? remote)
    {
        var remoteAddress = (remote as IPEndPoint)?
            .Address
            .MapToIPv4()
            .ToString();

        return
            !string.IsNullOrWhiteSpace(
                options.Value.TelemetryIgnoredRemoteAddress) &&
            string.Equals(
                remoteAddress,
                options.Value.TelemetryIgnoredRemoteAddress,
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DiscardSessionTelemetryResult(
        string Remote,
        long BytesDiscarded,
        long DurationMilliseconds,
        string Outcome,
        bool Succeeded,
        DateTimeOffset OccurredAt,
        string CorrelationId);
}
