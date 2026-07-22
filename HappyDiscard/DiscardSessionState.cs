/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

namespace HappyDiscard;

internal sealed class DiscardSessionState
{
    public long BytesDiscarded { get; set; }
    public bool ByteLimitReached { get; set; }
}
