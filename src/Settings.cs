using BepInEx.Configuration;

namespace ResourceRegrowth
{
	/*
		All times are real hours: wall-clock time, counted whether or not anyone is online (and
		while the server is stopped). World time would not do here: on a dedicated server it only
		advances while a player is online.
	*/
	internal class Settings
	{
		public readonly ConfigEntry<bool> Enabled;
		public readonly ConfigEntry<bool> DryRun;
		public readonly ConfigEntry<float> CheckIntervalMinutes;
		public readonly ConfigEntry<int> LogDetailsPerPass;

		public readonly ConfigEntry<float> IdleHours;
		public readonly ConfigEntry<int> PresenceRadiusZones;
		public readonly ConfigEntry<float> PlayerBuildRadius;

		public readonly ConfigEntry<bool> CoresEnabled;
		public readonly ConfigEntry<float> CoresAfterHours;
		public readonly ConfigEntry<string> CorePrefabs;

		public readonly ConfigEntry<bool> ChestsEnabled;
		public readonly ConfigEntry<float> ChestsAfterHours;
		public readonly ConfigEntry<string> ChestsExclude;

		public readonly ConfigEntry<bool> SpawnersEnabled;
		public readonly ConfigEntry<float> SpawnersAfterHours;
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

			IdleHours = config.Bind("Safety", "IdleHours", 5f,
				"Nothing is regrown in an area a player has been near within this many real hours. Areas are only tracked from the plugin's first run.");
			PresenceRadiusZones = config.Bind("Safety", "PresenceRadiusZones", 2,
				"A player counts as near every zone (64 m square) within this many zones of their own.");
			PlayerBuildRadius = config.Bind("Safety", "PlayerBuildRadius", 48f,
				"Nothing within this many metres (horizontally) of a player-built piece or a tombstone is regrown.");

			CoresEnabled = config.Bind("SurtlingCores", "Enabled", true,
				"Put taken surtling cores back on their stands (burial chambers and similar).");
			CoresAfterHours = config.Bind("SurtlingCores", "AfterHours", 10f,
				"Real hours after a core was taken (or after the plugin first saw it taken).");
			CorePrefabs = config.Bind("SurtlingCores", "Prefabs", "Pickable_SurtlingCoreStand",
				"Pickables that stay in place when taken (hide-when-picked) and should come back. Comma separated.");

			ChestsEnabled = config.Bind("TreasureChests", "Enabled", true,
				"Refill looted treasure chests (never player-built chests) with their normal loot.");
			ChestsAfterHours = config.Bind("TreasureChests", "AfterHours", 24f,
				"Real hours after the plugin first saw a chest empty.");
			ChestsExclude = config.Bind("TreasureChests", "ExcludePrefabs", "",
				"Chest prefabs never to refill, e.g. TreasureChest_meadows_buried for dug-up treasure. Comma separated.");

			SpawnersEnabled = config.Bind("DungeonMonsters", "Enabled", true,
				"Let one-time creature spawners (dungeons, camps, caves) spawn again once their creature is dead.");
			SpawnersAfterHours = config.Bind("DungeonMonsters", "AfterHours", 24f,
				"Real hours after the plugin first saw the spawner's creature gone.");
			SpawnersExclude = config.Bind("DungeonMonsters", "ExcludePrefabs", "",
				"Spawner prefabs never to reset. Comma separated.");
		}
	}
}
