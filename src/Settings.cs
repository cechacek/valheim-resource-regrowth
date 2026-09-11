using BepInEx.Configuration;

namespace ResourceRegrowth
{
	/*
		All times are in-game days: world time as the game counts it. On a dedicated server it only
		advances while at least one player is online (ZNet.UpdateNetTime), and skips ahead when
		players sleep. One in-game day is 30 minutes of play, so regrowth follows how much the world
		is played, not how long the server is up; nothing regrows while the server is empty.
	*/
	internal class Settings
	{
		public readonly ConfigEntry<bool> Enabled;
		public readonly ConfigEntry<bool> DryRun;
		public readonly ConfigEntry<float> CheckIntervalMinutes;
		public readonly ConfigEntry<int> LogDetailsPerPass;

		public readonly ConfigEntry<float> IdleDays;
		public readonly ConfigEntry<int> PresenceRadiusZones;
		public readonly ConfigEntry<float> PlayerBuildRadius;

		public readonly ConfigEntry<bool> CoresEnabled;
		public readonly ConfigEntry<float> CoresAfterDays;
		public readonly ConfigEntry<string> CorePrefabs;

		public readonly ConfigEntry<bool> ChestsEnabled;
		public readonly ConfigEntry<float> ChestsAfterDays;

		public readonly ConfigEntry<bool> SpawnersEnabled;
		public readonly ConfigEntry<float> SpawnersAfterDays;
		public readonly ConfigEntry<string> SpawnersExclude;

		public Settings(ConfigFile config)
		{
			Enabled = config.Bind("General", "Enabled", true, "Enable or disable the plugin.");
			DryRun = config.Bind("General", "DryRun", true,
				"Only log what would be regrown, change nothing. Leave on until the log looks right.");
			CheckIntervalMinutes = config.Bind("General", "CheckIntervalMinutes", 10f,
				"How often (real minutes) to look for things to regrow.");
			LogDetailsPerPass = config.Bind("General", "LogDetailsPerPass", 20,
				"How many individual objects to log per category and pass; the rest are only counted.");

			IdleDays = config.Bind("Safety", "IdleDays", 10f,
				"Nothing is regrown in an area a player has been near within this many in-game days (30 minutes of play each; time stands still while nobody is online). Areas are only tracked from the plugin's first run.");
			PresenceRadiusZones = config.Bind("Safety", "PresenceRadiusZones", 1,
				"A player counts as near every zone (64 m square) within this many zones of their own.");
			PlayerBuildRadius = config.Bind("Safety", "PlayerBuildRadius", 48f,
				"Nothing within this many metres (horizontally) of a player-built piece or a tombstone is regrown.");

			CoresEnabled = config.Bind("SurtlingCores", "Enabled", true,
				"Put taken surtling cores back on their stands (burial chambers and similar).");
			CoresAfterDays = config.Bind("SurtlingCores", "AfterDays", 20f,
				"In-game days after a core was taken (or after the plugin first saw it taken).");
			CorePrefabs = config.Bind("SurtlingCores", "Prefabs", "Pickable_SurtlingCoreStand",
				"Pickables that stay in place when taken (hide-when-picked) and should come back. Comma separated.");

			ChestsEnabled = config.Bind("TreasureChests", "Enabled", true,
				"Refill looted treasure chests (never player-built chests) with their normal loot.");
			ChestsAfterDays = config.Bind("TreasureChests", "AfterDays", 48f,
				"In-game days after the plugin first saw a chest empty.");

			SpawnersEnabled = config.Bind("DungeonMonsters", "Enabled", true,
				"Let one-time creature spawners (dungeons, camps, caves) spawn again once their creature is dead.");
			SpawnersAfterDays = config.Bind("DungeonMonsters", "AfterDays", 48f,
				"In-game days after the plugin first saw the spawner's creature gone.");
			SpawnersExclude = config.Bind("DungeonMonsters", "ExcludePrefabs", "",
				"Spawner prefabs never to reset. Comma separated.");
		}
	}
}
