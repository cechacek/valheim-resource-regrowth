# Sarkastic.eu Resource Regrowth

A server-side [BepInEx](https://github.com/BepInEx/BepInEx) plugin for Valheim 1.0 dedicated servers.
It brings one-time world content back once an area has been left alone for a while:

- **Surtling core stands** get their core back.
- **Treasure chests** (never player-built ones) are refilled with their normal loot.
- **One-time creature spawners** (dungeons, camps, caves) spawn their creature again.

Players need nothing: vanilla clients work. The plugin depends on BepInEx and nothing else,
and it patches no game code.

## How it works

Everything handled here stays in the world when it is used up and only changes state, so
regrowing it means flipping that state back. Nothing is deleted or created by the plugin.

| What | Depleted when | Regrown by |
|---|---|---|
| Surtling core stand | the core was taken (`picked`) | setting `picked` back to false |
| Treasure chest | vanilla filled it once and it is now empty | clearing `addedDefaultItems`; vanilla refills the chest the next time a player loads it |
| One-time spawner | its creature is gone | clearing the spawned link; vanilla spawns again the next time a player loads the area |

Every `CheckIntervalMinutes` the plugin scans the world. An object is regrown only when all of these hold:

- it has been depleted for `AfterDays` (per category);
- no player has been near its zone for `IdleDays`;
- no player-built piece or tombstone lies within `PlayerBuildRadius`;
- no connected player owns it.

A scan of a large world (165,000 objects) takes about 15 ms of work, spread over about 40 frames.

### Time

All times are **in-game days**, counted by the world clock. On a dedicated server that clock
only runs while at least one player is online. It also skips ahead when players sleep.

One in-game day is 30 minutes of play. So regrowth follows how much the world is played, not
how long the server is up: nothing regrows while the server is empty.

The plugin cannot know what happened before it was installed. Both clocks start at its first
run on a world, so nothing regrows until `IdleDays` and `AfterDays` have passed since then.

What it remembers is kept in `<world>.regrowth.txt` next to the world save. That file records
when players were last near each zone, and since when each object has been depleted.

## Install

Copy `SarkasticEU_Resource_Regrowth.dll` to `BepInEx/plugins/` on the server and start it once.
That creates `BepInEx/config/sarkasticeu.resource_regrowth.cfg`.

It starts in **dry run**: it only logs what it would regrow. Watch the log for a few passes, then
set `DryRun = false`.

## Configuration

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | Enabled | true | |
| General | DryRun | true | only log, change nothing |
| General | CheckIntervalMinutes | 10 | real minutes between scans |
| General | LogDetailsPerPass | 20 | objects logged individually per category and scan |
| Safety | IdleDays | 10 | in-game days since a player was near the zone |
| Safety | PresenceRadiusZones | 1 | a player counts as near all zones (64 m) within this many of their own |
| Safety | PlayerBuildRadius | 48 | metres around player-built pieces and tombstones that are never touched |
| SurtlingCores | Enabled / AfterDays | true / 20 | |
| SurtlingCores | Prefabs | Pickable_SurtlingCoreStand | hide-when-picked, non-respawning pickables to restore |
| TreasureChests | Enabled / AfterDays | true / 48 | |
| DungeonMonsters | Enabled / AfterDays | true / 48 | |
| DungeonMonsters | ExcludePrefabs | | spawner prefabs never to reset |

## Build

Requires the .NET SDK and a Valheim dedicated server with BepInEx installed (for the reference assemblies):

```
dotnet build src/ResourceRegrowth.csproj -c Release -p:ValheimServerDir=/path/to/valheim_server
```

The DLL ends up in `bin/Release/`.
