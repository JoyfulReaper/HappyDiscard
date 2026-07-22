/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

namespace HappyDaytime.Events;

public sealed record DiscardRequestCompletedEvent(
    string Remote,
    long bytesDiscarded,
    long DurationMilliseconds,
    string Outcome,
    bool Succeeded)
{
    public const string EventName = "happydiscard.request.completed";
}