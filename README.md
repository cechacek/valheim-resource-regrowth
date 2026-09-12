# Sarkastic.eu Resource Regrowth

A server-side [BepInEx](https://github.com/BepInEx/BepInEx) plugin for Valheim 1.0 dedicated servers.
It brings one-time world content back once an area has been left alone for a while:

- **Surtling core stands** get their core back.
- **Treasure chests** (never player-built ones) are refilled with their normal loot.
- **One-time creature spawners** (dungeons, camps, caves) spawn their creature again.

Players need nothing: vanilla clients work. The plugin depends on BepInEx and nothing else,
and it patches no game code.

## How it works

Everything handled here stays in the world when it is used up, so regrowing it only changes
the object's data. The plugin deletes and creates no objects.

| What | Depleted when | Regrown by |
|---|---|---|
| Surtling core stand | the core was taken (`picked`) | setting `picked` back to false |
| Treasure chest | vanilla filled it once and it is now empty | filling it from the chest's own loot table, as vanilla does when the chest is first generated |
| One-time spawner | its creature is gone | clearing the spawned link; vanilla spawns again the next time a player loads the area |

Every `CheckIntervalMinutes` the plugin scans the world. An object is regrown only when all of these hold:

- it has been depleted for `AfterHours` (per category);
- no player has been near its zone for `IdleHours`;
- no player-built piece or tombstone lies within `PlayerBuildRadius`;
- it is not in use: no connected player owns it, and the server does not have it loaded.
  Dedicated Simulation, for example, loads the zones around players on the server.

A scan of a large world (165,000 objects) takes about 15 ms of work, spread over about 40 frames.

### Time

All times are **real hours** (wall clock, UTC). They keep counting while nobody is online and
while the server is stopped. The world clock would not work here: on a dedicated server it
only runs while a player is online.

The plugin cannot know what happened before it was installed. Both clocks start at its first
run on a world, so nothing regrows until `IdleHours` and `AfterHours` have passed since then.

After a pass that regrew anything the world is saved right away (the same call as the autosave), so
a crash cannot lose the regrowth while keeping the reset clocks.

What it remembers is kept in `<world>.regrowth.txt` next to the world save. That file records
when players were last near each zone, and since when each object has been depleted.

Objects are identified by prefab and position, because the game hands out new object ids on
every world load.

### Limitations

- On a server where players own the objects around them (no server-side simulation), a spawner reset may
  not reach a player who stays connected the whole time and already had that area loaded in the same
  session. The game only sends a link, never its removal, so that player's game still thinks the creature
  exists. The creature appears once they reconnect. With Dedicated Simulation the server owns these
  objects, so this does not happen there.
- A core stand or chest that some machine currently has loaded is left for a later pass.

## Install

Copy `SarkasticEU_Resource_Regrowth.dll` to `BepInEx/plugins/` on the server and start it once.
That creates `BepInEx/config/sarkasticeu.resource_regrowth.cfg`.

It starts in **dry run**: it only logs what it would regrow. Watch the log for a few passes, then
set `DryRun = false`. The config file is re-read before every pass, so `DryRun`, the delays and
the logging amount change without a restart; `Enabled`, the prefab lists and the exclusions are
applied at startup.

## Configuration

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | Enabled | true | |
| General | DryRun | true | only log, change nothing |
| General | CheckIntervalMinutes | 10 | real minutes between scans |
| General | LogDetailsPerPass | 20 | objects logged individually per category and scan |
| Safety | IdleHours | 5 | real hours since a player was near the zone |
| Safety | PresenceRadiusZones | 2 | a player counts as near all zones (64 m) within this many of their own |
| Safety | PlayerBuildRadius | 48 | metres around player-built pieces and tombstones that are never touched |
| SurtlingCores | Enabled / AfterHours | true / 10 | |
| SurtlingCores | Prefabs | Pickable_SurtlingCoreStand | hide-when-picked, non-respawning pickables to restore |
| TreasureChests | Enabled / AfterHours | true / 24 | |
| TreasureChests | ExcludePrefabs | | chest prefabs never to refill, e.g. `TreasureChest_meadows_buried` |
| DungeonMonsters | Enabled / AfterHours | true / 24 | |
| DungeonMonsters | ExcludePrefabs | | spawner prefabs never to reset |

## Build

Requires the .NET SDK and a Valheim dedicated server with BepInEx installed (for the reference assemblies).
Point `VALHEIM_DEDI_INSTALL` at it, or pass `-p:ValheimServerDir=...`:

```
VALHEIM_DEDI_INSTALL=/path/to/valheim_server dotnet build src/ResourceRegrowth.csproj -c Release
```

The DLL ends up in `bin/Release/`.
