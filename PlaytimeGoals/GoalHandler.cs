using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using SteamKit2;
using SteamKit2.Internal;

namespace PlaytimeGoals;

internal sealed record GameSnapshot(
    uint AppId,
    string Name,
    uint PlaytimeForeverMinutes
);

internal sealed record FamilyLibraryGameSnapshot(
    uint AppId,
    string Name,
    uint PlaytimeForeverMinutes,
    bool Owned,
    bool FamilyShared,
    bool Shareable,
    bool Available,
    bool FamilyAvailabilityKnown,
    int FamilyCopies,
    int FamilyCopiesInUse,
    int ExcludeReason,
    IReadOnlyList<ulong> OwnerSteamIds
);

internal sealed record FamilyLibrarySnapshot(
    ulong FamilyGroupId,
    string FamilyGroupName,
    uint FamilyRole,
    int FamilyMemberCount,
    bool RunningAppsKnown,
    string? FamilyError,
    IReadOnlyList<FamilyLibraryGameSnapshot> Games
);

internal sealed record ParentalAppProbe(
    uint AppId,
    bool? BaseAllowed,
    bool? CustomAllowed,
    bool EffectiveAllowed
);

internal sealed record ParentalSnapshot(
    bool Available,
    bool Enabled,
    uint BaseListId,
    int BaseEntryCount,
    int CustomEntryCount,
    string? Error,
    IReadOnlyList<ParentalAppProbe> Apps
);

internal sealed record FamilyRunningUse(
    ulong MemberSteamId,
    ulong OwnerSteamId
);

internal sealed class GoalHandler : ClientMsgHandler {
    private readonly object serviceLock = new();
    private readonly object runningLock = new();

    private Player? unifiedPlayerService;
    private FamilyGroups? unifiedFamilyService;
    private Parental? unifiedParentalService;

    private readonly Dictionary<
        uint,
        IReadOnlyList<FamilyRunningUse>
    > runningUsesByApp = new();

    private bool runningAppsKnown;

    internal Action? FamilyRunningAppsChanged {
        get;
        set;
    }

    public override void HandleMsg(IPacketMsg packetMsg) { }

    internal Task PlayGames(
        IReadOnlyCollection<uint> gameIds
    ) {
        ArgumentNullException.ThrowIfNull(gameIds);

        if (
            (Client == null) ||
            !Client.IsConnected
        ) {
            return Task.CompletedTask;
        }

        ClientMsgProtobuf<CMsgClientGamesPlayed> request =
            new(EMsg.ClientGamesPlayedWithDataBlob);

        foreach (
            uint gameId in gameIds
                .Distinct()
                .Where(static id => id > 0)
                .Take(GoalConfig.MaxBatchSize)
        ) {
            request.Body.games_played.Add(
                new CMsgClientGamesPlayed.GamePlayed {
                    game_id = new GameID(gameId)
                }
            );
        }

        Client.Send(request);

        return Task.CompletedTask;
    }

    internal void UpdateFamilyRunningApps(
        CFamilyGroupsClient_NotifyRunningApps_Notification
            notification
    ) {
        ArgumentNullException.ThrowIfNull(notification);

        Dictionary<
            uint,
            IReadOnlyList<FamilyRunningUse>
        > next = new();

        foreach (
            CFamilyGroupsClient_NotifyRunningApps_Notification
                .RunningApp app
            in notification.running_apps
        ) {
            if (app.appid == 0) {
                continue;
            }

            FamilyRunningUse[] uses =
                app.playing_members
                    .Where(
                        static member =>
                            member.member_steamid > 0 &&
                            member.owner_steamid > 0
                    )
                    .Select(
                        static member =>
                            new FamilyRunningUse(
                                member.member_steamid,
                                member.owner_steamid
                            )
                    )
                    .Distinct()
                    .OrderBy(
                        static use =>
                            use.OwnerSteamId
                    )
                    .ThenBy(
                        static use =>
                            use.MemberSteamId
                    )
                    .ToArray();

            next[app.appid] = uses;
        }

        bool changed;

        lock (runningLock) {
            changed =
                !runningAppsKnown ||
                !RunningUseMapsEqual(
                    runningUsesByApp,
                    next
                );

            runningUsesByApp.Clear();

            foreach (
                (
                    uint appId,
                    IReadOnlyList<FamilyRunningUse> uses
                )
                in next
            ) {
                runningUsesByApp[appId] =
                    uses;
            }

            runningAppsKnown = true;
        }

        if (changed) {
            FamilyRunningAppsChanged?.Invoke();
        }
    }

    internal async Task<
        IReadOnlyList<GameSnapshot>?
    > GetOwnedGames(
        ulong steamId
    ) {
        if (
            (steamId == 0) ||
            (Client == null) ||
            !Client.IsConnected
        ) {
            return null;
        }

        Player? playerService =
            GetPlayerService();

        if (playerService == null) {
            return null;
        }

        CPlayer_GetOwnedGames_Request request =
            new() {
                steamid = steamId,
                include_appinfo = true,
                include_free_sub = true,
                include_played_free_games = true,
                skip_unvetted_apps = false
            };

        SteamUnifiedMessages
            .ServiceMethodResponse<
                CPlayer_GetOwnedGames_Response
            > response;

        try {
            response =
                await playerService
                    .GetOwnedGames(request)
                    .ToLongRunningTask()
                    .ConfigureAwait(false);
        } catch (Exception) {
            return null;
        }

        if (response.Result != EResult.OK) {
            return null;
        }

        return response.Body.games
            .Where(
                static game =>
                    game.appid > 0
            )
            .Select(
                static game =>
                    new GameSnapshot(
                        (uint) game.appid,
                        game.name ?? string.Empty,
                        (uint) Math.Max(
                            0,
                            game.playtime_forever
                        )
                    )
            )
            .ToList();
    }

    internal async Task<
        FamilyLibrarySnapshot
    > GetFamilyLibrary(
        ulong steamId
    ) {
        IReadOnlyList<GameSnapshot> owned =
            await GetOwnedGames(steamId)
                .ConfigureAwait(false)
            ?? Array.Empty<GameSnapshot>();

        Dictionary<uint, GameSnapshot>
            ownedByApp =
                owned
                    .GroupBy(
                        static game =>
                            game.AppId
                    )
                    .ToDictionary(
                        static group =>
                            group.Key,
                        static group =>
                            group
                                .OrderByDescending(
                                    static game =>
                                        game.PlaytimeForeverMinutes
                                )
                                .First()
                    );

        FamilyGroups? familyService =
            GetFamilyService();

        if (
            (steamId == 0) ||
            (familyService == null) ||
            (Client == null) ||
            !Client.IsConnected
        ) {
            return OwnOnlySnapshot(
                ownedByApp.Values,
                "FamilyGroups service unavailable"
            );
        }

        SteamUnifiedMessages
            .ServiceMethodResponse<
                CFamilyGroups_GetFamilyGroupForUser_Response
            > membership;

        try {
            membership =
                await familyService
                    .GetFamilyGroupForUser(
                        new CFamilyGroups_GetFamilyGroupForUser_Request {
                            steamid = steamId,
                            include_family_group_response = true
                        }
                    )
                    .ToLongRunningTask()
                    .ConfigureAwait(false);
        } catch (Exception e) {
            return OwnOnlySnapshot(
                ownedByApp.Values,
                "GetFamilyGroupForUser failed: " +
                e.GetType().Name
            );
        }

        if (
            membership.Result != EResult.OK
        ) {
            return OwnOnlySnapshot(
                ownedByApp.Values,
                "GetFamilyGroupForUser: " +
                membership.Result
            );
        }

        if (
            membership.Body.is_not_member_of_any_group ||
            (membership.Body.family_groupid == 0)
        ) {
            return OwnOnlySnapshot(
                ownedByApp.Values,
                null
            );
        }

        ulong familyGroupId =
            membership.Body.family_groupid;

        CFamilyGroups_GetFamilyGroup_Response?
            group =
                membership.Body.family_group;

        /*
         * send_running_apps asks Steam to also send the
         * FamilyGroupsClient.NotifyRunningApps notification.
         *
         * Failure here is not fatal for library discovery.
         */
        try {
            SteamUnifiedMessages
                .ServiceMethodResponse<
                    CFamilyGroups_GetFamilyGroup_Response
                > groupResponse =
                    await familyService
                        .GetFamilyGroup(
                            new CFamilyGroups_GetFamilyGroup_Request {
                                family_groupid = familyGroupId,
                                send_running_apps = true
                            }
                        )
                        .ToLongRunningTask()
                        .ConfigureAwait(false);

            if (
                groupResponse.Result ==
                EResult.OK
            ) {
                group =
                    groupResponse.Body;
            }
        } catch (Exception) {
            // Advisory runtime state only.
        }

        SteamUnifiedMessages
            .ServiceMethodResponse<
                CFamilyGroups_GetSharedLibraryApps_Response
            > sharedResponse;

        try {
            sharedResponse =
                await familyService
                    .GetSharedLibraryApps(
                        new CFamilyGroups_GetSharedLibraryApps_Request {
                            family_groupid = familyGroupId,
                            include_own = true,
                            include_excluded = true,
                            include_non_games = false,
                            steamid = steamId,
                            language = "english"
                        }
                    )
                    .ToLongRunningTask()
                    .ConfigureAwait(false);
        } catch (Exception e) {
            return OwnOnlySnapshot(
                ownedByApp.Values,
                "GetSharedLibraryApps failed: " +
                e.GetType().Name,
                familyGroupId,
                group?.name ?? string.Empty,
                membership.Body.role,
                group?.members.Count ?? 0
            );
        }

        if (
            sharedResponse.Result !=
            EResult.OK
        ) {
            return OwnOnlySnapshot(
                ownedByApp.Values,
                "GetSharedLibraryApps: " +
                sharedResponse.Result,
                familyGroupId,
                group?.name ?? string.Empty,
                membership.Body.role,
                group?.members.Count ?? 0
            );
        }

        Dictionary<uint, uint>
            familyPlaytimeMinutes = new();

        try {
            SteamUnifiedMessages
                .ServiceMethodResponse<
                    CFamilyGroups_GetPlaytimeSummary_Response
                > playtimeResponse =
                    await familyService
                        .GetPlaytimeSummary(
                            new CFamilyGroups_GetPlaytimeSummary_Request {
                                family_groupid =
                                    familyGroupId
                            }
                        )
                        .ToLongRunningTask()
                        .ConfigureAwait(false);

            if (
                playtimeResponse.Result ==
                EResult.OK
            ) {
                familyPlaytimeMinutes =
                    playtimeResponse.Body.entries
                        .Where(
                            entry =>
                                (entry.steamid == steamId) &&
                                (entry.appid > 0)
                        )
                        .GroupBy(
                            static entry =>
                                entry.appid
                        )
                        .ToDictionary(
                            static group =>
                                group.Key,
                            static group => {
                                ulong maxSeconds =
                                    group.Max(
                                        static entry =>
                                            (ulong)
                                            entry.seconds_played
                                    );

                                return (uint)
                                    Math.Min(
                                        uint.MaxValue,
                                        maxSeconds / 60UL
                                    );
                            }
                        );
            }
        } catch (Exception) {
            /*
             * Owned games still have authoritative
             * GetOwnedGames playtime.
             */
        }

        (
            bool availabilityKnown,
            Dictionary<
                uint,
                IReadOnlyList<FamilyRunningUse>
            > runningUses
        ) = GetRunningUsesSnapshot();

        Dictionary<
            uint,
            FamilyLibraryGameSnapshot
        > merged = new();

        foreach (
            CFamilyGroups_GetSharedLibraryApps_Response
                .SharedApp shared
            in sharedResponse.Body.apps
        ) {
            if (shared.appid == 0) {
                continue;
            }

            uint appId =
                shared.appid;

            ownedByApp.TryGetValue(
                appId,
                out GameSnapshot? own
            );

            ulong[] owners =
                shared.owner_steamids
                    .Where(
                        static owner =>
                            owner > 0
                    )
                    .Distinct()
                    .OrderBy(
                        static owner =>
                            owner
                    )
                    .ToArray();

            bool familyShared =
                owners.Any(
                    owner =>
                        owner != steamId
                );

            bool shareable =
                shared.exclude_reason ==
                ESharedLibraryExcludeReason
                    .k_ESharedLibrary_Included;

            int copiesInUse =
                CountCopiesInUse(
                    appId,
                    owners,
                    steamId,
                    runningUses
                );

            bool hasFreeFamilyCopy =
                (owners.Length > 0) &&
                availabilityKnown &&
                (copiesInUse < owners.Length);

            bool available =
                (own != null) ||
                (
                    shareable &&
                    hasFreeFamilyCopy
                );

            uint playtimeMinutes =
                own?.PlaytimeForeverMinutes
                ??
                (
                    familyPlaytimeMinutes
                        .TryGetValue(
                            appId,
                            out uint familyMinutes
                        )
                        ? familyMinutes
                        : 0
                );

            string name =
                !string.IsNullOrWhiteSpace(
                    shared.name
                )
                    ? shared.name
                    : own?.Name ??
                      string.Empty;

            merged[appId] =
                new FamilyLibraryGameSnapshot(
                    appId,
                    name,
                    playtimeMinutes,
                    own != null,
                    familyShared,
                    shareable,
                    available,
                    availabilityKnown,
                    owners.Length,
                    copiesInUse,
                    (int) shared.exclude_reason,
                    owners
                );
        }

        foreach (
            GameSnapshot own
            in ownedByApp.Values
        ) {
            if (
                merged.ContainsKey(
                    own.AppId
                )
            ) {
                continue;
            }

            merged[own.AppId] =
                new FamilyLibraryGameSnapshot(
                    own.AppId,
                    own.Name,
                    own.PlaytimeForeverMinutes,
                    true,
                    false,
                    true,
                    true,
                    availabilityKnown,
                    0,
                    0,
                    0,
                    new[] { steamId }
                );
        }

        return new FamilyLibrarySnapshot(
            familyGroupId,
            group?.name ?? string.Empty,
            membership.Body.role,
            group?.members.Count ?? 0,
            availabilityKnown,
            null,
            merged.Values
                .OrderBy(
                    static game =>
                        game.AppId
                )
                .ToArray()
        );
    }

    internal async Task<
        ParentalSnapshot
    > GetParentalSnapshot(
        ulong steamId,
        IReadOnlyCollection<uint> appIds
    ) {
        ArgumentNullException.ThrowIfNull(
            appIds
        );

        Parental? parentalService =
            GetParentalService();

        if (
            (steamId == 0) ||
            (parentalService == null) ||
            (Client == null) ||
            !Client.IsConnected
        ) {
            return new ParentalSnapshot(
                false,
                false,
                0,
                0,
                0,
                "Parental service unavailable",
                Array.Empty<ParentalAppProbe>()
            );
        }

        SteamUnifiedMessages
            .ServiceMethodResponse<
                CParental_GetParentalSettings_Response
            > response;

        try {
            response =
                await parentalService
                    .GetParentalSettings(
                        new CParental_GetParentalSettings_Request {
                            steamid = steamId
                        }
                    )
                    .ToLongRunningTask()
                    .ConfigureAwait(false);
        } catch (Exception e) {
            return new ParentalSnapshot(
                false,
                false,
                0,
                0,
                0,
                "GetParentalSettings failed: " +
                e.GetType().Name,
                Array.Empty<ParentalAppProbe>()
            );
        }

        if (
            (response.Result != EResult.OK) ||
            (response.Body.settings == null)
        ) {
            return new ParentalSnapshot(
                false,
                false,
                0,
                0,
                0,
                "GetParentalSettings: " +
                response.Result,
                Array.Empty<ParentalAppProbe>()
            );
        }

        ParentalSettings settings =
            response.Body.settings;

        Dictionary<uint, bool> baseMap =
            settings.applist_base
                .Where(
                    static app =>
                        app.appid > 0
                )
                .GroupBy(
                    static app =>
                        app.appid
                )
                .ToDictionary(
                    static group =>
                        group.Key,
                    static group =>
                        group.Last().is_allowed
                );

        Dictionary<uint, bool> customMap =
            settings.applist_custom
                .Where(
                    static app =>
                        app.appid > 0
                )
                .GroupBy(
                    static app =>
                        app.appid
                )
                .ToDictionary(
                    static group =>
                        group.Key,
                    static group =>
                        group.Last().is_allowed
                );

        /*
         * Steam UI uses base list 0 = all games,
         * base list 1 = no games.
         */
        bool defaultAllowed =
            settings.applist_base_id == 0;

        ParentalAppProbe[] probes =
            appIds
                .Where(
                    static appId =>
                        appId > 0
                )
                .Distinct()
                .OrderBy(
                    static appId =>
                        appId
                )
                .Select(
                    appId => {
                        bool? baseAllowed =
                            baseMap.TryGetValue(
                                appId,
                                out bool baseValue
                            )
                                ? baseValue
                                : null;

                        bool? customAllowed =
                            customMap.TryGetValue(
                                appId,
                                out bool customValue
                            )
                                ? customValue
                                : null;

                        bool effectiveAllowed =
                            customAllowed
                            ??
                            baseAllowed
                            ??
                            defaultAllowed;

                        return new ParentalAppProbe(
                            appId,
                            baseAllowed,
                            customAllowed,
                            effectiveAllowed
                        );
                    }
                )
                .ToArray();

        return new ParentalSnapshot(
            true,
            settings.is_enabled,
            settings.applist_base_id,
            settings.applist_base.Count,
            settings.applist_custom.Count,
            null,
            probes
        );
    }


    internal async Task<ParentalSettings?>
        GetParentalSettingsRaw(
            ulong steamId
        ) {
        Parental? parentalService =
            GetParentalService();

        if (
            steamId == 0 ||
            parentalService == null ||
            Client == null ||
            !Client.IsConnected
        ) {
            return null;
        }

        try {
            SteamUnifiedMessages
                .ServiceMethodResponse<
                    CParental_GetParentalSettings_Response
                > response =
                    await parentalService
                        .GetParentalSettings(
                            new CParental_GetParentalSettings_Request {
                                steamid = steamId
                            }
                        )
                        .ToLongRunningTask()
                        .ConfigureAwait(false);

            return response.Result == EResult.OK
                ? response.Body.settings
                : null;
        } catch (Exception) {
            return null;
        }
    }

    internal async Task<bool>
        SetParentalSettingsRaw(
            ulong steamId,
            string password,
            ParentalSettings settings
        ) {
        ArgumentException.ThrowIfNullOrEmpty(
            password
        );

        ArgumentNullException.ThrowIfNull(
            settings
        );

        Parental? parentalService =
            GetParentalService();

        if (
            steamId == 0 ||
            parentalService == null ||
            Client == null ||
            !Client.IsConnected
        ) {
            return false;
        }

        try {
            SteamUnifiedMessages
                .ServiceMethodResponse<
                    CParental_SetParentalSettings_Response
                > response =
                    await parentalService
                        .SetParentalSettings(
                            new CParental_SetParentalSettings_Request {
                                steamid = steamId,
                                password = password,
                                settings = settings
                            }
                        )
                        .ToLongRunningTask()
                        .ConfigureAwait(false);

            return response.Result ==
                EResult.OK;
        } catch (Exception) {
            return false;
        }
    }

    private Player? GetPlayerService() {
        lock (serviceLock) {
            return unifiedPlayerService ??=
                Client
                    ?.GetHandler<
                        SteamUnifiedMessages
                    >()
                    ?.CreateService<Player>();
        }
    }

    private FamilyGroups?
        GetFamilyService() {
        lock (serviceLock) {
            return unifiedFamilyService ??=
                Client
                    ?.GetHandler<
                        SteamUnifiedMessages
                    >()
                    ?.CreateService<
                        FamilyGroups
                    >();
        }
    }

    private Parental?
        GetParentalService() {
        lock (serviceLock) {
            return unifiedParentalService ??=
                Client
                    ?.GetHandler<
                        SteamUnifiedMessages
                    >()
                    ?.CreateService<
                        Parental
                    >();
        }
    }

    internal FamilyLibrarySnapshot
        ApplyRuntimeAvailability(
            FamilyLibrarySnapshot snapshot,
            ulong steamId
        ) {
        ArgumentNullException.ThrowIfNull(snapshot);

        (
            bool known,
            Dictionary<
                uint,
                IReadOnlyList<FamilyRunningUse>
            > runningUses
        ) = GetRunningUsesSnapshot();

        FamilyLibraryGameSnapshot[] games =
            snapshot.Games
                .Select(
                    game => {
                        int copiesInUse =
                            CountCopiesInUse(
                                game.AppId,
                                game.OwnerSteamIds,
                                steamId,
                                runningUses
                            );

                        bool available =
                            game.Owned ||
                            (
                                game.FamilyShared &&
                                game.Shareable &&
                                known &&
                                game.FamilyCopies > 0 &&
                                copiesInUse < game.FamilyCopies
                            );

                        return game with {
                            Available = available,
                            FamilyAvailabilityKnown = known,
                            FamilyCopiesInUse = copiesInUse
                        };
                    }
                )
                .ToArray();

        return snapshot with {
            RunningAppsKnown = known,
            Games = games
        };
    }

    private (
        bool Known,
        Dictionary<
            uint,
            IReadOnlyList<FamilyRunningUse>
        > Uses
    ) GetRunningUsesSnapshot() {
        lock (runningLock) {
            return (
                runningAppsKnown,
                runningUsesByApp
                    .ToDictionary(
                        static pair =>
                            pair.Key,
                        static pair =>
                            (IReadOnlyList<FamilyRunningUse>)
                            pair.Value.ToArray()
                    )
            );
        }
    }

    private static int CountCopiesInUse(
        uint appId,
        IReadOnlyCollection<ulong> owners,
        ulong selfSteamId,
        Dictionary<
            uint,
            IReadOnlyList<FamilyRunningUse>
        > runningUses
    ) {
        if (
            owners.Count == 0 ||
            !runningUses.TryGetValue(
                appId,
                out IReadOnlyList<FamilyRunningUse>? uses
            )
        ) {
            return 0;
        }

        HashSet<ulong> ownerSet =
            owners.ToHashSet();

        return uses
            .Where(
                use =>
                    use.MemberSteamId != selfSteamId &&
                    ownerSet.Contains(
                        use.OwnerSteamId
                    )
            )
            .Select(
                static use =>
                    use.OwnerSteamId
            )
            .Distinct()
            .Count();
    }

    private static bool RunningUseMapsEqual(
        Dictionary<
            uint,
            IReadOnlyList<FamilyRunningUse>
        > left,
        Dictionary<
            uint,
            IReadOnlyList<FamilyRunningUse>
        > right
    ) {
        if (left.Count != right.Count) {
            return false;
        }

        foreach (
            (
                uint appId,
                IReadOnlyList<FamilyRunningUse> leftUses
            )
            in left
        ) {
            if (
                !right.TryGetValue(
                    appId,
                    out IReadOnlyList<FamilyRunningUse>? rightUses
                ) ||
                !leftUses.SequenceEqual(
                    rightUses
                )
            ) {
                return false;
            }
        }

        return true;
    }

    private static FamilyLibrarySnapshot
        OwnOnlySnapshot(
            IEnumerable<GameSnapshot> owned,
            string? error,
            ulong familyGroupId = 0,
            string familyGroupName = "",
            uint familyRole = 0,
            int familyMemberCount = 0
        ) {
        FamilyLibraryGameSnapshot[] games =
            owned
                .OrderBy(
                    static game =>
                        game.AppId
                )
                .Select(
                    game =>
                        new FamilyLibraryGameSnapshot(
                            game.AppId,
                            game.Name,
                            game.PlaytimeForeverMinutes,
                            true,
                            false,
                            true,
                            true,
                            false,
                            0,
                            0,
                            0,
                            Array.Empty<ulong>()
                        )
                )
                .ToArray();

        return new FamilyLibrarySnapshot(
            familyGroupId,
            familyGroupName,
            familyRole,
            familyMemberCount,
            false,
            error,
            games
        );
    }
}
