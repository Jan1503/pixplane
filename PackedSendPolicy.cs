namespace PixPlane;

/// <summary>
///     Whether a packed frame is skipped, sent as a key (all fragments), or as a delta (dirty only).
///     Keys are the first frame and then at most once per second for loss recovery. High-motion
///     frames stay deltas so 14-bit video does not pay a full key whenever more than half the
///     slots changed.
/// </summary>
public static class PackedSendPolicy
{
    /// <summary>
    ///     <paramref name="nChanged"/> is the number of dirty 1440-byte slots (or all slots on the
    ///     first frame). <paramref name="dueKey"/> is true when no previous frame exists or the
    ///     key interval has elapsed.
    /// </summary>
    public static bool TrySend(bool havePrev, bool dueKey, int nChanged, bool dirtySinceKey, out bool key)
    {
        if (nChanged == 0 && (!dueKey || !dirtySinceKey))
        {
            key = false;
            return false;
        }

        key = !havePrev || dueKey;
        return true;
    }
}
