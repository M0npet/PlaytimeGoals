using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace PlaytimeGoals;

internal sealed class GoalConfig {
    internal const byte DefaultBatchSize = 5;
    internal const byte MaxBatchSize = 32;

    internal bool Enabled { get; init; }
    internal bool ParentalWritesEnabled { get; init; }

    internal byte BatchSize { get; init; } =
        DefaultBatchSize;

    /*
     * PlaytimeGoals keys are the ONLY managed AppIDs.
     *
     * Native ASF GamesPlayedWhileIdle must be empty whenever
     * PlaytimeGoals is enabled, otherwise two independent
     * components would compete for GamesPlayed state.
     */
    internal IReadOnlyList<uint> GameIds { get; init; } =
        Array.Empty<uint>();

    /*
     * number = finite target in hours
     * null   = unlimited idling
     */
    internal IReadOnlyDictionary<uint, double?> Goals {
        get;
        init;
    } = new Dictionary<uint, double?>();

    internal double? GetTargetHours(
        uint appId
    ) =>
        Goals.TryGetValue(
            appId,
            out double? value
        )
            ? value
            : null;

    internal static uint TargetMinutes(
        double hours
    ) {
        double minutes =
            Math.Ceiling(hours * 60d);

        return minutes >= uint.MaxValue
            ? uint.MaxValue
            : (uint) minutes;
    }
}

internal static class GoalConfigParser {
    internal const string EnabledKey =
        "PlaytimeGoalsEnabled";

    internal const string BatchSizeKey =
        "PlaytimeGoalsBatchSize";

    internal const string GoalsKey =
        "PlaytimeGoals";

    internal const string ParentalWritesKey =
        "PlaytimeGoalsParentalWritesEnabled";

    internal static (
        GoalConfig Config,
        IReadOnlyList<string> Errors
    ) Parse(
        IReadOnlyDictionary<
            string,
            JsonElement
        >? properties
    ) {
        bool enabled = false;
        bool parentalWritesEnabled = false;

        byte batchSize =
            GoalConfig.DefaultBatchSize;

        Dictionary<uint, double?> goals =
            new();

        List<string> errors =
            new();

        if (properties != null) {
            if (
                properties.TryGetValue(
                    EnabledKey,
                    out JsonElement enabledElement
                )
            ) {
                if (
                    enabledElement.ValueKind
                        is JsonValueKind.True
                        or JsonValueKind.False
                ) {
                    enabled =
                        enabledElement.GetBoolean();
                } else {
                    errors.Add(
                        $"{EnabledKey} must be boolean"
                    );
                }
            }

            if (
                properties.TryGetValue(
                    ParentalWritesKey,
                    out JsonElement parentalElement
                )
            ) {
                if (
                    parentalElement.ValueKind
                        is JsonValueKind.True
                        or JsonValueKind.False
                ) {
                    parentalWritesEnabled =
                        parentalElement.GetBoolean();
                } else {
                    errors.Add(
                        $"{ParentalWritesKey} must be boolean"
                    );
                }
            }

            if (
                properties.TryGetValue(
                    BatchSizeKey,
                    out JsonElement batchElement
                )
            ) {
                if (
                    batchElement.ValueKind ==
                        JsonValueKind.Number &&
                    batchElement.TryGetByte(
                        out byte parsedBatch
                    ) &&
                    parsedBatch
                        is >= 1
                        and <= GoalConfig.MaxBatchSize
                ) {
                    batchSize =
                        parsedBatch;
                } else {
                    errors.Add(
                        $"{BatchSizeKey} must be an integer " +
                        $"from 1 to {GoalConfig.MaxBatchSize}"
                    );
                }
            }

            if (
                properties.TryGetValue(
                    GoalsKey,
                    out JsonElement goalsElement
                )
            ) {
                if (
                    goalsElement.ValueKind !=
                    JsonValueKind.Object
                ) {
                    errors.Add(
                        $"{GoalsKey} must be an object " +
                        "mapping AppID to target hours"
                    );
                } else {
                    foreach (
                        JsonProperty property
                        in goalsElement.EnumerateObject()
                    ) {
                        if (
                            !uint.TryParse(
                                property.Name,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out uint appId
                            ) ||
                            appId == 0
                        ) {
                            errors.Add(
                                $"{GoalsKey}: invalid AppID " +
                                $"'{property.Name}'"
                            );

                            continue;
                        }

                        if (
                            property.Value.ValueKind ==
                            JsonValueKind.Null
                        ) {
                            goals[appId] = null;
                            continue;
                        }

                        if (
                            property.Value.ValueKind !=
                                JsonValueKind.Number ||
                            !property.Value.TryGetDouble(
                                out double hours
                            ) ||
                            !double.IsFinite(hours) ||
                            hours <= 0
                        ) {
                            errors.Add(
                                $"{GoalsKey}.{appId}: target must " +
                                "be a positive number of hours or null"
                            );

                            continue;
                        }

                        double maxHours =
                            uint.MaxValue / 60d;

                        if (hours > maxHours) {
                            errors.Add(
                                $"{GoalsKey}.{appId}: target is too large"
                            );

                            continue;
                        }

                        goals[appId] =
                            hours;
                    }
                }
            }
        }

        uint[] gameIds =
            goals.Keys
                .OrderBy(
                    static appId =>
                        appId
                )
                .ToArray();

        return (
            new GoalConfig {
                Enabled = enabled,
                ParentalWritesEnabled =
                    parentalWritesEnabled,
                BatchSize = batchSize,
                GameIds = gameIds,
                Goals = goals
            },
            errors
        );
    }
}
