/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

using HappyDiscard.Events;
using JoyfulReaperLib.MissionControl;
using JoyfulReaperLib.TcpServer;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HappyDiscard;

/// <summary>
/// Processes Discard protocol connections and publishes
/// connection-level telemetry.
/// </summary>
public sealed class DiscardConnectionHandler(
    ILogger<DiscardConnectionHandler> logger,
    IMissionControlClient missionControlClient,
    IOptions<HappyDiscardOptions> options) : ITcpConnectionHandler
{
    private static readonly TimeSpan TelemetryPublishTimeout =
        TimeSpan.FromSeconds(2); // TODO: make configurable

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        TcpConnectionContext context,
        CancellationToken cancellationToken)
    {
        EndPoint? remote = context.RemoteEndPoint;

        if (IsIgnoredTelemetrySource(remote))
        {
            logger.LogDebug(
                "Skipping telemetry for monitoring connection from {Remote}.",
                remote);

            _ = await ProcessAsync(
                context.Stream,
                remote,
                options.Value,
                logger,
                cancellationToken);

            return;
        }

        string remoteString = remote?.ToString() ?? "unknown";
        string correlationId = Guid.NewGuid().ToString("N");

        Task startedTelemetryTask =
            PublishDiscardStartedAsync(
                remoteString,
                DateTimeOffset.UtcNow,
                correlationId,
                cancellationToken);

        DiscardProtocolResult protocolResult =
            await ProcessAsync(
                context.Stream,
                remote,
                options.Value,
                logger,
                cancellationToken);

        var telemetryResult =
            new DiscardSessionTelemetryResult(
                Remote:
                    remoteString,
                BytesDiscarded:
                    protocolResult.BytesDiscarded,
                DurationMilliseconds:
                    protocolResult.DurationMilliseconds,
                Outcome:
                    protocolResult.Outcome,
                Succeeded:
                    protocolResult.Succeeded,
                OccurredAt:
                    DateTimeOffset.UtcNow,
                CorrelationId:
                    correlationId);

        long connectionId =
            context.ConnectionId;

        context.RegisterAfterClose(
            afterCloseToken =>
                CompleteTelemetryAsync(
                    connectionId,
                    startedTelemetryTask,
                    telemetryResult,
                    afterCloseToken));
    }

    private async ValueTask CompleteTelemetryAsync(
        long connectionId,
        Task startedTelemetryTask,
        DiscardSessionTelemetryResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await startedTelemetryTask;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "Discard-started telemetry for connection {ConnectionId} was cancelled during shutdown.",
                connectionId);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Discard-started telemetry for connection {ConnectionId} timed out.",
                connectionId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to publish discard-started telemetry for connection {ConnectionId}.",
                connectionId);
        }

        await PublishDiscardStoppedAsync(
            connectionId,
            result,
            cancellationToken);
    }

    private async Task PublishDiscardStartedAsync(
        string remote,
        DateTimeOffset occurredAt,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            TelemetryPublishTimeout);

        bool published =
            await missionControlClient.TryPublishAsync(
                eventType:
                    HappyDiscardEventTypes.DiscardStarted,
                payload:
                    new DiscardStartedEvent(
                        Remote:
                            remote,
                        RequestTimeoutSeconds:
                            options.Value
                                .RequestTimeoutSeconds,
                        MaxBytesPerConnection:
                            options.Value
                                .MaxBytesPerConnection),
                payloadTypeInfo:
                    HappyDiscardJsonContext
                        .Default
                        .DiscardStartedEvent,
                occurredAt:
                    occurredAt,
                correlationId:
                    correlationId,
                cancellationToken:
                    timeout.Token);

        if (!published)
        {
            logger.LogWarning(
                "Mission Control did not accept {EventType}.",
                HappyDiscardEventTypes.DiscardStarted);
        }
    }

    private async ValueTask PublishDiscardStoppedAsync(
        long connectionId,
        DiscardSessionTelemetryResult result,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            TelemetryPublishTimeout);

        try
        {
            bool published =
                await missionControlClient.TryPublishAsync(
                    eventType:
                        HappyDiscardEventTypes
                            .DiscardStopped,
                    payload:
                        new DiscardStoppedEvent(
                            Remote:
                                result.Remote,
                            BytesDiscarded:
                                result.BytesDiscarded,
                            DurationMilliseconds:
                                result.DurationMilliseconds,
                            Outcome:
                                result.Outcome,
                            Succeeded:
                                result.Succeeded),
                    payloadTypeInfo:
                        HappyDiscardJsonContext
                            .Default
                            .DiscardStoppedEvent,
                    occurredAt:
                        result.OccurredAt,
                    correlationId:
                        result.CorrelationId,
                    cancellationToken:
                        timeout.Token);

            if (!published)
            {
                logger.LogWarning(
                    "Mission Control did not accept {EventType} for connection {ConnectionId}.",
                    HappyDiscardEventTypes
                        .DiscardStopped,
                    connectionId);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
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

    private bool IsIgnoredTelemetrySource(
        EndPoint? remote)
    {
        string? remoteAddress =
            (remote as IPEndPoint)?
                .Address
                .MapToIPv4()
                .ToString();

        return
            !string.IsNullOrWhiteSpace(
                options.Value
                    .TelemetryIgnoredRemoteAddress) &&
            string.Equals(
                remoteAddress,
                options.Value
                    .TelemetryIgnoredRemoteAddress,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static async ValueTask<DiscardProtocolResult>
        ProcessAsync(
            Stream stream,
            EndPoint? remote,
            HappyDiscardOptions options,
            ILogger logger,
            CancellationToken cancellationToken)
    {
        Stopwatch stopwatch =
            Stopwatch.StartNew();

        var state =
            new DiscardSessionState();

        string outcome =
            "failed";

        bool succeeded =
            false;

        try
        {
            await DiscardAsync(
                stream,
                GetRequestTimeout(options),
                options.MaxBytesPerConnection,
                state,
                cancellationToken);

            outcome =
                state.ByteLimitReached
                    ? "byte-limit-reached"
                    : "client-disconnected";

            succeeded =
                true;
        }
        catch (OperationCanceledException)
            when (cancellationToken
                .IsCancellationRequested)
        {
            outcome =
                "server-shutdown";

            logger.LogDebug(
                "Discard session from {Remote} was cancelled during shutdown.",
                remote);
        }
        catch (OperationCanceledException)
        {
            outcome =
                "timeout";

            logger.LogDebug(
                "Discard session from {Remote} timed out.",
                remote);
        }
        catch (IOException exception)
        {
            outcome =
                "io-error";

            logger.LogDebug(
                exception,
                "Discard session from {Remote} ended early.",
                remote);
        }
        catch (SocketException exception)
        {
            outcome =
                "socket-error";

            logger.LogDebug(
                exception,
                "Socket error during Discard session from {Remote}.",
                remote);
        }
        catch (Exception exception)
        {
            outcome =
                "failed";

            logger.LogError(
                exception,
                "Unhandled error during Discard session from {Remote}.",
                remote);
        }
        finally
        {
            stopwatch.Stop();
        }

        return new DiscardProtocolResult(
            BytesDiscarded:
                state.BytesDiscarded,
            DurationMilliseconds:
                stopwatch.ElapsedMilliseconds,
            Outcome:
                outcome,
            Succeeded:
                succeeded);
    }

    private static async ValueTask DiscardAsync(
        Stream stream,
        TimeSpan requestTimeout,
        long maxBytesPerConnection,
        DiscardSessionState state,
        CancellationToken cancellationToken)
    {
        const int BufferSize =
            4096;

        byte[] buffer =
            ArrayPool<byte>.Shared.Rent(
                BufferSize);

        using var timeout =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        timeout.CancelAfter(
            requestTimeout);

        try
        {
            while (state.BytesDiscarded <
                   maxBytesPerConnection)
            {
                long remaining =
                    maxBytesPerConnection -
                    state.BytesDiscarded;

                int readSize =
                    (int)Math.Min(
                        BufferSize,
                        remaining);

                int bytesRead =
                    await stream.ReadAsync(
                        buffer.AsMemory(
                            0,
                            readSize),
                        timeout.Token);

                if (bytesRead == 0)
                {
                    break;
                }

                state.BytesDiscarded +=
                    bytesRead;
            }

            state.ByteLimitReached =
                state.BytesDiscarded >=
                maxBytesPerConnection;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(
                buffer);
        }
    }

    internal static TimeSpan GetRequestTimeout(
        HappyDiscardOptions options) =>
        TimeSpan.FromSeconds(
            options.RequestTimeoutSeconds);
}

internal sealed record DiscardProtocolResult(
    long BytesDiscarded,
    long DurationMilliseconds,
    string Outcome,
    bool Succeeded);

internal sealed record DiscardSessionTelemetryResult(
    string Remote,
    long BytesDiscarded,
    long DurationMilliseconds,
    string Outcome,
    bool Succeeded,
    DateTimeOffset OccurredAt,
    string CorrelationId);