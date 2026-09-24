using System;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;

namespace PlaytimeGoals;

internal sealed class FamilyLibraryCache : IDisposable {
    private static readonly TimeSpan FullRefreshInterval =
        TimeSpan.FromMinutes(1);

    private readonly Bot bot;
    private readonly GoalHandler handler;
    private readonly SemaphoreSlim sync = new(1, 1);

    private FamilyLibrarySnapshot? snapshot;
    private DateTime lastFullRefreshUtc = DateTime.MinValue;
    private bool disposed;

    internal FamilyLibraryCache(
        Bot bot,
        GoalHandler handler
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
    }

    internal DateTime LastFullRefreshUtc =>
        lastFullRefreshUtc;

    internal async Task<FamilyLibrarySnapshot>
        Get(
            bool forceFull = false
        ) {
        ObjectDisposedException.ThrowIf(
            disposed,
            this
        );

        DateTime now =
            DateTime.UtcNow;

        FamilyLibrarySnapshot? current =
            snapshot;

        bool needsFull =
            forceFull ||
            current == null ||
            (
                bot.IsConnectedAndLoggedOn &&
                (
                    (now - lastFullRefreshUtc) >=
                    FullRefreshInterval
                )
            );

        if (needsFull) {
            await sync
                .WaitAsync()
                .ConfigureAwait(false);

            try {
                ObjectDisposedException.ThrowIf(
                    disposed,
                    this
                );

                now = DateTime.UtcNow;
                current = snapshot;

                needsFull =
                    forceFull ||
                    current == null ||
                    (
                        bot.IsConnectedAndLoggedOn &&
                        (
                            (now - lastFullRefreshUtc) >=
                            FullRefreshInterval
                        )
                    );

                if (needsFull) {
                    FamilyLibrarySnapshot fetched =
                        await handler
                            .GetFamilyLibrary(
                                bot.SteamID
                            )
                            .ConfigureAwait(false);

                    snapshot = fetched;
                    current = fetched;
                    lastFullRefreshUtc = now;
                }
            } finally {
                sync.Release();
            }
        }

        current ??=
            await handler
                .GetFamilyLibrary(
                    bot.SteamID
                )
                .ConfigureAwait(false);

        return handler
            .ApplyRuntimeAvailability(
                current,
                bot.SteamID
            );
    }

    internal void Invalidate() {
        lastFullRefreshUtc =
            DateTime.MinValue;
    }

    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        sync.Dispose();
    }
}
