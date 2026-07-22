/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

namespace HappyDiscard.Events;

public static class HappyDiscardEventTypes
{
    public const string DiscardStarted =
        "happydiscard.discarding.started";

    public const string DiscardStopped =
        "happydiscard.discarding.stopped";

    public const string ServiceStarted =
        "happydiscard.service.started";
}
