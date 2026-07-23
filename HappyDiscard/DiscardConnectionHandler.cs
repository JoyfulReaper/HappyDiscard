/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

using JoyfulReaperLib.TcpServer;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HappyDiscard;

/// <summary>
/// Processes Discard protocol connections.
/// </summary>
public sealed class DiscardConnectionHandler(
    ILogger<DiscardConnectionHandler> logger,
    IOptions<HappyDiscardOptions> options) : ITcpConnectionHandler
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(
        TcpConnectionContext context,
        CancellationToken cancellationToken)
    {
        _ = await ProcessAsync(
            context.Stream,
            context.RemoteEndPoint,
            options.Value,
            logger,
            cancellationToken);
    }

    internal static async ValueTask<DiscardProtocolResult>
        ProcessAsync(
            Stream stream,
            EndPoint? remote,
            HappyDiscardOptions options,
            ILogger logger,
            CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        var state = new DiscardSessionState();

        string outcome = "failed";
        bool succeeded = false;

        try
        {
            await DiscardAsync(
                stream,
                GetRequestTimeout(options),
                options.MaxBytesPerConnection,
                state,
                cancellationToken);

            outcome = state.ByteLimitReached
                ? "byte-limit-reached"
                : "client-disconnected";

            succeeded = true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
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

        return new DiscardProtocolResult(
            BytesDiscarded: state.BytesDiscarded,
            DurationMilliseconds: stopwatch.ElapsedMilliseconds,
            Outcome: outcome,
            Succeeded: succeeded);
    }

    private static async ValueTask DiscardAsync(
        Stream stream,
        TimeSpan requestTimeout,
        long maxBytesPerConnection,
        DiscardSessionState state,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 4096;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(requestTimeout);

        try
        {
            while (state.BytesDiscarded < maxBytesPerConnection)
            {
                long remaining = maxBytesPerConnection - state.BytesDiscarded;
                int readSize = (int)Math.Min(BufferSize, remaining);
                int bytesRead = await stream.ReadAsync(
                        buffer.AsMemory(0, readSize),
                        timeout.Token);

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
            ArrayPool<byte>.Shared.Return(
                buffer);
        }
    }

    internal static TimeSpan GetRequestTimeout(
        HappyDiscardOptions options) => TimeSpan.FromSeconds(
            options.RequestTimeoutSeconds);
}

internal sealed record DiscardProtocolResult(
    long BytesDiscarded,
    long DurationMilliseconds,
    string Outcome,
    bool Succeeded);