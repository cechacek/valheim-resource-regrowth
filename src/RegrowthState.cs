using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ResourceRegrowth
{
	/*
		What the plugin remembers between restarts, in a text file next to the world save:
		- when a player was last near each zone;
		- since when each depleted object has been seen depleted.

		Both clocks start at the plugin's first run on a world, so nothing regrows until
		IdleDays / AfterDays have passed since installing it -- the plugin cannot know what
		happened before.
	*/
	internal class RegrowthState
	{
		private readonly string path;
		private readonly Dictionary<Vector2s, double> lastSeen = new Dictionary<Vector2s, double>();
		private readonly Dictionary<string, double> depletedSince = new Dictionary<string, double>();

		public double FirstRunDay { get; private set; }
		public string FilePath => path;

		public RegrowthState(string path)
		{
			this.path = path;
		}

		public void Load(double now)
		{
			FirstRunDay = now;
			if (!File.Exists(path))
			{
				return;
			}
			foreach (string raw in File.ReadAllLines(path))
			{
				string[] f = raw.Split(' ');
				try
				{
					switch (f[0])
					{
						case "firstrun":
							FirstRunDay = Parse(f[1]);
							break;
						case "zone":
							lastSeen[new Vector2s(int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture))] = Parse(f[3]);
							break;
						case "depleted":
							depletedSince[f[1]] = Parse(f[2]);
							break;
					}
				}
				catch (Exception)
				{
					// A damaged line only costs that entry's clock, which then starts again.
				}
			}
			ClampToNow(now);
		}

		/*
			World time can be behind what the file recorded: the world was rolled back to an older
			save, or it stopped at a point the plugin had already written past. Times in the future
			would keep objects waiting until the world catches up, so they count from now instead.
		*/
		private void ClampToNow(double now)
		{
			FirstRunDay = Math.Min(FirstRunDay, now);
			foreach (Vector2s zone in new List<Vector2s>(lastSeen.Keys))
			{
				lastSeen[zone] = Math.Min(lastSeen[zone], now);
			}
			foreach (string key in new List<string>(depletedSince.Keys))
			{
				depletedSince[key] = Math.Min(depletedSince[key], now);
			}
		}

		public void Save()
		{
			List<string> lines = new List<string>
			{
				"# Sarkastic.eu Resource Regrowth state. In-game days. Edit only while the server is stopped.",
				"firstrun " + Format(FirstRunDay),
			};
			foreach (KeyValuePair<Vector2s, double> zone in lastSeen)
			{
				lines.Add($"zone {zone.Key.x} {zone.Key.y} {Format(zone.Value)}");
			}
			foreach (KeyValuePair<string, double> entry in depletedSince)
			{
				lines.Add($"depleted {entry.Key} {Format(entry.Value)}");
			}
			string temp = path + ".tmp";
			File.WriteAllLines(temp, lines);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			File.Move(temp, path);
		}

		public void MarkSeen(Vector2s zone, double day)
		{
			if (!lastSeen.TryGetValue(zone, out double previous) || previous < day)
			{
				lastSeen[zone] = day;
			}
		}

		public double LastSeen(Vector2s zone)
		{
			return lastSeen.TryGetValue(zone, out double day) ? day : FirstRunDay;
		}

		// Returns since when the object has been depleted, recording now if this is the first sighting.
		public double DepletedSince(string key, double now)
		{
			if (!depletedSince.TryGetValue(key, out double since))
			{
				since = now;
				depletedSince[key] = since;
			}
			return since;
		}

		public void Forget(string key)
		{
			depletedSince.Remove(key);
		}

		// Drops entries for objects that are no longer depleted or no longer exist.
		public void KeepOnly(HashSet<string> keys)
		{
			List<string> stale = new List<string>();
			foreach (string key in depletedSince.Keys)
			{
				if (!keys.Contains(key))
				{
					stale.Add(key);
				}
			}
			foreach (string key in stale)
			{
				depletedSince.Remove(key);
			}
		}

		private static double Parse(string s)
		{
			return double.Parse(s, CultureInfo.InvariantCulture);
		}

		private static string Format(double d)
		{
			return d.ToString("R", CultureInfo.InvariantCulture);
		}
	}
}
