using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		Soft regrowth: every object handled here stays in the world when it is used up and only
		changes state, so regrowing it is flipping that state back. Nothing is deleted or created.

		- Surtling core stands (hide-when-picked pickables): ZDO "picked" back to false.
		- Treasure chests: when empty, ZDO "addedDefaultItems" back to false; vanilla refills the
		  chest with its loot table the next time it is loaded (Container.Awake).
		- One-time creature spawners: once their creature is gone, the "spawned" connection is
		  cleared; vanilla then spawns again when the area is next loaded (CreatureSpawner).

		An object is regrown only when it has been depleted for AfterDays, no player has been near
		its zone for IdleDays, there is no player-built piece or tombstone within PlayerBuildRadius,
		and no connected player owns it.
	*/
	internal class Regrowth
	{
		private enum Kind { Core, Chest, Spawner }

		private class Tally
		{
			public int depleted, waiting, blocked, busy, regrown, logged;
		}

		private const float PresenceIntervalSeconds = 10f;
		private const int ScanBatch = 4096;

		private readonly MonoBehaviour host;
		private readonly Settings settings;
		private RegrowthState state;
		private readonly HashSet<int> corePrefabs = new HashSet<int>();
		private readonly HashSet<int> chestPrefabs = new HashSet<int>();
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
			state.Load(Now());
			passTimer = 60f;
			RegrowthPlugin.Log.LogInfo(
				$"Active{(settings.DryRun.Value ? " (dry run: nothing will be changed)" : "")}. "
				+ $"Surtling core stands: {corePrefabs.Count} prefab(s), treasure chests: {chestPrefabs.Count}, one-time spawners: {spawnerPrefabs.Count}. "
				+ $"Tracking since in-game day {state.FirstRunDay:0.#}, now day {Now():0.#}. State: {state.FilePath}");
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

		private static double Now()
		{
			float dayLength = EnvMan.instance ? EnvMan.instance.m_dayLengthSec : 1800f;
			return ZNet.instance.GetTimeSeconds() / dayLength;
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
						corePrefabs.Add(hash);
					}
					else
					{
						RegrowthPlugin.Log.LogWarning($"{prefab.name} is not a non-respawning hide-when-picked pickable; ignored.");
					}
				}
				Container container = prefab.GetComponent<Container>();
				if (container && prefab.name.StartsWith("TreasureChest_", StringComparison.Ordinal) && container.m_defaultItems.m_drops.Count > 0)
				{
					chestPrefabs.Add(hash);
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
			double now = Now();
			int radius = Mathf.Max(0, settings.PresenceRadiusZones.Value);
			foreach (ZNetPeer peer in ZNet.instance.GetPeers())
			{
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

		private IEnumerator Pass()
		{
			double now = Now();
			bool dryRun = settings.DryRun.Value;
			// Wall time includes the frames in between; work time is only what this pass itself took.
			System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
			System.Diagnostics.Stopwatch work = System.Diagnostics.Stopwatch.StartNew();
			int frames = 1;
			List<ZDO> zdos = new List<ZDO>(ZDOMan.instance.m_objectsByID.Values);
			Dictionary<Vector2s, List<Vector3>> blockers = new Dictionary<Vector2s, List<Vector3>>();
			List<KeyValuePair<Kind, ZDO>> candidates = new List<KeyValuePair<Kind, ZDO>>();

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
				if (settings.CoresEnabled.Value && corePrefabs.Contains(prefab))
				{
					candidates.Add(new KeyValuePair<Kind, ZDO>(Kind.Core, zdo));
				}
				else if (settings.ChestsEnabled.Value && chestPrefabs.Contains(prefab))
				{
					candidates.Add(new KeyValuePair<Kind, ZDO>(Kind.Chest, zdo));
				}
				else if (settings.SpawnersEnabled.Value && spawnerPrefabs.Contains(prefab))
				{
					candidates.Add(new KeyValuePair<Kind, ZDO>(Kind.Spawner, zdo));
				}
				if (i % ScanBatch == ScanBatch - 1)
				{
					frames++;
					work.Stop();
					yield return null;
					work.Start();
				}
			}

			// 2. Decide for each candidate.
			Dictionary<Kind, Tally> tallies = new Dictionary<Kind, Tally>
			{
				{ Kind.Core, new Tally() }, { Kind.Chest, new Tally() }, { Kind.Spawner, new Tally() },
			};
			HashSet<string> stillDepleted = new HashSet<string>();
			foreach (KeyValuePair<Kind, ZDO> candidate in candidates)
			{
				Kind kind = candidate.Key;
				ZDO zdo = candidate.Value;
				if (ZDOMan.instance.GetZDO(zdo.m_uid) != zdo || !IsDepleted(kind, zdo))
				{
					continue;
				}
				Tally tally = tallies[kind];
				tally.depleted++;
				string key = $"{kind}:{zdo.m_uid.UserID}:{zdo.m_uid.ID}";
				stillDepleted.Add(key);
				double since = state.DepletedSince(key, now);
				Vector3 position = zdo.GetPosition();
				double idle = now - state.LastSeen(ZoneSystem.GetZone(position));
				if (now - since < AfterDays(kind) || idle < settings.IdleDays.Value)
				{
					tally.waiting++;
					continue;
				}
				if (IsBlocked(blockers, position))
				{
					tally.blocked++;
					continue;
				}
				if (!OwnerIsFree(zdo))
				{
					tally.busy++;
					continue;
				}
				tally.regrown++;
				if (tally.logged < settings.LogDetailsPerPass.Value)
				{
					tally.logged++;
					RegrowthPlugin.Log.LogInfo(
						$"{(dryRun ? "Would regrow" : "Regrew")} {Describe(kind)} {Name(zdo)} at {position.x:0},{position.z:0}{(position.y > 3000f ? " (dungeon)" : "")}: "
						+ $"depleted {now - since:0.#} days, area idle {idle:0.#} days.");
				}
				if (!dryRun)
				{
					Apply(kind, zdo);
					state.Forget(key);
					stillDepleted.Remove(key);
				}
			}
			state.KeepOnly(stillDepleted);

			RegrowthPlugin.Log.LogInfo(
				$"Pass at day {now:0.#}{(dryRun ? " (dry run)" : "")}: "
				+ string.Join("; ", tallies.Select(t =>
					$"{Describe(t.Key)}s {t.Value.depleted} depleted ({t.Value.waiting} waiting, {t.Value.blocked} near player builds, {t.Value.busy} owned by a player, "
					+ $"{t.Value.regrown} {(dryRun ? "would regrow" : "regrown")})"))
				+ $". Scanned {zdos.Count} objects: {work.ElapsedMilliseconds} ms of work spread over {frames} frames ({wall.ElapsedMilliseconds} ms wall).");
			SaveState();
			running = null;
		}

		private float AfterDays(Kind kind)
		{
			switch (kind)
			{
				case Kind.Core: return settings.CoresAfterDays.Value;
				case Kind.Chest: return settings.ChestsAfterDays.Value;
				default: return settings.SpawnersAfterDays.Value;
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

		private string Name(ZDO zdo)
		{
			return prefabNames.TryGetValue(zdo.GetPrefab(), out string name) ? name : zdo.GetPrefab().ToString();
		}

		private static bool IsDepleted(Kind kind, ZDO zdo)
		{
			switch (kind)
			{
				case Kind.Core:
					return zdo.GetBool(ZDOVars.s_picked);
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
			// Player-built chests are never touched, and a chest vanilla has not filled yet is not looted.
			if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L || !zdo.GetBool(ZDOVars.s_addedDefaultItems))
			{
				return false;
			}
			string items = zdo.GetString(ZDOVars.s_items);
			if (string.IsNullOrEmpty(items))
			{
				return true;
			}
			try
			{
				Inventory inventory = new Inventory("", null, 8, 8);
				inventory.Load(new ZPackage(items), false);
				return inventory.NrOfItems() == 0;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static void Apply(Kind kind, ZDO zdo)
		{
			switch (kind)
			{
				case Kind.Core:
					zdo.Set(ZDOVars.s_picked, false);
					break;
				case Kind.Chest:
					zdo.Set(ZDOVars.s_addedDefaultItems, false);
					break;
				default:
					zdo.SetConnection(ZDOExtraData.ConnectionType.None, ZDOID.None);
					break;
			}
		}

		private static bool OwnerIsFree(ZDO zdo)
		{
			long owner = zdo.GetOwner();
			return owner == 0L || owner == ZDOMan.GetSessionID() || ZNet.instance.GetPeer(owner) == null;
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
