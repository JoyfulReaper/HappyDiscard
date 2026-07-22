/*
 * Happy Daytime Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

namespace HappyDiscard.Events;

public sealed record DiscardServiceStartedEvent(
    string ListenAddress);