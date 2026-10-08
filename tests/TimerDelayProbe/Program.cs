using PlaytimeGoals;

static void AssertMilliseconds(
    ulong remainingSeconds,
    double expectedMilliseconds
) {
    TimeSpan actual =
        CompletionTimerDelay.FromRemainingSeconds(
            remainingSeconds
        );

    if (actual.TotalMilliseconds != expectedMilliseconds) {
        throw new InvalidOperationException(
            $"remaining={remainingSeconds}: expected {expectedMilliseconds} ms, got {actual.TotalMilliseconds} ms"
        );
    }
}

AssertMilliseconds(0, 0);
AssertMilliseconds(1, 1_000);
AssertMilliseconds(4_294_967, 4_294_967_000);
AssertMilliseconds(
    4_294_968,
    CompletionTimerDelay.MaxDueMilliseconds
);
AssertMilliseconds(
    6_514_817,
    CompletionTimerDelay.MaxDueMilliseconds
);
AssertMilliseconds(
    ulong.MaxValue,
    CompletionTimerDelay.MaxDueMilliseconds
);

Console.WriteLine("LONG COMPLETION TIMER PROBE: PASS");
