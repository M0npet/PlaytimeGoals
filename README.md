# PlaytimeGoals

**English** | [Українська](README.uk.md)

PlaytimeGoals is a custom ArchiSteamFarm plugin for per-game playtime
goals with Steam Families and Steam Family View support.

> **Status:** v0.5.0 source preview. Core backend, Steam Family
> integration, Family View round-trip recovery, migration, IPC/TLS and
> the web UI have been tested. Additional real-world runtime smoke tests
> are still in progress.

## Features

- Unified OWN + Steam Family library
- Source labels: OWN, FAMILY and OWN+FAMILY
- Finite per-game playtime goals
- Unlimited managed idling with a `null` target
- Up to 32 simultaneously managed AppIDs
- Steam Family copy availability tracking
- Running-app tracking for Steam Family members
- Real PC gameplay has priority over managed idling
- ASF CardsFarmer has priority while farming
- Steam Family View stays globally enabled
- Only the current managed batch is temporarily allowed
- Exact ABSENT / ALLOW / DENY Family View restoration
- Crash-safe parental recovery journal
- Fail-closed recovery gate
- Cached Steam library/state for the web UI
- Visibility-aware live UI polling
- Search and library filters
- Native ASF idle ownership is disabled while PlaytimeGoals is enabled

## Tested environment

PlaytimeGoals v0.5.0 was developed and tested with:

- ArchiSteamFarm 6.3.9.6
- SteamKit2 3.4.0
- .NET 10
- ASF-ui based on Vue 2.7

Newer ASF versions may require source changes.

## Configuration

PlaytimeGoals adds custom bot configuration keys:

```json
{
  "GamesPlayedWhileIdle": [],
  "CustomGamePlayedWhileIdle": null,
  "PlaytimeGoalsEnabled": true,
  "PlaytimeGoalsParentalWritesEnabled": true,
  "PlaytimeGoalsBatchSize": 5,
  "PlaytimeGoals": {
    "123456": 100,
    "234567": null
  }
}
```

`PlaytimeGoals` keys are the only managed AppIDs.

- A numeric value is the target total playtime in hours.
- `null` means unlimited managed idling.

When PlaytimeGoals is enabled, `GamesPlayedWhileIdle` must be empty and
`CustomGamePlayedWhileIdle` must be `null`, otherwise multiple
components could compete for Steam GamesPlayed state.

## Steam Family View

Parental writes are optional.

When `PlaytimeGoalsParentalWritesEnabled` is enabled:

1. ASF must already be able to unlock Steam Family View normally.
2. PlaytimeGoals records the exact original custom state before changing
   a managed game.
3. Only the active managed batch is temporarily allowed.
4. Original states are restored when games leave the batch.
5. Outstanding recovery state is restored before new managed idling is
   allowed after restart or reconnect.

The Family View PIN is obtained from ASF's in-memory bot configuration.
PlaytimeGoals does not store the PIN in its own state files.

Never commit your ASF bot configuration or parental PIN.

## Steam Families

The plugin combines games owned by the account with games currently
available through Steam Families.

For a FAMILY-only game, managed idling is allowed only when a family
copy is known to be available. Games owned directly by the account
remain independently eligible.

If family availability is unknown, FAMILY-only games fail closed and
are not started.

## Priority rules

Managed idling is intentionally lower priority than real gameplay.

Priority is effectively:

1. Real Steam gameplay
2. ASF CardsFarmer
3. PlaytimeGoals managed idling

PlaytimeGoals relinquishes ownership instead of fighting another Steam
session.

## Building from source

This repository currently targets source-tree integration with
ArchiSteamFarm 6.3.9.6.

Clone the matching ASF release including submodules:

```bash
git clone --recursive \
  --branch 6.3.9.6 \
  https://github.com/JustArchiNET/ArchiSteamFarm.git
```

Copy this repository's `PlaytimeGoals/` directory into the ASF source
root so that it sits next to `ArchiSteamFarm/`.

The `ASF-ui/` directory in this repository is an overlay. Copy its files
over the matching paths inside the ASF checkout.

Build the plugin:

```bash
dotnet build \
  PlaytimeGoals/PlaytimeGoals.csproj \
  -c Release \
  --no-restore \
  --no-dependencies
```

The DLL is produced at:

```text
PlaytimeGoals/bin/Release/net10.0/PlaytimeGoals.dll
```

Build the modified ASF-ui:

```bash
cd ASF-ui
npm ci
npm run build
```

Deploy the DLL into an ASF plugin directory and deploy the generated UI
using the same method as your ASF installation.

A packaged installer/release artifact is intentionally not provided yet.

## Security

Do not commit:

- ASF bot configuration files
- Steam usernames or passwords
- Steam login keys
- SteamID/account dumps
- Steam Family View PINs
- ASF IPC passwords
- cryptkey files
- TLS private keys
- cookies or authentication tokens
- ASF databases
- runtime logs
- PlaytimeGoals runtime databases
- PlaytimeGoals parental recovery journals

See [SECURITY.md](SECURITY.md).

## License

Licensed under the Apache License 2.0.

The ASF-ui overlay contains modified files originating from the
Apache-2.0 licensed ASF-ui project. See [NOTICE](NOTICE).

## Disclaimer

PlaytimeGoals is an independent custom plugin and is not affiliated with
Valve, Steam or the ArchiSteamFarm project.
