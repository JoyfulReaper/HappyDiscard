/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

namespace HappyDiscard.Events;

public sealed record DiscardStartedEvent(
    string Remote,
    int RequestTimeoutSeconds,
    long MaxBytesPerConnection);

public sealed record DiscardStoppedEvent(
    string Remote,
    long BytesDiscarded,
    long DurationMilliseconds,
    string Outcome,
    bool Succeeded);