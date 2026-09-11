using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		Soft regrowth: every object handled here stays in the world when it is used up, so
		regrowing it only changes its data. No object is deleted or created.

		- Surtling core stands (hide-when-picked pickables): ZDO "picked" back to false.
		- Treasure chests: when empty, the plugin fills them from the chest's own loot table, the
		  way Container.AddDefaultItems does. Vanilla would only refill a chest on load when the
		  loading machine already owns it, and ownership is handed out on a slower timer than
		  objects are created, so the plugin does not leave that to chance.
		- One-time creature spawners: once their creature is gone, the "spawned" connection is
		  cleared; vanilla then spawns again when the area is next loaded (CreatureSpawner).

		An object is regrown only when it has been depleted for AfterHours, no player has been near
		its zone for IdleHours, there is no player-built piece or tombstone within PlayerBuildRadius,
		and it is not in use: no connected player owns it and the server does not have it loaded
		(Dedicated Simulation loads zones around players on the server). Changing an object that is
		loaded somewhere would not show until it is reloaded, or would act at once (a spawner).
	*/
	internal class Regrowth
	{
		private enum Kind { Core, Chest, Spawner }

		private class Tally
		{
			public int depleted, waiting, blocked, inUse, regrown, logged;
		}

		// ZDO instances are pooled and reused after an object is destroyed, so a candidate keeps
		// the id and prefab it had when collected and is dropped if either no longer matches.
		private struct Candidate
		{
			public Kind kind;
			public ZDO zdo;
			public ZDOID uid;
			public int prefab;
		}

		private const float PresenceIntervalSeconds = 10f;
		private const int ScanBatch = 4096;

		private readonly MonoBehaviour host;
		private readonly Settings settings;
		private RegrowthState state;
		private readonly Dictionary<int, bool> corePrefabs = new Dictionary<int, bool>(); // prefab -> picked by default
		private readonly Dictionary<int, Container> chestPrefabs = new Dictionary<int, Container>();
		private readonly HashSet<int> spawnerPrefabs = new HashSet<int>();
		private readonly HashSet<int> piecePrefabs = new HashSet<int>();
		private readonly HashSet<int> tombstonePrefabs = new HashSet<int>();
		private readonly Dictionary<int, string> prefabNames = new Dictionary<int, string>();
		private float presenceTimer;
		private float passTimer;
		private Coroutine running;

		public Regrowth(MonoBehaviour host, Settings settings)
		{
			this.host = host;
			this.settings = settings;
		}

		public void Start()
		{
			BuildPrefabSets();
			state = new RegrowthState(StatePath());
			DateTime now = Now();
			state.Load(now);
			passTimer = 60f;
			RegrowthPlugin.Log.LogInfo(
				$"Active{(settings.DryRun.Value ? " (dry run: nothing will be changed)" : "")}. "
				+ $"Surtling core stands: {corePrefabs.Count} prefab(s), treasure chests: {chestPrefabs.Count}, one-time spawners: {spawnerPrefabs.Count}. "
				+ $"Tracking since {state.FirstRun.ToLocalTime():yyyy-MM-dd HH:mm} ({Hours(state.FirstRun, now):0.#} h ago). State: {state.FilePath}");
		}

		public void Stop()
		{
			if (running != null)
			{
				host.StopCoroutine(running);
				running = null;
			}
			SaveState();
		}

		public void Tick(float dt)
		{
			presenceTimer -= dt;
			if (presenceTimer <= 0f)
			{
				presenceTimer = PresenceIntervalSeconds;
				RecordPresence();
			}
			passTimer -= dt;
			if (passTimer <= 0f && running == null)
			{
				passTimer = Mathf.Max(1f, settings.CheckIntervalMinutes.Value) * 60f;
				running = host.StartCoroutine(Pass());
			}
		}

		private static DateTime Now()
		{
			return DateTime.UtcNow;
		}

		private static double Hours(DateTime from, DateTime to)
		{
			return (to - from).TotalHours;
		}

		private static string StatePath()
		{
			World world = ZNet.World;
			return Path.Combine(SaveSystem.GetWorldsSaveRootPath(world.m_fileSource), world.m_name + ".regrowth.txt");
		}

		private void SaveState()
		{
			try
			{
				state?.Save();
			}
			catch (Exception e)
			{
				RegrowthPlugin.Log.LogWarning($"Could not save state to {state.FilePath}: {e.Message}");
			}
		}

		private void BuildPrefabSets()
		{
			HashSet<string> cores = SplitList(settings.CorePrefabs.Value);
			HashSet<string> excludedChests = SplitList(settings.ChestsExclude.Value);
			HashSet<string> excludedSpawners = SplitList(settings.SpawnersExclude.Value);
			foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
			{
				if (!prefab)
				{
					continue;
				}
				int hash = prefab.name.GetStableHashCode();
				prefabNames[hash] = prefab.name;
				if (prefab.GetComponent<Piece>())
				{
					piecePrefabs.Add(hash);
				}
				if (prefab.GetComponent<TombStone>())
				{
					tombstonePrefabs.Add(hash);
				}
				if (cores.Contains(prefab.name))
				{
					Pickable pickable = prefab.GetComponent<Pickable>();
					if (pickable && pickable.m_hideWhenPicked && pickable.m_respawnTimeMinutes <= 0f)
					{
						corePrefabs[hash] = pickable.m_defaultPicked;
					}
					else
					{
						RegrowthPlugin.Log.LogWarning($"{prefab.name} is not a non-respawning hide-when-picked pickable; ignored.");
					}
				}
				if (prefab.name.StartsWith("TreasureChest_", StringComparison.Ordinal) && !excludedChests.Contains(prefab.name))
				{
					Container container = prefab.GetComponent<Container>();
					if (container && !container.m_rootObjectOverride && container.m_defaultItems.m_drops.Count > 0)
					{
						chestPrefabs[hash] = container;
					}
					else
					{
						RegrowthPlugin.Log.LogInfo($"{prefab.name} has no container with a loot table of its own; ignored.");
					}
				}
				CreatureSpawner spawner = prefab.GetComponent<CreatureSpawner>();
				if (spawner && spawner.m_respawnTimeMinuts <= 0f && !excludedSpawners.Contains(prefab.name))
				{
					spawnerPrefabs.Add(hash);
				}
			}
		}

		private static HashSet<string> SplitList(string value)
		{
			return new HashSet<string>(value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
		}

		private void RecordPresence()
		{
			DateTime now = Now();
			int radius = Mathf.Max(0, settings.PresenceRadiusZones.Value);
			foreach (ZNetPeer peer in ZNet.instance.GetPeers())
			{
				// Only players in the world: a peer still connecting or loading has no real position yet.
				if (!peer.IsReady() || peer.m_characterID.IsNone())
				{
					continue;
				}
				Vector2s zone = ZoneSystem.GetZone(peer.GetRefPos());
				for (int dy = -radius; dy <= radius; dy++)
				{
					for (int dx = -radius; dx <= radius; dx++)
					{
						state.MarkSeen(new Vector2s(zone.x + dx, zone.y + dy), now);
					}
				}
			}
		}

		// On shutdown ZDOMan empties its objects before the plugin is told; a pass must then not
		// conclude that nothing is depleted any more and throw the clocks away.
		private static bool WorldLoaded()
		{
			return ZNet.instance && ZNet.instance.enabled && ZDOMan.instance != null && ZDOMan.instance.m_objectsByID.Count > 0;
		}

		private IEnumerator Pass()
		{
			// Whatever goes wrong inside, the next pass must still be able to start.
			try
			{
				DateTime now = Now();
				bool dryRun = settings.DryRun.Value;
				// Wall time includes the frames in between; work time is only what this pass itself took.
				System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
				System.Diagnostics.Stopwatch work = System.Diagnostics.Stopwatch.StartNew();
				int frames = 1;
				List<ZDO> zdos = new List<ZDO>(ZDOMan.instance.m_objectsByID.Values);
				Dictionary<Vector2s, List<Vector3>> blockers = new Dictionary<Vector2s, List<Vector3>>();
				List<Candidate> candidates = new List<Candidate>();

				// 1. One pass over every object: note player builds and tombstones, collect candidates.
				for (int i = 0; i < zdos.Count; i++)
				{
					ZDO zdo = zdos[i];
					int prefab = zdo.GetPrefab();
					if (tombstonePrefabs.Contains(prefab)
						|| (piecePrefabs.Contains(prefab) && zdo.GetLong(ZDOVars.s_creator, 0L) != 0L))
					{
						AddBlocker(blockers, zdo.GetPosition());
					}
					if (settings.CoresEnabled.Value && corePrefabs.ContainsKey(prefab))
					{
						candidates.Add(new Candidate { kind = Kind.Core, zdo = zdo, uid = zdo.m_uid, prefab = prefab });
					}
					else if (settings.ChestsEnabled.Value && chestPrefabs.ContainsKey(prefab))
					{
						candidates.Add(new Candidate { kind = Kind.Chest, zdo = zdo, uid = zdo.m_uid, prefab = prefab });
					}
					else if (settings.SpawnersEnabled.Value && spawnerPrefabs.Contains(prefab))
					{
						candidates.Add(new Candidate { kind = Kind.Spawner, zdo = zdo, uid = zdo.m_uid, prefab = prefab });
					}
					if (i % ScanBatch == ScanBatch - 1)
					{
						frames++;
						work.Stop();
						yield return null;
						work.Start();
					}
				}
				if (!WorldLoaded())
				{
					yield break;
				}

				// 2. Decide for each candidate, all in this frame.
				Dictionary<Kind, Tally> tallies = new Dictionary<Kind, Tally>
				{
					{ Kind.Core, new Tally() }, { Kind.Chest, new Tally() }, { Kind.Spawner, new Tally() },
				};
				HashSet<string> stillDepleted = new HashSet<string>();
				HashSet<string> regrown = new HashSet<string>();
				int errors = 0;
				foreach (Candidate candidate in candidates)
				{
					try
					{
						Decide(candidate, now, dryRun, blockers, tallies[candidate.kind], stillDepleted, regrown);
					}
					catch (Exception e)
					{
						if (errors++ == 0)
						{
							RegrowthPlugin.Log.LogWarning($"Skipped {Describe(candidate.kind)} {Name(candidate.prefab)}: {e}");
						}
					}
				}
				// Forgotten only now: objects that share a key (same prefab and spot) share the clock too.
				foreach (string key in regrown)
				{
					state.Forget(key);
					stillDepleted.Remove(key);
				}
				state.KeepOnly(stillDepleted);
				state.ForgetZonesSeenBefore(now.AddHours(-Math.Max(0f, settings.IdleHours.Value)));

				RegrowthPlugin.Log.LogInfo(
					$"Pass{(dryRun ? " (dry run)" : "")}: "
					+ string.Join("; ", tallies.Select(t =>
						$"{Describe(t.Key)}s {t.Value.depleted} depleted ({t.Value.waiting} waiting, {t.Value.blocked} near player builds, {t.Value.inUse} in use, "
						+ $"{t.Value.regrown} {(dryRun ? "would regrow" : "regrown")})"))
					+ $". Scanned {zdos.Count} objects: {work.ElapsedMilliseconds} ms of work spread over {frames} frames ({wall.ElapsedMilliseconds} ms wall)."
					+ (errors > 0 ? $" {errors} object(s) skipped after errors." : ""));
				SaveState();
			}
			finally
			{
				running = null;
			}
		}

		private void Decide(Candidate candidate, DateTime now, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers,
			Tally tally, HashSet<string> stillDepleted, HashSet<string> regrown)
		{
			ZDO zdo = candidate.zdo;
			if (ZDOMan.instance.GetZDO(candidate.uid) != zdo || zdo.GetPrefab() != candidate.prefab || !IsDepleted(candidate.kind, zdo))
			{
				return;
			}
			tally.depleted++;
			string key = Key(candidate.kind, zdo);
			stillDepleted.Add(key);
			double depleted = Hours(state.DepletedSince(key, now), now);
			Vector3 position = zdo.GetPosition();
			double idle = Hours(state.LastSeen(ZoneSystem.GetZone(position)), now);
			if (depleted < AfterHours(candidate.kind) || idle < settings.IdleHours.Value)
			{
				tally.waiting++;
				return;
			}
			if (IsBlocked(blockers, position))
			{
				tally.blocked++;
				return;
			}
			if (InUse(zdo))
			{
				tally.inUse++;
				return;
			}
			tally.regrown++;
			if (tally.logged < settings.LogDetailsPerPass.Value)
			{
				tally.logged++;
				RegrowthPlugin.Log.LogInfo(
					$"{(dryRun ? "Would regrow" : "Regrew")} {Describe(candidate.kind)} {Name(candidate.prefab)} at {position.x:0},{position.z:0}{(position.y > 3000f ? " (dungeon)" : "")}: "
					+ $"depleted {depleted:0.##} h, area idle {idle:0.##} h.");
			}
			if (!dryRun)
			{
				Apply(candidate.kind, zdo);
				regrown.Add(key);
			}
		}

		/*
			ZDO ids are handed out afresh on every world load (ZDO.Load), so they cannot identify an
			object across restarts. Everything handled here stays where the world put it, so prefab and
			position do.
		*/
		private string Key(Kind kind, ZDO zdo)
		{
			Vector3 p = zdo.GetPosition();
			return string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:0.0}:{3:0.0}:{4:0.0}",
				kind, Name(zdo.GetPrefab()).Replace(' ', '_'), p.x, p.y, p.z);
		}

		private float AfterHours(Kind kind)
		{
			switch (kind)
			{
				case Kind.Core: return settings.CoresAfterHours.Value;
				case Kind.Chest: return settings.ChestsAfterHours.Value;
				default: return settings.SpawnersAfterHours.Value;
			}
		}

		private static string Describe(Kind kind)
		{
			switch (kind)
			{
				case Kind.Core: return "surtling core stand";
				case Kind.Chest: return "treasure chest";
				default: return "creature spawner";
			}
		}

		private string Name(int prefab)
		{
			return prefabNames.TryGetValue(prefab, out string name) ? name : prefab.ToString();
		}

		private bool IsDepleted(Kind kind, ZDO zdo)
		{
			switch (kind)
			{
				case Kind.Core:
					return zdo.GetBool(ZDOVars.s_picked, corePrefabs[zdo.GetPrefab()]);
				case Kind.Chest:
					return IsLootedChest(zdo);
				default:
					ZDOConnection connection = zdo.GetConnection();
					return connection != null
						&& connection.m_type == ZDOExtraData.ConnectionType.Spawned
						&& ZDOMan.instance.GetZDO(connection.m_target) == null;
			}
		}

		private static bool IsLootedChest(ZDO zdo)
		{
			// Player-built chests are never touched, a chest vanilla has not filled yet is not looted,
			// and chests from cheats are left alone.
			if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L || !zdo.GetBool(ZDOVars.s_addedDefaultItems) || zdo.GetBool(ZDOVars.s_cheated))
			{
				return false;
			}
			// Container.Save stores the inventory as a byte array (Inventory.Save): version, then item count.
			byte[] items = zdo.GetByteArray(ZDOVars.s_items);
			if (items == null)
			{
				return true;
			}
			ZPackage package = new ZPackage(items);
			int version = package.ReadInt();
			int count = version >= (int)Version.Item.Smaller ? package.ReadUShort() : package.ReadInt();
			return count == 0;
		}

		private void Apply(Kind kind, ZDO zdo)
		{
			switch (kind)
			{
				case Kind.Core:
					zdo.Set(ZDOVars.s_picked, false);
					break;
				case Kind.Chest:
					Refill(zdo, chestPrefabs[zdo.GetPrefab()]);
					break;
				default:
					zdo.SetConnection(ZDOExtraData.ConnectionType.None, ZDOID.None);
					break;
			}
		}

		// What Container.AddDefaultItems and Container.Save do, done on the ZDO directly.
		private static void Refill(ZDO zdo, Container prefab)
		{
			Inventory inventory = new Inventory(prefab.m_name, prefab.m_bkg, prefab.m_width, prefab.m_height);
			foreach (ItemDrop.ItemData item in prefab.m_defaultItems.GetDropListItems())
			{
				inventory.AddItem(item);
			}
			ZPackage package = new ZPackage();
			inventory.Save(package);
			zdo.Set(ZDOVars.s_items, package.GetArray());
			zdo.Set(ZDOVars.s_addedDefaultItems, true);
		}

		private static bool InUse(ZDO zdo)
		{
			if (ZNetScene.instance.FindInstance(zdo))
			{
				return true;
			}
			long owner = zdo.GetOwner();
			return owner != 0L && owner != ZDOMan.GetSessionID() && ZNet.instance.GetPeer(owner) != null;
		}

		private static void AddBlocker(Dictionary<Vector2s, List<Vector3>> blockers, Vector3 position)
		{
			Vector2s zone = ZoneSystem.GetZone(position);
			if (!blockers.TryGetValue(zone, out List<Vector3> list))
			{
				list = new List<Vector3>();
				blockers[zone] = list;
			}
			list.Add(position);
		}

		private bool IsBlocked(Dictionary<Vector2s, List<Vector3>> blockers, Vector3 position)
		{
			float radius = Mathf.Max(0f, settings.PlayerBuildRadius.Value);
			int zones = Mathf.CeilToInt(radius / 64f);
			Vector2s zone = ZoneSystem.GetZone(position);
			for (int dy = -zones; dy <= zones; dy++)
			{
				for (int dx = -zones; dx <= zones; dx++)
				{
					if (!blockers.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<Vector3> list))
					{
						continue;
					}
					foreach (Vector3 blocker in list)
					{
						float x = blocker.x - position.x;
						float z = blocker.z - position.z;
						if (x * x + z * z <= radius * radius)
						{
							return true;
						}
					}
				}
			}
			return false;
		}
	}
}
