# PlaytimeGoals

A custom ArchiSteamFarm plugin for managed per-game playtime goals with
Steam Families and Steam Family View support.

## Current version

`0.5.0`

Tested against:

- ArchiSteamFarm 6.3.9.6
- SteamKit2 3.4.0
- .NET 10
- ASF-ui / Vue 2

## Features

- Unified OWN + Steam Family library
- Per-game finite playtime targets
- Unlimited idling goals
- Up to 32 simultaneous managed AppIDs
- Family-copy availability tracking
- Real gameplay takes priority over managed idling
- ASF CardsFarmer takes priority when farming
- Steam Family View remains globally enabled
- Temporary allow only for the active managed batch
- Exact Family View state restoration
- Crash-safe parental state journal and recovery gate
- Cached library/state API for the web UI
- Live UI updates with visibility-aware polling
- No dependency on native `GamesPlayedWhileIdle`

## Configuration

The plugin uses custom ASF bot configuration keys:

- `PlaytimeGoalsEnabled`
- `PlaytimeGoalsParentalWritesEnabled`
- `PlaytimeGoalsBatchSize`
- `PlaytimeGoals`

`PlaytimeGoals` is the sole list of managed games.

A value is either:

- a number: target playtime in hours
- `null`: unlimited managed idling

No account configuration is included in this repository.

## ASF integration

`PlaytimeGoals/` is designed to live inside an ASF source tree next to
the `ArchiSteamFarm/` project.

The `ASF-ui/` directory in this repository is an overlay containing only
the files modified for PlaytimeGoals integration.

## Security

Never commit an ASF bot configuration, database, login key, Steam
credentials, Family View PIN, IPC password, TLS key, authentication
token, runtime log or PlaytimeGoals state/journal file.

See `SECURITY.md`.

## Status

v0.5 backend, Steam Family integration, parental round-trip,
migration, IPC/TLS health and UI deployment have been verified.

Additional real-world runtime smoke tests are still being performed.
