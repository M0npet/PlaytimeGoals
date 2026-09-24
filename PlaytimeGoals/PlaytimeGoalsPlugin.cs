using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Composition;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;
using SteamKit2;
using SteamKit2.Internal;

namespace PlaytimeGoals;

[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed class PlaytimeGoalsPlugin :
    IPlugin,
    IBot,
    IBotModules,
    IBotSteamClient,
    IBotCardsFarmerInfo,
    IBotConnection {

    private const int PostTransitionDelayMs = 3000;
    private const int LogonSettleDelayMs = 8000;
    private const int ReadOnlyProbeDelayMs = 12000;

    private static readonly
        ConcurrentDictionary<
            string,
            GoalHandler
        > Handlers =
            new(
                StringComparer.OrdinalIgnoreCase
            );

    private static readonly
        ConcurrentDictionary<
            string,
            GoalConfig
        > Configs =
            new(
                StringComparer.OrdinalIgnoreCase
            );

    private static readonly
        ConcurrentDictionary<
            string,
            GoalRunner
        > Runners =
            new(
                StringComparer.OrdinalIgnoreCase
            );

    private static readonly
        ConcurrentDictionary<
            string,
            ParentalPolicyService
        > Policies =
            new(
                StringComparer.OrdinalIgnoreCase
            );

    private static readonly
        ConcurrentDictionary<
            string,
            FamilyLibraryCache
        > LibraryCaches =
            new(
                StringComparer.OrdinalIgnoreCase
            );

    private static readonly
        ConcurrentDictionary<
            string,
            bool
        > LastPlayingBlocked =
            new(
                StringComparer.OrdinalIgnoreCase
            );

    public string Name =>
        "PlaytimeGoals";

    public Version Version =>
        typeof(PlaytimeGoalsPlugin)
            .Assembly
            .GetName()
            .Version
        ?? new Version(0, 5, 0, 0);

    public Task OnLoaded() {
        ASF.ArchiLogger.LogGenericInfo(
            $"PlaytimeGoals {Version} loaded"
        );

        return Task.CompletedTask;
    }

    public Task OnBotInit(
        Bot bot
    ) {
        ArgumentNullException.ThrowIfNull(
            bot
        );

        return Task.CompletedTask;
    }

    public async Task OnBotDestroy(
        Bot bot
    ) {
        ArgumentNullException.ThrowIfNull(
            bot
        );

        if (
            Handlers.TryGetValue(
                bot.BotName,
                out GoalHandler? handler
            )
        ) {
            handler.FamilyRunningAppsChanged =
                null;
        }

        if (
            Runners.TryRemove(
                bot.BotName,
                out GoalRunner? runner
            )
        ) {
            await runner
                .Stop(
                    "bot destroyed"
                )
                .ConfigureAwait(false);

            runner.Dispose();
        }

        if (
            Policies.TryRemove(
                bot.BotName,
                out ParentalPolicyService? policy
            )
        ) {
            bool recovered =
                await policy
                    .RecoverOutstanding()
                    .ConfigureAwait(false);

            if (!recovered) {
                bot.ArchiLogger
                    .LogGenericWarning(
                        "PlaytimeGoals: Family View recovery remains pending " +
                        "during bot destroy"
                    );
            }

            policy.Dispose();
        }

        if (
            LibraryCaches.TryRemove(
                bot.BotName,
                out FamilyLibraryCache? cache
            )
        ) {
            cache.Dispose();
        }

        Configs.TryRemove(
            bot.BotName,
            out _
        );

        Handlers.TryRemove(
            bot.BotName,
            out _
        );

        LastPlayingBlocked.TryRemove(
            bot.BotName,
            out _
        );
    }

    public Task<
        IReadOnlyCollection<
            ClientMsgHandler
        >?
    > OnBotSteamHandlersInit(
        Bot bot
    ) {
        ArgumentNullException.ThrowIfNull(
            bot
        );

        GoalHandler handler =
            new();

        Handlers[bot.BotName] =
            handler;

        return Task.FromResult<
            IReadOnlyCollection<
                ClientMsgHandler
            >?
        >(
            new ClientMsgHandler[] {
                handler
            }
        );
    }

    public Task OnBotSteamCallbacksInit(
        Bot bot,
        CallbackManager callbackManager
    ) {
        ArgumentNullException.ThrowIfNull(
            bot
        );

        ArgumentNullException.ThrowIfNull(
            callbackManager
        );

        callbackManager.Subscribe<
            SteamUser.PlayingSessionStateCallback
        >(
            callback =>
                OnPlayingSessionState(
                    bot,
                    callback
                )
        );

        callbackManager
            .SubscribeServiceNotification<
                FamilyGroupsClient,
                CFamilyGroupsClient_NotifyRunningApps_Notification
            >(
                notification =>
                    OnFamilyRunningApps(
                        bot,
                        notification.Body
                    )
            );

        callbackManager
            .SubscribeServiceNotification<
                FamilyGroupsClient,
                CFamilyGroupsClient_GroupChanged_Notification
            >(
                notification =>
                    OnFamilyGroupChanged(
                        bot,
                        notification.Body
                    )
            );

        return Task.CompletedTask;
    }

    public async Task OnBotInitModules(
        Bot bot,
        IReadOnlyDictionary<
            string,
            JsonElement
        >? additionalConfigProperties =
            null
    ) {
        ArgumentNullException.ThrowIfNull(
            bot
        );

        if (
            Handlers.TryGetValue(
                bot.BotName,
                out GoalHandler? existingHandler
            )
        ) {
            existingHandler.FamilyRunningAppsChanged =
                null;
        }

        if (
            Runners.TryRemove(
                bot.BotName,
                out GoalRunner? previous
            )
        ) {
            await previous
                .Stop(
                    "config reload"
                )
                .ConfigureAwait(false);

            previous.Dispose();
        }

        if (
            Policies.TryRemove(
                bot.BotName,
                out ParentalPolicyService? oldPolicy
            )
        ) {
            bool oldRecovered =
                await oldPolicy
                    .RecoverOutstanding()
                    .ConfigureAwait(false);

            if (!oldRecovered) {
                bot.ArchiLogger
                    .LogGenericWarning(
                        "PlaytimeGoals: previous Family View journal " +
                        "remains pending across config reload"
                    );
            }

            oldPolicy.Dispose();
        }

        if (
            LibraryCaches.TryRemove(
                bot.BotName,
                out FamilyLibraryCache? oldCache
            )
        ) {
            oldCache.Dispose();
        }

        (
            GoalConfig config,
            IReadOnlyList<string> errors
        ) = GoalConfigParser.Parse(
            additionalConfigProperties
        );

        Configs[bot.BotName] =
            config;

        if (errors.Count > 0) {
            foreach (
                string error
                in errors
            ) {
                bot.ArchiLogger
                    .LogGenericError(
                        "PlaytimeGoals config: " +
                        error
                    );
            }

            return;
        }

        if (
            config.Enabled &&
            (
                bot.BotConfig
                    .GamesPlayedWhileIdle
                    .Count > 0 ||
                !string.IsNullOrEmpty(
                    bot.BotConfig
                        .CustomGamePlayedWhileIdle
                )
            )
        ) {
            bot.ArchiLogger
                .LogGenericError(
                    "PlaytimeGoals: GamesPlayedWhileIdle must be empty " +
                    "and CustomGamePlayedWhileIdle must be null/empty " +
                    "while PlaytimeGoalsEnabled=true"
                );

            return;
        }

        if (
            !Handlers.TryGetValue(
                bot.BotName,
                out GoalHandler? handler
            )
        ) {
            bot.ArchiLogger
                .LogGenericError(
                    "PlaytimeGoals: Steam handler is unavailable"
                );

            return;
        }

        string statePath =
            Bot.GetFilePath(
                $"{bot.BotName}_PlaytimeGoals",
                Bot.EFileType.Database
            );

        string parentalJournalPath =
            Bot.GetFilePath(
                $"{bot.BotName}_PlaytimeGoalsParental",
                Bot.EFileType.Database
            );

        FamilyLibraryCache cache =
            new(
                bot,
                handler
            );

        LibraryCaches[bot.BotName] =
            cache;

        ParentalPolicyService policy =
            new(
                bot,
                handler,
                new ParentalStateJournal(
                    parentalJournalPath
                ),
                config.ParentalWritesEnabled,
                message =>
                    bot.ArchiLogger
                        .LogGenericInfo(
                            "PlaytimeGoals: " +
                            message
                        ),
                message =>
                    bot.ArchiLogger
                        .LogGenericWarning(
                            "PlaytimeGoals: " +
                            message
                        )
            );

        Policies[bot.BotName] =
            policy;

        if (bot.IsConnectedAndLoggedOn) {
            bool recovered =
                await policy
                    .RecoverOutstanding()
                    .ConfigureAwait(false);

            if (!recovered) {
                bot.ArchiLogger
                    .LogGenericWarning(
                        "PlaytimeGoals: Family View recovery gate is closed"
                    );
            }
        }

        GoalRunner runner =
            new(
                config,
                new BotStateAdapter(
                    bot
                ),
                new SteamActionsAdapter(
                    bot,
                    handler,
                    cache,
                    policy,
                    config
                ),
                new GoalStateStore(
                    statePath
                ),
                message =>
                    bot.ArchiLogger
                        .LogGenericInfo(
                            "PlaytimeGoals: " +
                            message
                        ),
                message =>
                    bot.ArchiLogger
                        .LogGenericWarning(
                            "PlaytimeGoals: " +
                            message
                        )
            );

        Runners[bot.BotName] =
            runner;

        handler.FamilyRunningAppsChanged =
            () =>
                _ = PumpSoon(
                    bot,
                    0,
                    "family running apps changed",
                    forceReassert: false,
                    refreshLibrary: true
                );

        int managedGames =
            config.GameIds.Count;

        bot.ArchiLogger.LogGenericInfo(
            "PlaytimeGoals config loaded: " +
            $"enabled={config.Enabled}, " +
            $"parentalWrites={config.ParentalWritesEnabled}, " +
            $"recoveryReady={policy.RecoveryReady}, " +
            $"batch={config.BatchSize}, " +
            $"games={managedGames}"
        );

        if (
            config.Enabled &&
            managedGames > 0 &&
            bot.IsConnectedAndLoggedOn
        ) {
            Utilities.InBackground(
                () =>
                    runner.Pump(
                        "config loaded",
                        true,
                        true
                    )
            );
        }
    }

    public Task OnBotFarmingStarted(
        Bot bot
    ) =>
        PumpSoon(
            bot,
            0,
            "farming started"
        );

    public Task OnBotFarmingStopped(
        Bot bot
    ) =>
        PumpSoon(
            bot,
            PostTransitionDelayMs,
            "farming stopped",
            true
        );

    public Task OnBotFarmingFinished(
        Bot bot,
        bool farmedSomething
    ) =>
        PumpSoon(
            bot,
            PostTransitionDelayMs,
            "farming finished",
            true
        );

    public Task OnBotLoggedOn(
        Bot bot
    ) {
        Utilities.InBackground(
            async () => {
                await Task
                    .Delay(
                        LogonSettleDelayMs
                    )
                    .ConfigureAwait(false);

                if (
                    LibraryCaches.TryGetValue(
                        bot.BotName,
                        out FamilyLibraryCache? cache
                    )
                ) {
                    cache.Invalidate();
                }

                bool recovered = true;

                if (
                    Policies.TryGetValue(
                        bot.BotName,
                        out ParentalPolicyService? policy
                    )
                ) {
                    recovered =
                        await policy
                            .RecoverOutstanding()
                            .ConfigureAwait(false);
                }

                if (
                    recovered &&
                    Runners.TryGetValue(
                        bot.BotName,
                        out GoalRunner? runner
                    )
                ) {
                    await runner
                        .Pump(
                            "logged on",
                            true,
                            true
                        )
                        .ConfigureAwait(false);
                }

                int remainingProbeDelay =
                    Math.Max(
                        0,
                        ReadOnlyProbeDelayMs -
                        LogonSettleDelayMs
                    );

                if (remainingProbeDelay > 0) {
                    await Task
                        .Delay(
                            remainingProbeDelay
                        )
                        .ConfigureAwait(false);
                }

                await ProbeSteamState(
                        bot
                    )
                    .ConfigureAwait(false);
            }
        );

        return Task.CompletedTask;
    }

    public Task OnBotDisconnected(
        Bot bot,
        EResult reason
    ) =>
        PumpSoon(
            bot,
            0,
            "disconnected"
        );

    private static void
        OnPlayingSessionState(
            Bot bot,
            SteamUser
                .PlayingSessionStateCallback
                callback
        ) {
        bool blocked =
            callback.PlayingBlocked;

        if (
            LastPlayingBlocked
                .TryGetValue(
                    bot.BotName,
                    out bool previous
                ) &&
            (previous == blocked)
        ) {
            return;
        }

        LastPlayingBlocked[
            bot.BotName
        ] = blocked;

        _ = PumpSoon(
            bot,
            PostTransitionDelayMs,
            blocked
                ? "account blocked"
                : "account freed",
            !blocked
        );
    }

    private static void
        OnFamilyRunningApps(
            Bot bot,
            CFamilyGroupsClient_NotifyRunningApps_Notification
                notification
        ) {
        if (
            Handlers.TryGetValue(
                bot.BotName,
                out GoalHandler? handler
            )
        ) {
            handler.UpdateFamilyRunningApps(
                notification
            );
        }
    }

    private static void
        OnFamilyGroupChanged(
            Bot bot,
            CFamilyGroupsClient_GroupChanged_Notification
                notification
        ) {
        if (
            LibraryCaches.TryGetValue(
                bot.BotName,
                out FamilyLibraryCache? cache
            )
        ) {
            cache.Invalidate();
        }

        _ = PumpSoon(
            bot,
            0,
            "family group changed",
            forceReassert: false,
            refreshLibrary: true
        );
    }

    private static Task PumpSoon(
        Bot bot,
        int delayMs,
        string reason,
        bool forceReassert = false,
        bool refreshLibrary = false
    ) {
        if (
            Runners.TryGetValue(
                bot.BotName,
                out GoalRunner? runner
            )
        ) {
            Utilities.InBackground(
                async () => {
                    if (delayMs > 0) {
                        await Task
                            .Delay(delayMs)
                            .ConfigureAwait(false);
                    }

                    await runner
                        .Pump(
                            reason,
                            forceReassert,
                            refreshLibrary
                        )
                        .ConfigureAwait(false);
                }
            );
        }

        return Task.CompletedTask;
    }

    private static async Task
        ProbeSteamState(
            Bot bot
        ) {
        if (
            !bot.IsConnectedAndLoggedOn
        ) {
            return;
        }

        if (
            !Handlers.TryGetValue(
                bot.BotName,
                out GoalHandler? handler
            ) ||
            !LibraryCaches.TryGetValue(
                bot.BotName,
                out FamilyLibraryCache? cache
            )
        ) {
            return;
        }

        try {
            FamilyLibrarySnapshot library =
                await cache
                    .Get()
                    .ConfigureAwait(false);

            int own =
                library.Games.Count(
                    static game =>
                        game.Owned
                );

            int family =
                library.Games.Count(
                    static game =>
                        !game.Owned &&
                        game.FamilyShared
                );

            int excluded =
                library.Games.Count(
                    static game =>
                        !game.Owned &&
                        !game.Shareable
                );

            int unavailable =
                library.Games.Count(
                    static game =>
                        !game.Available
                );

            bot.ArchiLogger
                .LogGenericInfo(
                    "PlaytimeGoals read-only family probe: " +
                    $"group={(library.FamilyGroupId > 0 ? "yes" : "no")}, " +
                    $"members={library.FamilyMemberCount}, " +
                    $"own={own}, " +
                    $"family={family}, " +
                    $"excluded={excluded}, " +
                    $"unavailable={unavailable}, " +
                    $"runningKnown={library.RunningAppsKnown}"
                );

            if (
                !string.IsNullOrEmpty(
                    library.FamilyError
                )
            ) {
                bot.ArchiLogger
                    .LogGenericWarning(
                        "PlaytimeGoals family probe: " +
                        library.FamilyError
                    );
            }

            uint[] managed =
                Configs.TryGetValue(
                    bot.BotName,
                    out GoalConfig? config
                )
                    ? config.GameIds
                        .ToArray()
                    : Array.Empty<uint>();

            HashSet<uint> managedSet =
                managed.ToHashSet();

            uint[] managedFamily =
                library.Games
                    .Where(
                        game =>
                            managedSet.Contains(
                                game.AppId
                            ) &&
                            !game.Owned &&
                            game.FamilyShared
                    )
                    .Select(
                        static game =>
                            game.AppId
                    )
                    .OrderBy(
                        static appId =>
                            appId
                    )
                    .ToArray();

            if (
                managedFamily.Length > 0
            ) {
                bot.ArchiLogger
                    .LogGenericInfo(
                        "PlaytimeGoals managed Family AppIDs: " +
                        string.Join(
                            ',',
                            managedFamily
                        )
                    );
            }

            ParentalSnapshot parental =
                await handler
                    .GetParentalSnapshot(
                        bot.SteamID,
                        managed
                    )
                    .ConfigureAwait(false);

            int denied =
                parental.Apps.Count(
                    static app =>
                        !app.EffectiveAllowed
                );

            bot.ArchiLogger
                .LogGenericInfo(
                    "PlaytimeGoals read-only parental probe: " +
                    $"available={parental.Available}, " +
                    $"enabled={parental.Enabled}, " +
                    $"baseList={parental.BaseListId}, " +
                    $"baseEntries={parental.BaseEntryCount}, " +
                    $"customEntries={parental.CustomEntryCount}, " +
                    $"managedDenied={denied}"
                );

            if (
                !string.IsNullOrEmpty(
                    parental.Error
                )
            ) {
                bot.ArchiLogger
                    .LogGenericWarning(
                        "PlaytimeGoals parental probe: " +
                        parental.Error
                    );
            }
        } catch (Exception e) {
            bot.ArchiLogger
                .LogGenericWarning(
                    "PlaytimeGoals read-only probe failed: " +
                    e
                );
        }
    }

    internal static async Task<object>
        GetStatusResponse(
            string botName
        ) {
        if (
            string.IsNullOrWhiteSpace(
                botName
            )
        ) {
            return Failure(
                "Bot name is missing"
            );
        }

        if (
            (Bot.BotsReadOnly == null) ||
            !Bot.BotsReadOnly
                .TryGetValue(
                    botName,
                    out Bot? bot
                )
        ) {
            return Failure(
                $"Bot '{botName}' was not found"
            );
        }

        if (
            !Configs.TryGetValue(
                bot.BotName,
                out GoalConfig? config
            )
        ) {
            return Failure(
                "PlaytimeGoals config has not been initialized yet"
            );
        }

        GoalStatusSnapshot snapshot;

        if (
            Runners.TryGetValue(
                bot.BotName,
                out GoalRunner? runner
            )
        ) {
            snapshot =
                await runner
                    .GetStatus(false)
                    .ConfigureAwait(false);
        } else {
            List<GoalGameStatus> rows =
                new();

            foreach (
                (
                    uint appId,
                    double? targetHours
                )
                in config.Goals
            ) {
                rows.Add(
                    new GoalGameStatus(
                        appId,
                        string.Empty,
                        targetHours,
                        0,
                        0,
                        targetHours,
                        targetHours.HasValue
                            ? "unknown"
                            : "unset"
                    )
                );
            }

            snapshot =
                new GoalStatusSnapshot(
                    config.Enabled,
                    config.BatchSize,
                    bot.IsConnectedAndLoggedOn,
                    bot.CardsFarmer.NowFarming,
                    bot.CardsFarmer.Paused,
                    bot.IsPlayingPossible,
                    Policies.TryGetValue(
                        bot.BotName,
                        out ParentalPolicyService? policy
                    ) &&
                    policy.RecoveryReady,
                    Array.Empty<uint>(),
                    rows,
                    DateTime.MinValue
                );
        }

        return new {
            Success = true,
            Message = (string?) null,

            Result = new {
                Bot = bot.BotName,
                Enabled = snapshot.Enabled,

                ParentalWritesEnabled =
                    config.ParentalWritesEnabled,

                BatchSize =
                    snapshot.BatchSize,
                Connected =
                    snapshot.Connected,

                snapshot.Farming,
                snapshot.FarmerPaused,
                snapshot.PlayingPossible,
                snapshot.RecoveryReady,

                SnapshotUtc =
                    snapshot.SnapshotUtc ==
                    DateTime.MinValue
                        ? string.Empty
                        : snapshot
                            .SnapshotUtc
                            .ToString("O"),

                CurrentBatch =
                    snapshot.CurrentBatch
                        .Select(
                            static appId =>
                                (long) appId
                        )
                        .ToArray(),

                Games =
                    snapshot.Games
                        .Select(
                            static game =>
                                new {
                                    AppId =
                                        (long)
                                        game.AppId,

                                    game.Name,
                                    game.TargetHours,
                                    game.CurrentHours,
                                    game.EffectiveHours,
                                    game.RemainingHours,
                                    game.State,
                                    game.QueuePosition
                                }
                        )
                        .ToArray()
            }
        };
    }

    internal static async Task<object>
        GetLibraryResponse(
            string botName
        ) {
        if (
            string.IsNullOrWhiteSpace(
                botName
            )
        ) {
            return Failure(
                "Bot name is missing"
            );
        }

        if (
            (Bot.BotsReadOnly == null) ||
            !Bot.BotsReadOnly
                .TryGetValue(
                    botName,
                    out Bot? bot
                )
        ) {
            return Failure(
                $"Bot '{botName}' was not found"
            );
        }

        if (
            !LibraryCaches.TryGetValue(
                bot.BotName,
                out FamilyLibraryCache? cache
            )
        ) {
            return Failure(
                "PlaytimeGoals library cache is unavailable"
            );
        }

        FamilyLibrarySnapshot snapshot =
            await cache
                .Get()
                .ConfigureAwait(false);

        return new {
            Success = true,
            Message = (string?) null,

            Result = new {
                Bot = bot.BotName,

                FamilyGroupId =
                    snapshot
                        .FamilyGroupId
                        .ToString(CultureInfo.InvariantCulture),

                snapshot.FamilyGroupName,

                FamilyRole =
                    (long)
                    snapshot.FamilyRole,

                snapshot.FamilyMemberCount,
                snapshot.RunningAppsKnown,
                snapshot.FamilyError,

                LastFullRefreshUtc =
                    cache.LastFullRefreshUtc ==
                    DateTime.MinValue
                        ? string.Empty
                        : cache.LastFullRefreshUtc
                            .ToString("O"),

                OwnGames =
                    snapshot.Games.Count(
                        static game =>
                            game.Owned
                    ),

                FamilyGames =
                    snapshot.Games.Count(
                        static game =>
                            !game.Owned &&
                            game.FamilyShared
                    ),

                Games =
                    snapshot.Games
                        .Select(
                            static game =>
                                new {
                                    AppId =
                                        (long)
                                        game.AppId,

                                    game.Name,

                                    CurrentHours =
                                        Math.Round(
                                            game.PlaytimeForeverMinutes
                                            / 60d,
                                            2
                                        ),

                                    Source =
                                        game.Owned
                                            ? "own"
                                            : (
                                                game.FamilyShared &&
                                                game.Shareable
                                                    ? "family"
                                                    : "excluded"
                                            ),

                                    Owned =
                                        game.Owned,

                                    FamilyShared =
                                        game.FamilyShared,

                                    AlsoInFamily =
                                        game.Owned &&
                                        game.FamilyShared,

                                    CanSelect =
                                        game.Owned ||
                                        (
                                            game.FamilyShared &&
                                            game.Shareable
                                        ),

                                    game.Shareable,
                                    game.Available,
                                    game.FamilyAvailabilityKnown,
                                    game.FamilyCopies,
                                    game.FamilyCopiesInUse,
                                    game.ExcludeReason,

                                    OwnerSteamIds =
                                        game.OwnerSteamIds
                                            .Select(
                                                static steamId =>
                                                    steamId
                                                        .ToString(CultureInfo.InvariantCulture)
                                            )
                                            .ToArray()
                                }
                        )
                        .ToArray()
            }
        };
    }

    internal static async Task<object>
        GetParentalResponse(
            string botName
        ) {
        if (
            string.IsNullOrWhiteSpace(
                botName
            )
        ) {
            return Failure(
                "Bot name is missing"
            );
        }

        if (
            (Bot.BotsReadOnly == null) ||
            !Bot.BotsReadOnly
                .TryGetValue(
                    botName,
                    out Bot? bot
                )
        ) {
            return Failure(
                $"Bot '{botName}' was not found"
            );
        }

        if (
            !Handlers.TryGetValue(
                bot.BotName,
                out GoalHandler? handler
            )
        ) {
            return Failure(
                "PlaytimeGoals Steam handler is unavailable"
            );
        }

        uint[] managedAppIds =
            Configs.TryGetValue(
                bot.BotName,
                out GoalConfig? config
            )
                ? config.GameIds
                    .ToArray()
                : Array.Empty<uint>();

        ParentalSnapshot snapshot =
            await handler
                .GetParentalSnapshot(
                    bot.SteamID,
                    managedAppIds
                )
                .ConfigureAwait(false);

        return new {
            Success = true,
            Message = (string?) null,

            Result = new {
                Bot = bot.BotName,

                snapshot.Available,
                snapshot.Enabled,

                BaseListId =
                    (long)
                    snapshot.BaseListId,

                snapshot.BaseEntryCount,
                snapshot.CustomEntryCount,
                snapshot.Error,

                Apps =
                    snapshot.Apps
                        .Select(
                            static app =>
                                new {
                                    AppId =
                                        (long)
                                        app.AppId,

                                    app.BaseAllowed,
                                    app.CustomAllowed,
                                    app.EffectiveAllowed
                                }
                        )
                        .ToArray()
            }
        };
    }

    internal static async Task<object>
        RunParentalSelfTestResponse(
            string botName,
            uint appId
        ) {
        if (
            string.IsNullOrWhiteSpace(
                botName
            )
        ) {
            return Failure(
                "Bot name is missing"
            );
        }

        if (appId == 0) {
            return Failure(
                "AppID must be greater than zero"
            );
        }

        if (
            (Bot.BotsReadOnly == null) ||
            !Bot.BotsReadOnly
                .TryGetValue(
                    botName,
                    out Bot? bot
                )
        ) {
            return Failure(
                $"Bot '{botName}' was not found"
            );
        }

        if (
            !Configs.TryGetValue(
                bot.BotName,
                out GoalConfig? config
            )
        ) {
            return Failure(
                "PlaytimeGoals config has not been initialized yet"
            );
        }

        if (config.Enabled) {
            return Failure(
                "Disable PlaytimeGoals before running the parental self-test"
            );
        }

        if (!config.ParentalWritesEnabled) {
            return Failure(
                "Parental writes must be enabled for the self-test"
            );
        }

        if (
            !Policies.TryGetValue(
                bot.BotName,
                out ParentalPolicyService? policy
            )
        ) {
            return Failure(
                "PlaytimeGoals parental policy is unavailable"
            );
        }

        ParentalSelfTestResult result =
            await policy
                .SelfTest(
                    appId
                )
                .ConfigureAwait(false);

        return new {
            Success = result.Success,
            Message = result.Message,

            Result = new {
                AppId =
                    (long) result.AppId,

                result.BeforeCustomAllowed,
                result.BeforeEffectiveAllowed,
                result.DuringCustomAllowed,
                result.DuringEffectiveAllowed,
                result.AfterCustomAllowed,
                result.AfterEffectiveAllowed,
                result.JournalEmpty,
                RecoveryReady =
                    policy.RecoveryReady
            }
        };
    }

    private static object Failure(
        string message
    ) =>
        new {
            Success = false,
            Message = message,
            Result = (object?) null
        };
}

internal sealed class BotStateAdapter(
    Bot bot
) : IBotStateView {
    public bool Connected =>
        bot.IsConnectedAndLoggedOn;

    public bool Farming =>
        bot.CardsFarmer.NowFarming;

    public bool FarmerPaused =>
        bot.CardsFarmer.Paused;

    public bool PlayingPossible =>
        bot.IsPlayingPossible;
}

internal sealed class SteamActionsAdapter(
    Bot bot,
    GoalHandler handler,
    FamilyLibraryCache libraryCache,
    ParentalPolicyService parentalPolicy,
    GoalConfig config
) : IGoalSteamActions {
    public bool RecoveryReady =>
        parentalPolicy.RecoveryReady;

    public Task<bool> EnsureRecovery() =>
        parentalPolicy
            .RecoverOutstanding();

    public async Task ReleaseGames(
        bool clearSteamGames
    ) {
        if (clearSteamGames) {
            await handler
                .PlayGames(
                    Array.Empty<uint>()
                )
                .ConfigureAwait(false);
        }

        await parentalPolicy
            .SyncAllowed(
                Array.Empty<uint>()
            )
            .ConfigureAwait(false);
    }

    public async Task AssertGames(
        IReadOnlyCollection<uint> appIds
    ) {
        ArgumentNullException.ThrowIfNull(
            appIds
        );

        if (appIds.Count == 0) {
            throw new ArgumentException(
                "A managed GamesPlayed batch cannot be empty",
                nameof(appIds)
            );
        }

        if (!parentalPolicy.RecoveryReady) {
            throw new InvalidOperationException(
                "Family View recovery gate is closed"
            );
        }

        await parentalPolicy
            .SyncAllowed(
                appIds
            )
            .ConfigureAwait(false);

        await handler
            .PlayGames(
                appIds
            )
            .ConfigureAwait(false);
    }

    public async Task<
        IReadOnlyList<ManagedGameSnapshot>?
    > FetchManagedLibrary() {
        FamilyLibrarySnapshot snapshot =
            await libraryCache
                .Get()
                .ConfigureAwait(false);

        HashSet<uint> managed =
            config.GameIds
                .ToHashSet();

        FamilyLibraryGameSnapshot[] games =
            snapshot.Games
                .Where(
                    game =>
                        managed.Contains(
                            game.AppId
                        )
                )
                .ToArray();

        Dictionary<uint, bool> parentalAllowed =
            new();

        bool parentalUnavailable =
            false;

        if (
            !parentalPolicy.WritesEnabled &&
            games.Length > 0
        ) {
            ParentalSnapshot parental =
                await handler
                    .GetParentalSnapshot(
                        bot.SteamID,
                        games
                            .Select(
                                static game =>
                                    game.AppId
                            )
                            .ToArray()
                    )
                    .ConfigureAwait(false);

            parentalUnavailable =
                !parental.Available;

            if (
                parental.Available &&
                parental.Enabled
            ) {
                parentalAllowed =
                    parental.Apps
                        .ToDictionary(
                            static app =>
                                app.AppId,
                            static app =>
                                app.EffectiveAllowed
                        );
            }
        }

        return games
            .Select(
                game => {
                    bool runnable;
                    string blockState =
                        string.Empty;

                    if (game.Owned) {
                        /*
                         * OWN always wins over Steam Family metadata.
                         * We cannot choose a concrete Steam license in
                         * ClientGamesPlayed, but owned games never depend
                         * on family-copy availability in our scheduler.
                         */
                        runnable = true;
                    } else if (
                        !game.FamilyShared
                    ) {
                        runnable = false;
                        blockState =
                            "family-not-shareable";
                    } else if (
                        !game.Shareable
                    ) {
                        runnable = false;
                        blockState =
                            "family-not-shareable";
                    } else if (
                        !game.FamilyAvailabilityKnown
                    ) {
                        runnable = false;
                        blockState =
                            "family-availability-unknown";
                    } else if (
                        !game.Available
                    ) {
                        runnable = false;
                        blockState =
                            "family-copy-busy";
                    } else {
                        runnable = true;
                    }

                    if (
                        runnable &&
                        !parentalPolicy.WritesEnabled
                    ) {
                        bool allowed =
                            !parentalUnavailable &&
                            (
                                parentalAllowed.Count == 0 ||
                                !parentalAllowed.TryGetValue(
                                    game.AppId,
                                    out bool effective
                                ) ||
                                effective
                            );

                        if (!allowed) {
                            runnable = false;
                            blockState =
                                "parental-blocked";
                        }
                    }

                    return new ManagedGameSnapshot(
                        game.AppId,
                        game.Name,
                        game.PlaytimeForeverMinutes,
                        runnable,
                        blockState
                    );
                }
            )
            .ToArray();
    }
}
