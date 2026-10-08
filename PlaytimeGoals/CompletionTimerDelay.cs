using System;

namespace PlaytimeGoals;

internal static class CompletionTimerDelay {
    /*
     * System.Threading.Timer's native timer queue rejects due times
     * above uint.MaxValue - 1 milliseconds on current .NET runtimes.
     *
     * Long playtime goals therefore use a capped wake-up interval.
     * GoalRunner's one-minute heartbeat continuously recomputes the
     * actual remaining target time, so once the deadline enters the
     * supported timer window the exact remaining delay is scheduled.
     */
    internal const uint MaxDueMilliseconds =
        uint.MaxValue - 1;

    internal static TimeSpan FromRemainingSeconds(
        ulong remainingSeconds
    ) {
        if (remainingSeconds == 0) {
            return TimeSpan.Zero;
        }

        ulong maxWholeSeconds =
            MaxDueMilliseconds / 1000UL;

        if (remainingSeconds > maxWholeSeconds) {
            return TimeSpan.FromMilliseconds(
                MaxDueMilliseconds
            );
        }

        return TimeSpan.FromSeconds(
            remainingSeconds
        );
    }
}
