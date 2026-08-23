/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

namespace HappyDiscard.Events;

public sealed record UdpDiscardStartedEvent(
    string ListenEndpoint,
    int MaxDatagramBytes);

public sealed record UdpDiscardStoppedEvent(
    string ListenEndpoint,
    long DatagramsReceived,
    long DatagramsDiscarded,
    long DatagramsDropped,
    long BytesDiscarded,
    long DurationMilliseconds);

public sealed record UdpDatagramDiscardedEvent(
    string Remote,
    int BytesDiscarded);

public sealed record UdpDatagramDroppedEvent(
    string Remote,
    int BytesReceived,
    string Reason);
