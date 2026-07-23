/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

using HappyDiscard.Events;
using JoyfulReaperLib.JRNet;
using JoyfulReaperLib.MissionControl;
using Microsoft.Extensions.Options;

namespace HappyDiscard;

/// <summary>
/// Handles application-level lifecycle telemetry for HappyDiscard.
/// </summary>
public sealed class DiscardLifecycleService(
    ILogger<DiscardLifecycleService> logger,
    IMissionControlClient missionControlClient,
    IOptions<HappyDiscardOptions> options) : IHostedLifecycleService
{
    private static readonly TimeSpan TelemetryPublishTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var listenAddress =
            IPAddressUtils.ParseListenAddress(options.Value.ListenAddress);

        logger.LogInformation(
            "HappyDiscard service listening on {IPAddress}:{Port}.",
            listenAddress,
            options.Value.Port);

        DateTimeOffset occurredAt = DateTimeOffset.UtcNow;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TelemetryPublishTimeout);

        try
        {
            bool published = await missionControlClient.TryPublishAsync(
                eventType: HappyDiscardEventTypes.ServiceStarted,
                payload: new DiscardServiceStartedEvent($"{listenAddress}:{options.Value.Port}"),
                payloadTypeInfo: HappyDiscardJsonContext.Default.DiscardServiceStartedEvent,
                occurredAt: occurredAt,
                correlationId: null,
                cancellationToken: timeout.Token);

            if (!published)
            {
                logger.LogWarning(
                    "Mission Control did not accept {EventType}.",
                    HappyDiscardEventTypes.ServiceStarted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "Mission Control publication for Discard Service Started was cancelled during shutdown.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Mission Control publication for Discard Service Started timed out.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to publish Mission Control event for Discard Service Started.");
        }
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("HappyDiscard service stopping...");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
