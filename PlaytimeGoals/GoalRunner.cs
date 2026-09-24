using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlaytimeGoals;

internal interface IBotStateView {
    bool Connected { get; }
    bool Farming { get; }
    bool FarmerPaused { get; }
    bool PlayingPossible { get; }
}

internal sealed record ManagedGameSnapshot(
    uint AppId,
    string Name,
    uint PlaytimeForeverMinutes,
    bool Runnable,
    string BlockState
);

internal interface IGoalSteamActions {
    bool RecoveryReady { get; }

    Task<bool> EnsureRecovery();

    Task ReleaseGames(bool clearSteamGames);

    Task AssertGames(
        IReadOnlyCollection<uint> appIds
    );

    Task<IReadOnlyList<ManagedGameSnapshot>?>
        FetchManagedLibrary();
}

internal sealed record GoalGameStatus(
    uint AppId,
    string Name,
    double? TargetHours,
    double CurrentHours,
    double EffectiveHours,
    double? RemainingHours,
    string State,
    int? QueuePosition = null
);

internal sealed record GoalStatusSnapshot(
    bool Enabled,
    byte BatchSize,
    bool Connected,
    bool Farming,
    bool FarmerPaused,
    bool PlayingPossible,
    bool RecoveryReady,
    IReadOnlyList<uint> CurrentBatch,
    IReadOnlyList<GoalGameStatus> Games,
    DateTime SnapshotUtc
);

internal sealed class GoalRunner : IDisposable {
    private static readonly TimeSpan FetchInterval =
        TimeSpan.FromMinutes(1);

    private static readonly TimeSpan FetchTimeout =
        TimeSpan.FromSeconds(45);

    private static readonly TimeSpan ReassertInterval =
        TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PersistInterval =
        TimeSpan.FromMinutes(10);

    private readonly GoalConfig config;
    private readonly IBotStateView botState;
    private readonly IGoalSteamActions steam;
    private readonly GoalStateStore store;
    private readonly Action<string> info;
    private readonly Action<string> warn;
    private readonly SemaphoreSlim sync = new(1, 1);
    private readonly Timer? heartbeat;

    private readonly Dictionary<
        uint,
        ManagedGameSnapshot
    > library = new();

    private Dictionary<uint, CreditEntry> ledger;

    private uint[] currentBatch =
        Array.Empty<uint>();

    private bool asserted;
    private bool disposed;

    private DateTime creditStartUtc =
        DateTime.MinValue;

    private DateTime lastFetchUtc =
        DateTime.MinValue;

    private DateTime lastPersistUtc =
        DateTime.MinValue;

    private DateTime lastSendUtc =
        DateTime.MinValue;

    internal GoalRunner(
        GoalConfig config,
        IBotStateView botState,
        IGoalSteamActions steam,
        GoalStateStore store,
        Action<string> info,
        Action<string> warn
    ) {
        this.config =
            config ??
            throw new ArgumentNullException(
                nameof(config)
            );

        this.botState =
            botState ??
            throw new ArgumentNullException(
                nameof(botState)
            );

        this.steam =
            steam ??
            throw new ArgumentNullException(
                nameof(steam)
            );

        this.store =
            store ??
            throw new ArgumentNullException(
                nameof(store)
            );

        this.info =
            info ??
            throw new ArgumentNullException(
                nameof(info)
            );

        this.warn =
            warn ??
            throw new ArgumentNullException(
                nameof(warn)
            );

        ledger =
            store.Load();

        HashSet<uint> configured =
            config.GameIds.ToHashSet();

        ledger =
            ledger
                .Where(
                    pair =>
                        configured.Contains(
                            pair.Key
                        )
                )
                .ToDictionary(
                    static pair =>
                        pair.Key,
                    static pair =>
                        pair.Value
                );

        if (config.Enabled) {
            heartbeat =
                new Timer(
                    _ =>
                        _ = Pump(
                            "heartbeat"
                        ),
                    null,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMinutes(1)
                );
        }
    }

    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        heartbeat?.Dispose();
        sync.Dispose();
    }

    internal async Task Stop(
        string reason
    ) {
        if (disposed) {
            return;
        }

        await sync
            .WaitAsync()
            .ConfigureAwait(false);

        try {
            await StandDown(
                    reason,
                    clearSteamGames:
                        botState.Connected &&
                        !botState.Farming
                )
                .ConfigureAwait(false);
        } catch (Exception e) {
            warn(
                $"stop failed: {e.Message}"
            );
        } finally {
            sync.Release();
        }
    }

    internal async Task Pump(
        string reason,
        bool forceReassert = false,
        bool refreshLibrary = false
    ) {
        if (
            disposed ||
            !config.Enabled
        ) {
            return;
        }

        await sync
            .WaitAsync()
            .ConfigureAwait(false);

        try {
            if (disposed) {
                return;
            }

            DateTime now =
                DateTime.UtcNow;

            if (asserted) {
                SettleCredit(now);
            }

            /*
             * CardsFarmer owns GamesPlayed while it is farming.
             *
             * Do NOT send PlayGames(empty) here. CardsFarmer sets
             * NowFarming before starting its asynchronous Farm()
             * loop, so a late empty message from this plugin could
             * otherwise erase ASF's own farming GamesPlayed state.
             *
             * We still release our bookkeeping and restore any
             * temporary Family View custom entries.
             */
            if (botState.Farming) {
                if (asserted) {
                    await StandDown(
                            reason,
                            clearSteamGames: false
                        )
                        .ConfigureAwait(false);
                }

                return;
            }

            if (!CanIdle()) {
                if (asserted) {
                    await StandDown(
                            reason,
                            clearSteamGames:
                                botState.Connected
                        )
                        .ConfigureAwait(false);
                }

                return;
            }

            if (!steam.RecoveryReady) {
                bool recovered =
                    await steam
                        .EnsureRecovery()
                        .ConfigureAwait(false);

                if (!recovered) {
                    warn(
                        "Family View recovery gate is closed; " +
                        "idling remains stopped"
                    );

                    return;
                }
            }

            bool fetchDue =
                (library.Count == 0) ||
                (
                    (now - lastFetchUtc) >=
                    FetchInterval
                );

            if (
                fetchDue ||
                refreshLibrary
            ) {
                await RefreshLibrary(
                        now
                    )
                    .ConfigureAwait(false);
            }

            uint[] selected =
                SelectBatch();

            if (selected.Length == 0) {
                if (asserted) {
                    await StandDown(
                            "all managed games are complete or unavailable",
                            clearSteamGames: true
                        )
                        .ConfigureAwait(false);
                }

                return;
            }

            bool changed =
                !currentBatch.SequenceEqual(
                    selected
                );

            bool reassertDue =
                forceReassert ||
                (
                    (now - lastSendUtc) >=
                    ReassertInterval
                );

            if (asserted && changed) {
                /*
                 * Transaction boundary for a batch change:
                 *
                 * 1. stop old GamesPlayed;
                 * 2. restore stale Family View state;
                 * 3. allow the new batch;
                 * 4. send new GamesPlayed.
                 *
                 * If step 2 or 3 fails, local ownership remains
                 * released and the recovery gate prevents a new
                 * batch from starting.
                 */
                await StandDown(
                        "batch changed",
                        clearSteamGames: true
                    )
                    .ConfigureAwait(false);

                if (!steam.RecoveryReady) {
                    return;
                }
            }

            if (!asserted) {
                await steam
                    .AssertGames(
                        selected
                    )
                    .ConfigureAwait(false);

                currentBatch =
                    selected;

                asserted = true;
                creditStartUtc = now;
                lastSendUtc = now;

                info(
                    $"idling {currentBatch.Length} managed game(s): " +
                    $"{string.Join(',', currentBatch)} ({reason})"
                );
            } else if (reassertDue) {
                await steam
                    .AssertGames(
                        currentBatch
                    )
                    .ConfigureAwait(false);

                lastSendUtc = now;

                info(
                    $"reasserted {currentBatch.Length} managed game(s) " +
                    $"({reason})"
                );
            }

            Persist(false);
        } catch (Exception e) {
            warn(
                $"pump failed ({reason}): {e}"
            );
        } finally {
            sync.Release();
        }
    }

    internal async Task<GoalStatusSnapshot>
        GetStatus(
            bool refresh = false
        ) {
        ObjectDisposedException.ThrowIf(
            disposed,
            this
        );

        await sync
            .WaitAsync()
            .ConfigureAwait(false);

        try {
            DateTime now =
                DateTime.UtcNow;

            if (asserted) {
                SettleCredit(now);
            }

            if (
                refresh &&
                botState.Connected &&
                (
                    library.Count == 0 ||
                    (
                        (now - lastFetchUtc) >=
                        TimeSpan.FromSeconds(30)
                    )
                )
            ) {
                await RefreshLibrary(
                        now
                    )
                    .ConfigureAwait(false);
            }

            uint[] orderedCandidates =
                OrderedCandidates();

            List<GoalGameStatus> games =
                new();

            foreach (
                uint appId
                in config.GameIds
                    .OrderBy(
                        static id =>
                            id
                    )
            ) {
                double? targetHours =
                    config.GetTargetHours(
                        appId
                    );

                library.TryGetValue(
                    appId,
                    out ManagedGameSnapshot? game
                );

                uint serverMinutes =
                    game?.PlaytimeForeverMinutes ??
                    0;

                uint effectiveMinutes =
                    EffectiveMinutes(
                        appId,
                        serverMinutes
                    );

                double? remaining =
                    targetHours is > 0
                        ? Math.Max(
                            0d,
                            targetHours.Value -
                            (
                                effectiveMinutes /
                                60d
                            )
                        )
                        : null;

                string state =
                    ResolveState(
                        appId,
                        game,
                        targetHours,
                        effectiveMinutes
                    );

                int? queuePosition =
                    null;

                if (
                    state ==
                        "queued"
                ) {
                    int candidateIndex =
                        Array.IndexOf(
                            orderedCandidates,
                            appId
                        );

                    if (
                        candidateIndex >=
                            config.BatchSize
                    ) {
                        queuePosition =
                            candidateIndex -
                            config.BatchSize +
                            1;
                    }
                }

                games.Add(
                    new GoalGameStatus(
                        appId,
                        game?.Name ??
                            string.Empty,
                        targetHours,
                        Math.Round(
                            serverMinutes /
                            60d,
                            2
                        ),
                        Math.Round(
                            effectiveMinutes /
                            60d,
                            2
                        ),
                        remaining.HasValue
                            ? Math.Round(
                                remaining.Value,
                                2
                            )
                            : null,
                        state,
                        queuePosition
                    )
                );
            }

            return new GoalStatusSnapshot(
                config.Enabled,
                config.BatchSize,
                botState.Connected,
                botState.Farming,
                botState.FarmerPaused,
                botState.PlayingPossible,
                steam.RecoveryReady,
                currentBatch.ToArray(),
                games,
                lastFetchUtc
            );
        } finally {
            sync.Release();
        }
    }

    private bool CanIdle() =>
        botState.Connected &&
        !botState.Farming &&
        !botState.FarmerPaused &&
        botState.PlayingPossible;

    private async Task StandDown(
        string reason,
        bool clearSteamGames
    ) {
        bool hadAssertion =
            asserted;

        if (hadAssertion) {
            SettleCredit(
                DateTime.UtcNow
            );
        }

        /*
         * Local ownership is dropped BEFORE any remote operation.
         *
         * This is intentional. PlayGames(empty) can succeed while
         * the subsequent Family View restore fails. In that case
         * we must never keep a stale "asserted" state locally.
         */
        asserted = false;
        currentBatch =
            Array.Empty<uint>();

        creditStartUtc =
            DateTime.MinValue;

        lastSendUtc =
            DateTime.UtcNow;

        Persist(true);

        await steam
            .ReleaseGames(
                clearSteamGames &&
                hadAssertion
            )
            .ConfigureAwait(false);

        if (hadAssertion) {
            info(
                clearSteamGames
                    ? $"cleared games ({reason})"
                    : $"released plugin batch without clearing GamesPlayed ({reason})"
            );
        }
    }

    private async Task RefreshLibrary(
        DateTime now
    ) {
        Task<
            IReadOnlyList<
                ManagedGameSnapshot
            >?
        > fetch =
            steam.FetchManagedLibrary();

        Task winner =
            await Task.WhenAny(
                    fetch,
                    Task.Delay(
                        FetchTimeout
                    )
                )
                .ConfigureAwait(false);

        if (winner != fetch) {
            warn(
                "managed-library fetch timed out; " +
                "using previous snapshot"
            );

            return;
        }

        IReadOnlyList<
            ManagedGameSnapshot
        >? fetched =
            await fetch
                .ConfigureAwait(false);

        if (fetched == null) {
            warn(
                "managed-library fetch failed; " +
                "using previous snapshot"
            );

            return;
        }

        library.Clear();

        foreach (
            ManagedGameSnapshot game
            in fetched
        ) {
            if (
                !library.TryGetValue(
                    game.AppId,
                    out ManagedGameSnapshot? existing
                ) ||
                game.PlaytimeForeverMinutes >
                    existing.PlaytimeForeverMinutes
            ) {
                library[game.AppId] =
                    game;
            }
        }

        ReconcileLedger();
        lastFetchUtc = now;

        Persist(true);
    }

    private uint[] SelectBatch() =>
        OrderedCandidates()
            .Take(
                config.BatchSize
            )
            .ToArray();

    private uint[] OrderedCandidates() {
        List<
            (
                uint AppId,
                bool Unlimited,
                uint Remaining
            )
        > candidates =
            new();

        foreach (
            uint appId
            in config.GameIds
        ) {
            if (
                !library.TryGetValue(
                    appId,
                    out ManagedGameSnapshot? game
                ) ||
                !game.Runnable
            ) {
                continue;
            }

            double? targetHours =
                config.GetTargetHours(
                    appId
                );

            if (targetHours is > 0) {
                uint target =
                    GoalConfig.TargetMinutes(
                        targetHours.Value
                    );

                uint effective =
                    EffectiveMinutes(
                        appId,
                        game.PlaytimeForeverMinutes
                    );

                if (effective >= target) {
                    continue;
                }

                candidates.Add(
                    (
                        appId,
                        false,
                        target - effective
                    )
                );
            } else {
                candidates.Add(
                    (
                        appId,
                        true,
                        0
                    )
                );
            }
        }

        return candidates
            .OrderBy(
                static candidate =>
                    candidate.Unlimited
            )
            .ThenByDescending(
                static candidate =>
                    candidate.Remaining
            )
            .ThenBy(
                static candidate =>
                    candidate.AppId
            )
            .Select(
                static candidate =>
                    candidate.AppId
            )
            .ToArray();
    }

    private string ResolveState(
        uint appId,
        ManagedGameSnapshot? game,
        double? targetHours,
        uint effectiveMinutes
    ) {
        if (
            targetHours is > 0 &&
            effectiveMinutes >=
                GoalConfig.TargetMinutes(
                    targetHours.Value
                )
        ) {
            return "complete";
        }

        if (
            asserted &&
            currentBatch.Contains(
                appId
            )
        ) {
            return targetHours == null
                ? "idling-unlimited"
                : "idling";
        }

        if (game == null) {
            return botState.Connected
                ? "unavailable"
                : "unknown";
        }

        if (!game.Runnable) {
            return string.IsNullOrEmpty(
                    game.BlockState
                )
                ? "unavailable"
                : game.BlockState;
        }

        if (!botState.PlayingPossible) {
            return "account-in-use";
        }

        if (botState.Farming) {
            return "asf-farming";
        }

        if (botState.FarmerPaused) {
            return "asf-paused";
        }

        return "queued";
    }

    private void SettleCredit(
        DateTime now
    ) {
        if (
            !asserted ||
            creditStartUtc ==
                DateTime.MinValue ||
            currentBatch.Length == 0
        ) {
            return;
        }

        uint minutes =
            (uint) Math.Floor(
                (
                    now -
                    creditStartUtc
                ).TotalMinutes
            );

        if (minutes == 0) {
            return;
        }

        foreach (
            uint appId
            in currentBatch
        ) {
            uint server =
                library.TryGetValue(
                    appId,
                    out ManagedGameSnapshot? game
                )
                    ? game.PlaytimeForeverMinutes
                    : 0;

            ledger[appId] =
                ledger.TryGetValue(
                    appId,
                    out CreditEntry? entry
                )
                    ? entry with {
                        CreditedMinutes =
                            entry.CreditedMinutes +
                            minutes
                    }
                    : new CreditEntry(
                        server,
                        minutes
                    );
        }

        creditStartUtc =
            creditStartUtc
                .AddMinutes(
                    minutes
                );
    }

    private uint EffectiveMinutes(
        uint appId,
        uint serverMinutes
    ) {
        if (
            !ledger.TryGetValue(
                appId,
                out CreditEntry? entry
            )
        ) {
            return serverMinutes;
        }

        ulong believed =
            (ulong)
            entry.ServerBaselineMinutes +
            entry.CreditedMinutes;

        return (uint) Math.Min(
            uint.MaxValue,
            Math.Max(
                (ulong) serverMinutes,
                believed
            )
        );
    }

    private void ReconcileLedger() {
        Dictionary<
            uint,
            CreditEntry
        > reconciled =
            new();

        foreach (
            (
                uint appId,
                CreditEntry entry
            )
            in ledger
        ) {
            if (
                !library.TryGetValue(
                    appId,
                    out ManagedGameSnapshot? game
                )
            ) {
                reconciled[appId] =
                    entry;

                continue;
            }

            uint reported =
                game.PlaytimeForeverMinutes;

            ulong believed =
                (ulong)
                entry.ServerBaselineMinutes +
                entry.CreditedMinutes;

            if (reported >= believed) {
                continue;
            }

            if (
                reported >
                entry.ServerBaselineMinutes
            ) {
                reconciled[appId] =
                    new CreditEntry(
                        reported,
                        (uint)
                        (
                            believed -
                            reported
                        )
                    );
            } else {
                reconciled[appId] =
                    entry;
            }
        }

        ledger =
            reconciled;
    }

    private void Persist(
        bool force
    ) {
        DateTime now =
            DateTime.UtcNow;

        if (
            !force &&
            (
                (now - lastPersistUtc) <
                PersistInterval
            )
        ) {
            return;
        }

        store.Save(
            ledger
        );

        lastPersistUtc =
            now;
    }
}
