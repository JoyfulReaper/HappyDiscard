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

    public const string UdpStarted =
        "happydiscard.udp.started";

    public const string UdpStopped =
        "happydiscard.udp.stopped";

    public const string UdpDatagramDiscarded =
        "happydiscard.udp.datagram.discarded";

    public const string UdpDatagramDropped =
        "happydiscard.udp.datagram.dropped";
}
