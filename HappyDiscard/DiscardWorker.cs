/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

using HappyDiscard.Events;
using JoyfulReaperLib.JRNet;
using JoyfulReaperLib.MissionControl;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
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
            TcpClient client = await _listener.AcceptTcpClientAsync(stoppingToken);
            while (!_stopRequested && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (stoppingToken.IsCancellationRequested || _stopRequested)
                {
                    break;
                }
                try
                {
                    await _connectionLimit.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    client.Dispose();
                    break;
                }
            }

            long connectionId = Interlocked.Increment(ref _nextConnectionId);
            Task task = HandleClientAsync(connectionId, client, stoppingToken);
            _activeConnections[connectionId] = task;

            _ = task.ContinueWith(ct =>
            {
                _activeConnections.TryRemove(connectionId, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
                    telemetry = await ProcessDiscardProtocolAsync(
                        stream,
                        remote,
                        isIgnoredTelemetrySource,
                        correlationId,
                        stoppingToken);

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
        if (!result.ShouldPublish)
        {
            return;
        }

        if (result.ShouldPublish)
        {
            logger.LogDebug(
                "Skipping telemetry for health-check connection {ConnectionId} from {Remote}.",
                connectionId,
                result.Remote);

            return;
        }

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


    private async Task<DiscardSessionTelemetryResult?> ProcessDiscardProtocolAsync(
        Stream stream,
        EndPoint? remote,
        bool isIgnoredTelemetrySource,
        string correlationId,
        CancellationToken stoppingToken)
    {
        string remoteString = remote?.ToString() ?? "unknown";
        Stopwatch stopwatch = Stopwatch.StartNew();
        var state = new DiscardSessionState();

        if (isIgnoredTelemetrySource)
        {
            logger.LogDebug(
                "Skipping telemetry for monitoring connection from {Remote}.",
                remote);
        }

        string outcome = "failed";
        bool succeeded = false;

        try
        {
            await DiscardAsync(stream,
                options.Value.RequestTimeoutSeconds,
                options.Value.MaxBytesPerConnection,
                state,
                stoppingToken);

            outcome = state.ByteLimitReached ? "byte-limit-reached" : "client-disconnected";
            succeeded = true;
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            outcome = "server-shutdown";
            logger.LogDebug(
                "Discard session from {Remote} was cancelled during shutdown.",
                remote);
        }
        catch (OperationCanceledException)
        {
            outcome = "timeout";
            logger.LogDebug(
                "Discard session from {Remote} timed out.",
                remote);
        }
        catch (IOException exception)
        {
            outcome = "io-error";
            logger.LogDebug(
                exception,
                "Discard session from {Remote} ended early.",
                remote);
        }
        catch (SocketException exception)
        {
            outcome = "socket-error";
            logger.LogDebug(
                exception,
                "Socket error during Discard session from {Remote}.",
                remote);
        }
        catch (Exception exception)
        {
            outcome = "failed";
            logger.LogError(
                exception,
                "Unhandled error during Discard session from {Remote}.",
                remote);
        }
        finally
        {
            stopwatch.Stop();
        }

        if (isIgnoredTelemetrySource)
            return null;

        return new DiscardSessionTelemetryResult(
            remoteString,
            state.BytesDiscarded,
            stopwatch.ElapsedMilliseconds,
            outcome,
            succeeded,
            !isIgnoredTelemetrySource,
            OccurredAt: DateTimeOffset.UtcNow,
            correlationId);
    }

    private static async Task DiscardAsync(
        Stream stream,
        int RequestTimeoutSeconds,
        long maxBytesPerConnection,
        DiscardSessionState state,
        CancellationToken stoppingToken)
    {
        const int BUFFER_SIZE = 4096;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);

        // We dont want to keep discarding data forever so we set a timeout
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));

        try
        {
            while (state.BytesDiscarded < maxBytesPerConnection)
            {
                long remaining = maxBytesPerConnection - state.BytesDiscarded;
                int readSize = (int)Math.Min(BUFFER_SIZE, remaining);

                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, readSize), timeout.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                state.BytesDiscarded += bytesRead;
            }

            state.ByteLimitReached = state.BytesDiscarded >= maxBytesPerConnection;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

    internal static TimeSpan GetRequestTimeout(
        HappyDiscardOptions options) =>
            TimeSpan.FromSeconds(options.RequestTimeoutSeconds);


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
        bool ShouldPublish,
        DateTimeOffset OccurredAt,
        string CorrelationId);
}
