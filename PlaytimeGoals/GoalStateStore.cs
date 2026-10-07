using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlaytimeGoals;

internal sealed record CreditEntry(
    uint ServerBaselineMinutes,
    ulong CreditedSeconds
);

internal sealed class GoalStateStore(string filePath) {
    internal Dictionary<uint, CreditEntry> Load() {
        Dictionary<uint, CreditEntry> result = new();

        try {
            if (!File.Exists(filePath)) {
                return result;
            }

            string text =
                File.ReadAllTextAsync(filePath)
                    .GetAwaiter()
                    .GetResult();

            bool secondPrecision = false;

            foreach (string rawLine in text.Split('\n')) {
                string line = rawLine.Trim();

                if (line.Length == 0) {
                    continue;
                }

                if (line.StartsWith('#')) {
                    if (
                        line.StartsWith(
                            "# PlaytimeGoals v2:",
                            StringComparison.Ordinal
                        )
                    ) {
                        secondPrecision = true;
                    }

                    continue;
                }

                string[] parts =
                    line.Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries
                    );

                if (
                    (parts.Length == 3) &&
                    uint.TryParse(
                        parts[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint appId
                    ) &&
                    uint.TryParse(
                        parts[1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint baseline
                    ) &&
                    ulong.TryParse(
                        parts[2],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out ulong credit
                    )
                ) {
                    /*
                     * v1 stored credited whole minutes.
                     * v2 stores credited whole seconds.
                     *
                     * Files without a v2 header are intentionally
                     * interpreted as v1 for backwards compatibility.
                     */
                    ulong creditedSeconds =
                        secondPrecision
                            ? credit
                            : credit * 60UL;

                    result[appId] =
                        new CreditEntry(
                            baseline,
                            creditedSeconds
                        );
                }
            }
        } catch {
            // Persistence is only a lag-smoothing cache. A corrupt/missing cache must never stop idling.
        }

        return result;
    }

    internal void Save(
        IReadOnlyDictionary<uint, CreditEntry> ledger
    ) {
        try {
            StringBuilder builder = new();

            builder.AppendLine(
                "# PlaytimeGoals v2: appId baselineMinutes creditedSeconds"
            );

            foreach (
                (uint appId, CreditEntry entry)
                in ledger
            ) {
                builder
                    .Append(
                        appId.ToString(
                            CultureInfo.InvariantCulture
                        )
                    )
                    .Append(' ')
                    .Append(
                        entry.ServerBaselineMinutes
                            .ToString(
                                CultureInfo.InvariantCulture
                            )
                    )
                    .Append(' ')
                    .Append(
                        entry.CreditedSeconds
                            .ToString(
                                CultureInfo.InvariantCulture
                            )
                    )
                    .Append('\n');
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(filePath)
                ?? "."
            );

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
        } catch {
            // Same rationale as Load(): never make the feature depend on cache I/O.
        }
    }
}
