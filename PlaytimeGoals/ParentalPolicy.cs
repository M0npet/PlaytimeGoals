using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using SteamKit2.Internal;

namespace PlaytimeGoals;

internal sealed record ParentalOriginalState(
    bool Exists,
    bool Allowed
);

internal sealed record ParentalSelfTestResult(
    bool Success,
    string Message,
    uint AppId,
    bool? BeforeCustomAllowed,
    bool BeforeEffectiveAllowed,
    bool? DuringCustomAllowed,
    bool DuringEffectiveAllowed,
    bool? AfterCustomAllowed,
    bool AfterEffectiveAllowed,
    bool JournalEmpty
);

internal sealed class ParentalStateJournal(
    string filePath
) {
    internal Dictionary<
        uint,
        ParentalOriginalState
    > Load() {
        Dictionary<
            uint,
            ParentalOriginalState
        > result = new();

        try {
            if (!File.Exists(filePath)) {
                return result;
            }

            string text =
                File.ReadAllTextAsync(
                    filePath
                ).GetAwaiter().GetResult();

            foreach (
                string rawLine
                in text.Split('\n')
            ) {
                string line =
                    rawLine.Trim();

                if (
                    line.Length == 0 ||
                    line.StartsWith('#')
                ) {
                    continue;
                }

                string[] parts =
                    line.Split(
                        ' ',
                        StringSplitOptions
                            .RemoveEmptyEntries
                    );

                if (
                    parts.Length != 2 ||
                    !uint.TryParse(
                        parts[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint appId
                    ) ||
                    appId == 0
                ) {
                    continue;
                }

                ParentalOriginalState? parsed =
                    parts[1] switch {
                        "allow" =>
                            new ParentalOriginalState(
                                true,
                                true
                            ),

                        "deny" =>
                            new ParentalOriginalState(
                                true,
                                false
                            ),

                        "absent" =>
                            new ParentalOriginalState(
                                false,
                                false
                            ),

                        _ => null
                    };

                if (parsed != null) {
                    result[appId] =
                        parsed;
                }
            }
        } catch {
            /*
             * Never destroy the running bot merely because
             * the recovery journal cannot be parsed.
             */
        }

        return result;
    }

    internal void Save(
        IReadOnlyDictionary<
            uint,
            ParentalOriginalState
        > entries
    ) {
        Directory.CreateDirectory(
            Path.GetDirectoryName(filePath)
            ?? "."
        );

        if (entries.Count == 0) {
            try {
                File.Delete(filePath);
            } catch {
                // Best effort.
            }

            return;
        }

        StringBuilder builder =
            new();

        builder.AppendLine(
            "# PlaytimeGoals parental journal v1"
        );

        foreach (
            (
                uint appId,
                ParentalOriginalState state
            )
            in entries.OrderBy(
                static pair =>
                    pair.Key
            )
        ) {
            builder
                .Append(
                    appId.ToString(
                        CultureInfo.InvariantCulture
                    )
                )
                .Append(' ')
                .Append(
                    state.Exists
                        ? (
                            state.Allowed
                                ? "allow"
                                : "deny"
                        )
                        : "absent"
                )
                .Append('\n');
        }

        string temporary =
            filePath + ".new";

        File.WriteAllTextAsync(
            temporary,
            builder.ToString()
        ).GetAwaiter().GetResult();

        File.Move(
            temporary,
            filePath,
            true
        );
    }
}

internal sealed class ParentalPolicyService : IDisposable {
    private readonly Bot bot;
    private readonly GoalHandler handler;
    private readonly ParentalStateJournal journal;
    private readonly Action<string> info;
    private readonly Action<string> warn;

    private readonly SemaphoreSlim sync =
        new(1, 1);

    private volatile bool recoveryReady;

    internal bool WritesEnabled {
        get;
    }

    internal bool RecoveryReady =>
        recoveryReady;

    internal ParentalPolicyService(
        Bot bot,
        GoalHandler handler,
        ParentalStateJournal journal,
        bool writesEnabled,
        Action<string> info,
        Action<string> warn
    ) {
        this.bot =
            bot ??
            throw new ArgumentNullException(
                nameof(bot)
            );

        this.handler =
            handler ??
            throw new ArgumentNullException(
                nameof(handler)
            );

        this.journal =
            journal ??
            throw new ArgumentNullException(
                nameof(journal)
            );

        WritesEnabled =
            writesEnabled;

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
    }

    internal async Task SyncAllowed(
        IReadOnlyCollection<uint> appIds
    ) {
        ArgumentNullException.ThrowIfNull(
            appIds
        );

        HashSet<uint> desired =
            appIds
                .Where(
                    static appId =>
                        appId > 0
                )
                .Distinct()
                .ToHashSet();

        await sync
            .WaitAsync()
            .ConfigureAwait(false);

        try {
            Dictionary<
                uint,
                ParentalOriginalState
            > state =
                journal.Load();

            if (
                desired.Count > 0 &&
                !recoveryReady
            ) {
                throw new InvalidOperationException(
                    "Family View recovery gate is not ready"
                );
            }

            uint[] stale =
                state.Keys
                    .Where(
                        appId =>
                            !desired.Contains(
                                appId
                            )
                    )
                    .ToArray();

            if (stale.Length > 0) {
                bool restored =
                    await RestoreLocked(
                        state,
                        stale
                    ).ConfigureAwait(false);

                if (!restored) {
                    recoveryReady = false;

                    throw new InvalidOperationException(
                        "Family View restore is still pending"
                    );
                }
            }

            if (desired.Count == 0) {
                recoveryReady = true;
                return;
            }

            if (!WritesEnabled) {
                return;
            }

            ParentalSettings? settings =
                await handler
                    .GetParentalSettingsRaw(
                        bot.SteamID
                    )
                    .ConfigureAwait(false);

            if (settings == null) {
                throw new InvalidOperationException(
                    "Steam parental settings are unavailable"
                );
            }

            if (!settings.is_enabled) {
                if (state.Count > 0) {
                    bool restored =
                        await RestoreLocked(
                            state,
                            state.Keys.ToArray()
                        ).ConfigureAwait(false);

                    if (!restored) {
                        recoveryReady = false;

                        throw new InvalidOperationException(
                            "Family View restore is still pending"
                        );
                    }
                }

                recoveryReady = true;
                return;
            }

            string? pin =
                bot.BotConfig
                    .SteamParentalCode;

            if (
                string.IsNullOrEmpty(
                    pin
                )
            ) {
                throw new InvalidOperationException(
                    "Steam parental PIN is unavailable in ASF memory"
                );
            }

            foreach (uint appId in desired) {
                if (
                    !state.ContainsKey(
                        appId
                    )
                ) {
                    ParentalApp? existing =
                        settings
                            .applist_custom
                            .LastOrDefault(
                                entry =>
                                    entry.appid ==
                                    appId
                            );

                    state[appId] =
                        existing == null
                            ? new ParentalOriginalState(
                                false,
                                false
                            )
                            : new ParentalOriginalState(
                                true,
                                existing.is_allowed
                            );
                }

                SetCustomState(
                    settings,
                    appId,
                    true
                );
            }

            /*
             * Journal FIRST, remote write SECOND.
             *
             * A failed or interrupted write is deliberately
             * treated as ambiguous. The recovery gate closes
             * until RecoverOutstanding() proves the original
             * Family View state has been restored.
             */
            journal.Save(state);
            recoveryReady = false;

            bool success =
                await handler
                    .SetParentalSettingsRaw(
                        bot.SteamID,
                        pin,
                        settings
                    )
                    .ConfigureAwait(false);

            if (!success) {
                throw new InvalidOperationException(
                    "Steam rejected SetParentalSettings"
                );
            }

            ParentalSettings? verified =
                await handler
                    .GetParentalSettingsRaw(
                        bot.SteamID
                    )
                    .ConfigureAwait(false);

            bool allowVerified =
                verified != null &&
                desired.All(
                    appId =>
                        verified
                            .applist_custom
                            .LastOrDefault(
                                entry =>
                                    entry.appid ==
                                    appId
                            )
                            ?.is_allowed ==
                        true
                );

            if (!allowVerified) {
                recoveryReady = false;

                throw new InvalidOperationException(
                    "Family View temporary allow could not be verified"
                );
            }

            recoveryReady = true;

            info(
                "Family View temporarily allowed: " +
                string.Join(
                    ',',
                    desired.OrderBy(
                        static appId =>
                            appId
                    )
                )
            );
        } catch (Exception e) {
            warn(
                "parental policy sync failed: " +
                e.Message
            );

            throw;
        } finally {
            sync.Release();
        }
    }

    internal async Task<bool> RecoverOutstanding() {
        await sync
            .WaitAsync()
            .ConfigureAwait(false);

        try {
            Dictionary<
                uint,
                ParentalOriginalState
            > state =
                journal.Load();

            if (state.Count == 0) {
                recoveryReady = true;
                return true;
            }

            recoveryReady = false;

            bool restored =
                await RestoreLocked(
                    state,
                    state.Keys.ToArray()
                ).ConfigureAwait(false);

            recoveryReady =
                restored &&
                state.Count == 0;

            if (recoveryReady) {
                info(
                    "recovered Family View journal"
                );
            }

            return recoveryReady;
        } catch (Exception e) {
            recoveryReady = false;

            warn(
                "parental journal recovery failed: " +
                e.Message
            );

            return false;
        } finally {
            sync.Release();
        }
    }

    internal async Task<ParentalSelfTestResult>
        SelfTest(
            uint appId
        ) {
        ArgumentOutOfRangeException.ThrowIfZero(
            appId
        );

        if (!WritesEnabled) {
            return new ParentalSelfTestResult(
                false,
                "Parental writes are disabled",
                appId,
                null,
                false,
                null,
                false,
                null,
                false,
                false
            );
        }

        if (!RecoveryReady) {
            bool recovered =
                await RecoverOutstanding()
                    .ConfigureAwait(false);

            if (!recovered) {
                return new ParentalSelfTestResult(
                    false,
                    "Outstanding Family View journal could not be recovered",
                    appId,
                    null,
                    false,
                    null,
                    false,
                    null,
                    false,
                    false
                );
            }
        }

        ParentalSnapshot before =
            await handler
                .GetParentalSnapshot(
                    bot.SteamID,
                    new[] { appId }
                )
                .ConfigureAwait(false);

        ParentalAppProbe? beforeApp =
            before.Apps
                .FirstOrDefault(
                    app =>
                        app.AppId == appId
                );

        if (
            !before.Available ||
            !before.Enabled ||
            beforeApp == null
        ) {
            return new ParentalSelfTestResult(
                false,
                before.Error ??
                    "Steam Family View is unavailable or disabled",
                appId,
                beforeApp?.CustomAllowed,
                beforeApp?.EffectiveAllowed ?? false,
                null,
                false,
                null,
                false,
                journal.Load().Count == 0
            );
        }

        ParentalSnapshot? during = null;
        Exception? operationError = null;

        try {
            await SyncAllowed(
                    new[] { appId }
                )
                .ConfigureAwait(false);

            during =
                await handler
                    .GetParentalSnapshot(
                        bot.SteamID,
                        new[] { appId }
                    )
                    .ConfigureAwait(false);
        } catch (Exception e) {
            operationError = e;
        }

        try {
            await SyncAllowed(
                    Array.Empty<uint>()
                )
                .ConfigureAwait(false);
        } catch (Exception e) {
            operationError ??= e;
        }

        ParentalSnapshot after =
            await handler
                .GetParentalSnapshot(
                    bot.SteamID,
                    new[] { appId }
                )
                .ConfigureAwait(false);

        ParentalAppProbe? duringApp =
            during?.Apps
                .FirstOrDefault(
                    app =>
                        app.AppId == appId
                );

        ParentalAppProbe? afterApp =
            after.Apps
                .FirstOrDefault(
                    app =>
                        app.AppId == appId
                );

        bool journalEmpty =
            journal.Load().Count == 0;

        bool exactRestore =
            after.Available &&
            afterApp != null &&
            (
                beforeApp.CustomAllowed ==
                afterApp.CustomAllowed
            ) &&
            (
                beforeApp.EffectiveAllowed ==
                afterApp.EffectiveAllowed
            );

        bool temporaryAllow =
            during?.Available == true &&
            duringApp != null &&
            duringApp.EffectiveAllowed &&
            duringApp.CustomAllowed == true;

        bool success =
            operationError == null &&
            temporaryAllow &&
            exactRestore &&
            journalEmpty &&
            RecoveryReady;

        string message =
            success
                ? "temporary allow and exact restore verified"
                : operationError?.Message ??
                  (
                      !temporaryAllow
                          ? "temporary allow verification failed"
                          : !exactRestore
                              ? "exact Family View restore verification failed"
                              : !journalEmpty
                                  ? "parental journal is not empty"
                                  : "recovery gate is not ready"
                  );

        return new ParentalSelfTestResult(
            success,
            message,
            appId,
            beforeApp.CustomAllowed,
            beforeApp.EffectiveAllowed,
            duringApp?.CustomAllowed,
            duringApp?.EffectiveAllowed ?? false,
            afterApp?.CustomAllowed,
            afterApp?.EffectiveAllowed ?? false,
            journalEmpty
        );
    }

    internal IReadOnlyDictionary<
        uint,
        ParentalOriginalState
    > GetJournalSnapshot() =>
        journal.Load();

    private async Task<bool> RestoreLocked(
        Dictionary<
            uint,
            ParentalOriginalState
        > state,
        uint[] appIds
    ) {
        if (appIds.Length == 0) {
            return true;
        }

        ParentalSettings? settings =
            await handler
                .GetParentalSettingsRaw(
                    bot.SteamID
                )
                .ConfigureAwait(false);

        if (settings == null) {
            recoveryReady = false;

            warn(
                "cannot restore Family View: " +
                "settings unavailable"
            );

            return false;
        }

        string? pin =
            bot.BotConfig
                .SteamParentalCode;

        if (
            string.IsNullOrEmpty(
                pin
            )
        ) {
            recoveryReady = false;

            warn(
                "cannot restore Family View: " +
                "parental PIN unavailable"
            );

            return false;
        }

        foreach (uint appId in appIds) {
            if (
                !state.TryGetValue(
                    appId,
                    out ParentalOriginalState? original
                )
            ) {
                continue;
            }

            settings.applist_custom.RemoveAll(
                entry =>
                    entry.appid == appId
            );

            if (original.Exists) {
                settings.applist_custom.Add(
                    new ParentalApp {
                        appid = appId,
                        is_allowed =
                            original.Allowed
                    }
                );
            }
        }

        bool success =
            await handler
                .SetParentalSettingsRaw(
                    bot.SteamID,
                    pin,
                    settings
                )
                .ConfigureAwait(false);

        if (!success) {
            recoveryReady = false;

            warn(
                "Steam rejected Family View restore"
            );

            return false;
        }

        ParentalSettings? verified =
            await handler
                .GetParentalSettingsRaw(
                    bot.SteamID
                )
                .ConfigureAwait(false);

        if (verified == null) {
            recoveryReady = false;

            warn(
                "Family View restore could not be verified"
            );

            return false;
        }

        foreach (uint appId in appIds) {
            if (
                !state.TryGetValue(
                    appId,
                    out ParentalOriginalState? original
                )
            ) {
                continue;
            }

            ParentalApp? current =
                verified
                    .applist_custom
                    .LastOrDefault(
                        entry =>
                            entry.appid ==
                            appId
                    );

            bool exact =
                original.Exists
                    ? (
                        current != null &&
                        current.is_allowed ==
                            original.Allowed
                    )
                    : current == null;

            if (!exact) {
                recoveryReady = false;

                warn(
                    "Family View restore verification failed for AppID " +
                    appId.ToString(
                        CultureInfo.InvariantCulture
                    )
                );

                return false;
            }
        }

        foreach (uint appId in appIds) {
            state.Remove(appId);
        }

        journal.Save(state);
        recoveryReady = true;

        info(
            "Family View restored: " +
            string.Join(
                ',',
                appIds.OrderBy(
                    static appId =>
                        appId
                )
            )
        );

        return true;
    }

    public void Dispose() {
        sync.Dispose();
    }

    private static void SetCustomState(
        ParentalSettings settings,
        uint appId,
        bool allowed
    ) {
        settings.applist_custom.RemoveAll(
            entry =>
                entry.appid == appId
        );

        settings.applist_custom.Add(
            new ParentalApp {
                appid = appId,
                is_allowed = allowed
            }
        );
    }
}
