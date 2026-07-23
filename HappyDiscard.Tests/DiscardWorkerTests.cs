using HappyDiscard.Events;
using JoyfulReaperLib.MissionControl;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace HappyDiscard.Tests;

public sealed class DiscardWorkerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartAsync_PublishesOneServiceStartedEvent()
    {
        int port = GetFreeLoopbackPort();
        var missionControl = new FakeMissionControlClient();

        await using var server = await DiscardServer.StartAsync(missionControl, new()
        {
            ListenAddress = IPAddress.Loopback.ToString(),
            Port = port
        });

        Publication publication = await missionControl.WaitForSuccessfulAsync(
            HappyDiscardEventTypes.ServiceStarted,
            WaitTimeout);

        DiscardServiceStartedEvent payload =
            Assert.IsType<DiscardServiceStartedEvent>(publication.Payload);
        Assert.Equal($"{IPAddress.Loopback}:{port}", payload.ListenAddress);
        Assert.Null(publication.CorrelationId);
        Assert.Equal(typeof(DiscardServiceStartedEvent), publication.DeclaredPayloadType);
        Assert.Single(missionControl.SuccessfulPublications);
    }

    [Fact]
    public async Task StartAsync_WhenStartupTelemetryThrows_ServerContinuesServing()
    {
        var missionControl = new FakeMissionControlClient();
        missionControl.ThrowOn(HappyDiscardEventTypes.ServiceStarted);

        await using var server = await DiscardServer.StartAsync(missionControl);

        await SendAndWaitForCloseAsync(server.Port, [1, 2, 3]);

        Publication stopped = await missionControl.WaitForSuccessfulAsync(
            HappyDiscardEventTypes.DiscardStopped,
            WaitTimeout);
        Assert.Equal(3, Assert.IsType<DiscardStoppedEvent>(stopped.Payload).BytesDiscarded);
    }

    [Fact]
    public async Task ClientDisconnect_PublishesStartedAndStoppedSessionTelemetry()
    {
        var missionControl = new FakeMissionControlClient();
        var options = new HappyDiscardOptions
        {
            RequestTimeoutSeconds = 3,
            MaxBytesPerConnection = 128
        };
        await using var server = await DiscardServer.StartAsync(missionControl, options);
        byte[] bytes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

        await SendAndWaitForCloseAsync(server.Port, bytes);

        Publication started = await missionControl.WaitForSuccessfulAsync(
            HappyDiscardEventTypes.DiscardStarted,
            WaitTimeout);
        Publication stopped = await missionControl.WaitForSuccessfulAsync(
            HappyDiscardEventTypes.DiscardStopped,
            WaitTimeout);

        DiscardStartedEvent startedPayload =
            Assert.IsType<DiscardStartedEvent>(started.Payload);
        Assert.StartsWith($"{IPAddress.Loopback}:", startedPayload.Remote);
        Assert.Equal(options.RequestTimeoutSeconds, startedPayload.RequestTimeoutSeconds);
        Assert.Equal(options.MaxBytesPerConnection, startedPayload.MaxBytesPerConnection);
        Assert.Equal(typeof(DiscardStartedEvent), started.DeclaredPayloadType);

        DiscardStoppedEvent payload = Assert.IsType<DiscardStoppedEvent>(stopped.Payload);
        Assert.Equal(bytes.Length, payload.BytesDiscarded);
        Assert.Equal("client-disconnected", payload.Outcome);
        Assert.True(payload.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(started.CorrelationId));
        Assert.Equal(started.CorrelationId, stopped.CorrelationId);
    }

    [Fact]
    public async Task ByteLimit_StopsAtConfiguredMaximumAndClosesConnection()
    {
        var missionControl = new FakeMissionControlClient();
        await using var server = await DiscardServer.StartAsync(missionControl, new()
        {
            MaxBytesPerConnection = 4
        });

        using var client = await ConnectAsync(server.Port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).AsTask().WaitAsync(WaitTimeout);
        bool connectionClosed = await ReadShowsConnectionClosedAsync(stream);

        Publication stopped = await missionControl.WaitForSuccessfulAsync(
            HappyDiscardEventTypes.DiscardStopped,
            WaitTimeout);
        DiscardStoppedEvent payload = Assert.IsType<DiscardStoppedEvent>(stopped.Payload);

        Assert.True(connectionClosed);
        Assert.Equal(4, payload.BytesDiscarded);
        Assert.Equal("byte-limit-reached", payload.Outcome);
        Assert.True(payload.Succeeded);
    }

    [Fact]
    public async Task Timeout_WhenClientSendsNoData_PublishesTimeoutOutcome()
    {
        var missionControl = new FakeMissionControlClient();
        await using var server = await DiscardServer.StartAsync(missionControl, new()
        {
            RequestTimeoutSeconds = 1
        });

        using var client = await ConnectAsync(server.Port);

        Publication stopped = await missionControl.WaitForSuccessfulAsync(
            HappyDiscardEventTypes.DiscardStopped,
            TimeSpan.FromSeconds(4));

        DiscardStoppedEvent payload = Assert.IsType<DiscardStoppedEvent>(stopped.Payload);
        Assert.Equal("timeout", payload.Outcome);
        Assert.False(payload.Succeeded);
        Assert.True(
            await ReadShowsConnectionClosedAsync(
                client.GetStream()));
    }

    [Fact]
    public async Task IgnoredSource_ProcessesProtocolWithoutSessionTelemetry()
    {
        var missionControl = new FakeMissionControlClient();
        await using var server = await DiscardServer.StartAsync(missionControl, new()
        {
            TelemetryIgnoredRemoteAddress = IPAddress.Loopback.ToString()
        });

        await SendAndWaitForCloseAsync(server.Port, [42, 43, 44]);

        Assert.Single(
            missionControl.AttemptedPublications,
            publication => publication.EventType == HappyDiscardEventTypes.ServiceStarted);
        Assert.DoesNotContain(
            missionControl.AttemptedPublications,
            publication => publication.EventType is
                HappyDiscardEventTypes.DiscardStarted or
                HappyDiscardEventTypes.DiscardStopped);
    }

    [Fact]
    public async Task BlockedStoppedTelemetry_DoesNotHoldConnectionSlotOrClientOpen()
    {
        var missionControl = new FakeMissionControlClient();
        missionControl.BlockDiscardStoppedTelemetry();
        await using var server = await DiscardServer.StartAsync(missionControl, new()
        {
            MaxConcurrentConnections = 1
        });

        try
        {
            await SendAndWaitForCloseAsync(server.Port, [1]);
            await missionControl.WaitForAttemptAsync(
                HappyDiscardEventTypes.DiscardStopped,
                WaitTimeout);

            await SendAndWaitForCloseAsync(server.Port, [2]);

            await missionControl.WaitForAttemptsAsync(
                HappyDiscardEventTypes.DiscardStopped,
                expectedCount: 2,
                WaitTimeout);
        }
        finally
        {
            missionControl.ReleaseBlockedTelemetry();
        }
    }

    [Fact]
    public async Task StartedTelemetryFailure_DoesNotStopServer()
    {
        var missionControl = new FakeMissionControlClient();
        missionControl.ThrowOn(HappyDiscardEventTypes.DiscardStarted);
        await using var server = await DiscardServer.StartAsync(missionControl);

        await SendAndWaitForCloseAsync(server.Port, [1, 2]);
        await SendAndWaitForCloseAsync(server.Port, [3, 4, 5]);

        await missionControl.WaitForSuccessfulCountAsync(
            HappyDiscardEventTypes.DiscardStopped,
            expectedCount: 2,
            WaitTimeout);
    }

    [Fact]
    public async Task StoppedTelemetryFailure_DoesNotStopServerForSubsequentConnection()
    {
        var missionControl = new FakeMissionControlClient();
        missionControl.ThrowOn(HappyDiscardEventTypes.DiscardStopped, times: 1);
        await using var server = await DiscardServer.StartAsync(missionControl);

        await SendAndWaitForCloseAsync(server.Port, [1, 2]);
        await SendAndWaitForCloseAsync(server.Port, [3]);

        await missionControl.WaitForAttemptsAsync(
            HappyDiscardEventTypes.DiscardStopped,
            expectedCount: 2,
            WaitTimeout);
        await missionControl.WaitForSuccessfulCountAsync(
            HappyDiscardEventTypes.DiscardStopped,
            expectedCount: 1,
            WaitTimeout);
    }

    [Fact]
    public async Task TelemetryCancellation_DoesNotKillServer()
    {
        var missionControl = new FakeMissionControlClient();
        missionControl.Block(HappyDiscardEventTypes.DiscardStarted);
        await using var server = await DiscardServer.StartAsync(missionControl);

        await SendAndWaitForCloseAsync(server.Port, [1]);
        await missionControl.WaitForObservedCancellationAsync(WaitTimeout);

        missionControl.ReleaseBlockedTelemetry();
        await SendAndWaitForCloseAsync(server.Port, [2]);

        await missionControl.WaitForAttemptsAsync(
            HappyDiscardEventTypes.DiscardStopped,
            expectedCount: 2,
            WaitTimeout);
    }

    [Fact]
    public async Task StopAsync_ReleasesListeningPortForImmediateRebind()
    {
        int port = GetFreeLoopbackPort();
        var missionControl = new FakeMissionControlClient();
        await using var server = await DiscardServer.StartAsync(missionControl, new()
        {
            Port = port
        });

        await server.StopAsync();

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
    }

    [Fact]
    public async Task StopAsync_CancelsActiveConnectionsWithoutHanging()
    {
        var missionControl = new FakeMissionControlClient();
        await using var server = await DiscardServer.StartAsync(missionControl);
        using var client = await ConnectAsync(server.Port);

        await server.StopAsync().WaitAsync(WaitTimeout);

        Publication stopped = await missionControl.WaitForAttemptAsync(
            HappyDiscardEventTypes.DiscardStopped,
            WaitTimeout);
        Assert.Equal(
            "server-shutdown",
            Assert.IsType<DiscardStoppedEvent>(stopped.Payload).Outcome);
    }

    [Fact]
    public void GetRequestTimeout_ConvertsConfiguredSecondsToTimeSpan()
    {
        var options = new HappyDiscardOptions
        {
            RequestTimeoutSeconds = 17
        };

        Assert.Equal(TimeSpan.FromSeconds(17), DiscardWorker.GetRequestTimeout(options));
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(WaitTimeout);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task SendAndWaitForCloseAsync(int port, byte[] bytes)
    {
        using TcpClient client = await ConnectAsync(port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(bytes).AsTask().WaitAsync(WaitTimeout);
        client.Client.Shutdown(SocketShutdown.Send);
        int bytesRead = await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(WaitTimeout);
        Assert.Equal(0, bytesRead);
    }

    private static async Task<bool> ReadShowsConnectionClosedAsync(NetworkStream stream)
    {
        try
        {
            int bytesRead = await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(WaitTimeout);
            return bytesRead == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class DiscardServer : IAsyncDisposable
    {
        private readonly DiscardWorker _worker;
        private bool _stopped;

        private DiscardServer(DiscardWorker worker, int port)
        {
            _worker = worker;
            Port = port;
        }

        public int Port { get; }

        public static async Task<DiscardServer> StartAsync(
            FakeMissionControlClient missionControl,
            HappyDiscardOptions? options = null)
        {
            options ??= new HappyDiscardOptions();
            if (options.Port == 9)
            {
                options.Port = GetFreeLoopbackPort();
            }

            options.ListenAddress = IPAddress.Loopback.ToString();

            var worker = new DiscardWorker(
                NullLogger<DiscardWorker>.Instance,
                missionControl,
                Options.Create(options));

            await worker.StartAsync(CancellationToken.None).WaitAsync(WaitTimeout);
            await missionControl.WaitForAttemptAsync(
                HappyDiscardEventTypes.ServiceStarted,
                WaitTimeout);

            return new DiscardServer(worker, options.Port);
        }

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            await _worker.StopAsync(CancellationToken.None).WaitAsync(WaitTimeout);
            _worker.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }

    private sealed class FakeMissionControlClient : IMissionControlClient
    {
        private readonly object _gate = new();
        private readonly List<Publication> _attempted = [];
        private readonly List<Publication> _successful = [];
        private readonly Dictionary<string, int> _throwsRemainingByEventType = [];
        private readonly HashSet<string> _blockedEventTypes = [];
        private TaskCompletionSource _nextPublicationChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _releaseBlockedTelemetry =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _observedCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Publication> AttemptedPublications
        {
            get
            {
                lock (_gate)
                {
                    return _attempted.ToArray();
                }
            }
        }

        public IReadOnlyList<Publication> SuccessfulPublications
        {
            get
            {
                lock (_gate)
                {
                    return _successful.ToArray();
                }
            }
        }

        public void ThrowOn(string eventType, int times = int.MaxValue)
        {
            lock (_gate)
            {
                _throwsRemainingByEventType[eventType] = times;
            }
        }

        public void Block(string eventType)
        {
            lock (_gate)
            {
                _blockedEventTypes.Add(eventType);
            }
        }

        public void BlockDiscardStoppedTelemetry() =>
            Block(HappyDiscardEventTypes.DiscardStopped);

        public void ReleaseBlockedTelemetry() =>
            _releaseBlockedTelemetry.TrySetResult();

        public async Task<bool> TryPublishAsync<TPayload>(
            string eventType,
            TPayload payload,
            JsonTypeInfo<TPayload> payloadTypeInfo,
            DateTimeOffset occurredAt,
            string? correlationId,
            CancellationToken cancellationToken)
        {
            var publication = new Publication(
                eventType,
                payload,
                payloadTypeInfo.Type,
                occurredAt,
                correlationId);

            AddAttempt(publication);

            if (ShouldThrow(eventType))
            {
                throw new InvalidOperationException($"Synthetic publication failure for {eventType}.");
            }

            if (ShouldBlock(eventType))
            {
                try
                {
                    await _releaseBlockedTelemetry.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _observedCancellation.TrySetResult();
                    throw;
                }
            }

            AddSuccess(publication);
            return true;
        }

        public Task<Publication> WaitForAttemptAsync(
            string eventType,
            TimeSpan timeout) =>
            WaitForAsync(
                () => AttemptedPublications.LastOrDefault(p => p.EventType == eventType),
                timeout);

        public Task WaitForAttemptsAsync(
            string eventType,
            int expectedCount,
            TimeSpan timeout) =>
            WaitForAsync(
                () => AttemptedPublications.Count(p => p.EventType == eventType) >= expectedCount,
                timeout);

        public Task<Publication> WaitForSuccessfulAsync(
            string eventType,
            TimeSpan timeout) =>
            WaitForAsync(
                () => SuccessfulPublications.LastOrDefault(p => p.EventType == eventType),
                timeout);

        public Task WaitForSuccessfulCountAsync(
            string eventType,
            int expectedCount,
            TimeSpan timeout) =>
            WaitForAsync(
                () => SuccessfulPublications.Count(p => p.EventType == eventType) >= expectedCount,
                timeout);

        public async Task WaitForObservedCancellationAsync(TimeSpan timeout)
        {
            await _observedCancellation.Task.WaitAsync(timeout);
        }

        private void AddAttempt(Publication publication)
        {
            lock (_gate)
            {
                _attempted.Add(publication);
                SignalChanged();
            }
        }

        private void AddSuccess(Publication publication)
        {
            lock (_gate)
            {
                _successful.Add(publication);
                SignalChanged();
            }
        }

        private bool ShouldThrow(string eventType)
        {
            lock (_gate)
            {
                if (!_throwsRemainingByEventType.TryGetValue(eventType, out int remaining) ||
                    remaining <= 0)
                {
                    return false;
                }

                _throwsRemainingByEventType[eventType] = remaining - 1;
                return true;
            }
        }

        private bool ShouldBlock(string eventType)
        {
            lock (_gate)
            {
                return _blockedEventTypes.Contains(eventType);
            }
        }

        private void SignalChanged()
        {
            _nextPublicationChanged.TrySetResult();
            _nextPublicationChanged =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task<T> WaitForAsync<T>(
            Func<T?> getResult,
            TimeSpan timeout)
            where T : class
        {
            using var timeoutSource = new CancellationTokenSource(timeout);

            while (true)
            {
                Task nextChange;

                lock (_gate)
                {
                    T? result = getResult();

                    if (result is not null)
                    {
                        return result;
                    }

                    nextChange = _nextPublicationChanged.Task;
                }

                await nextChange.WaitAsync(timeoutSource.Token);
            }
        }

        private async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
        {
            using var timeoutSource = new CancellationTokenSource(timeout);

            while (true)
            {
                Task nextChange;

                lock (_gate)
                {
                    if (condition())
                    {
                        return;
                    }

                    nextChange = _nextPublicationChanged.Task;
                }

                await nextChange.WaitAsync(timeoutSource.Token);
            }
        }
    }

    private sealed record Publication(
        string EventType,
        object? Payload,
        Type DeclaredPayloadType,
        DateTimeOffset OccurredAt,
        string? CorrelationId);
}
