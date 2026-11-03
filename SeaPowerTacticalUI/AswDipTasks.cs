using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-159: the pure parts of two game reads of the ASW module, split from AswGame.cs so that the light stand can prove them:
    /// the longest dipping-sonar range of a unit (sensor name -> R_eff) and the "someone else changed the task list" test.
    /// </summary>
    internal static class AswDipTasks
    {
        /// <summary>Task kinds the ASW controller itself puts into a unit's queue: they never count as a player takeover.</summary>
        private static readonly string[] OwnKinds = { "AttackAtWaypoint", "GoToWaypointTask", "DropSonobuoyTask", "EngageTask" };

        /// <summary>R_eff (nm) of the first dipping sonar in the list; 0 = none / unknown. The delegates read the game.</summary>
        public static double DipRangeNm(IEnumerable sensors, Func<object, bool> isDippingSonar, Func<object, string> nameOf, Func<string, double> rangeNm)
        {
            if (sensors == null) return 0;
            foreach (var s in sensors)
            {
                if (s == null || !isDippingSonar(s)) continue;
                string name = nameOf(s);
                if (string.IsNullOrEmpty(name)) continue;
                double r = rangeNm(name);
                if (r > 0) return r;
            }
            return 0;
        }

        /// <summary>True when the current task kinds hold a kind the controller never issues that was not in the previous snapshot.
        /// The first snapshot of a unit only records the baseline.</summary>
        public static bool ChangedByOther(IDictionary<int, List<string>> last, int uid, IEnumerable<string> kinds)
        {
            var now = new List<string>(kinds);
            List<string> prev;
            if (!last.TryGetValue(uid, out prev)) { last[uid] = now; return false; }
            last[uid] = now;
            foreach (var k in now)
                if (Array.IndexOf(OwnKinds, k) < 0 && !prev.Contains(k)) return true;
            return false;
        }

        /// <summary>T-214: why the aircraft left the group's hands. null = no takeover; "player" = the player took it; "game_rtb" = the game sends it home
        /// (Winchester | BingoFuel | ReturnToBase: the new GoToRelativePoint task is the game's return task, not a player's order); ownRtb = the mod sent it home itself.</summary>
        internal static string Takeover(string where, string aiState, bool evading, bool tasksChanged, bool ownRtb)
        {
            if (where == "player") return "player";
            if (evading || !tasksChanged || ownRtb) return null;
            if (aiState == "Winchester" || aiState == "BingoFuel" || aiState == "ReturnToBase") return "game_rtb";
            return "player";
        }

        // ---- T-188: does the type carry a dipping sonar the game really loads (ObjectBaseLoader.loadSensorSystems) ----

        /// <summary>Result of the type read. Dropped: null | "beyond_count:SensorSystem3=VGS-3 n=2" | "removed_by_loadout:SensorSystem2=AQS-13E".</summary>
        internal sealed class DipSonarInfo { public bool Loaded; public string System; public int Declared; public string Dropped; public string Via; }

        private static string Clean(string raw)
        {
            if (raw == null) return null;
            int c = raw.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) raw = raw.Substring(0, c);
            return raw.Trim();
        }

        /// <summary>"2 //x" -> 2; null / "" / garbage -> dflt.</summary>
        public static int IniInt(string raw, int dflt)
        {
            string t = Clean(raw);
            int v;
            return !string.IsNullOrEmpty(t) && int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : dflt;
        }

        /// <summary>"6,7" -> [6,7].</summary>
        public static List<int> IniIntList(string raw)
        {
            var res = new List<int>();
            string t = Clean(raw);
            if (string.IsNullOrEmpty(t)) return res;
            foreach (var part in t.Split(','))
            {
                int v;
                if (int.TryParse(part.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) res.Add(v);
            }
            return res;
        }

        /// <summary>The game loads SensorSystem1..NumberOfSensorSystems minus the loadout's _SensorsToRemove; a Sonar section whose sensors.ini type is Dipping is the dipping sonar.</summary>
        public static DipSonarInfo DipSonarOfIni(Func<string, bool> hasSection, Func<string, string, string> read, string loadout, Func<string, bool> isDippingSystem)
        {
            return DipSonarOfIni(hasSection, read, loadout, isDippingSystem, null);
        }

        /// <summary>T-200: sensor sections that the loadout's hardpoint switches on (ObjectBaseLoader.LoadWeaponSystems + WeaponSystemHardpoint ctor).
        /// Key = loadout section, Value = sensor section. loadout null/"" = "Default"; "Empty" = none; nation null = no national section.</summary>
        public static List<KeyValuePair<string, string>> LoadoutSensorSections(Func<string, bool> hasSection, Func<string, string, string> read, string loadout, string nation)
        {
            var res = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(loadout)) loadout = "Default";
            if (loadout == "Empty") return res;
            if (!hasSection("WeaponSystems")) return res;   // the game returns before LoadWeaponSystems without the section
            int n = IniInt(read("WeaponSystems", "NumberOfWeaponSystems"), 1);
            for (int k = 1; k <= n; k++)
            {
                string text3 = "WeaponSystem" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (hasSection(text3 + loadout))
                {
                    if (!string.IsNullOrEmpty(Clean(read(text3 + loadout, "SystemName")))) text3 += loadout;
                    else if ((Clean(read(text3, "Type")) ?? "").ToLowerInvariant() != "hardpoint") continue;
                }
                if ((Clean(read(text3, "Type")) ?? "").ToLowerInvariant() != "hardpoint") continue;
                string lt = loadout;
                if (nation != null && hasSection(text3 + loadout + "_" + nation)) lt = loadout + "_" + nation;
                string sec = text3 + lt;
                string raw = Clean(read(sec, "EnableSensorSystems"));
                if (string.IsNullOrEmpty(raw)) continue;
                foreach (var part in raw.Split(','))
                {
                    string name = part.Trim();
                    if (name.Length > 0) res.Add(new KeyValuePair<string, string>(sec, name));
                }
            }
            return res;
        }

        public static DipSonarInfo DipSonarOfIni(Func<string, bool> hasSection, Func<string, string, string> read, string loadout, Func<string, bool> isDippingSystem, string nation)
        {
            var info = new DipSonarInfo();
            if (!hasSection("SensorSystems")) return info;
            int n = IniInt(read("SensorSystems", "NumberOfSensorSystems"), 1);
            info.Declared = n;
            var removed = string.IsNullOrEmpty(loadout) ? new List<int>() : IniIntList(read("WeaponSystems", loadout + "_SensorsToRemove"));
            string dropped = null;
            for (int i = 1; i <= n + 8; i++)
            {
                string sec = "SensorSystem" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!hasSection(sec)) { if (i > n) break; continue; }
                if (Clean(read(sec, "Type")) != "Sonar") continue;
                string name = Clean(read(sec, "SystemName"));
                if (string.IsNullOrEmpty(name) || !isDippingSystem(name)) continue;
                if (i > n) { if (dropped == null) dropped = "beyond_count:" + sec + "=" + name + " n=" + n.ToString(System.Globalization.CultureInfo.InvariantCulture); continue; }
                if (removed.Contains(i)) { if (dropped == null) dropped = "removed_by_loadout:" + sec + "=" + name; continue; }
                info.Loaded = true; info.System = name; info.Dropped = null; info.Via = "count";
                return info;
            }
            foreach (var kv in LoadoutSensorSections(hasSection, read, loadout, nation))
            {
                if (!hasSection(kv.Value) || Clean(read(kv.Value, "Type")) != "Sonar") continue;
                string name = Clean(read(kv.Value, "SystemName"));
                if (string.IsNullOrEmpty(name) || !isDippingSystem(name)) continue;
                info.Loaded = true; info.System = name; info.Dropped = null; info.Via = "loadout:" + kv.Key;
                return info;
            }
            info.Dropped = dropped;
            return info;
        }

        /// <summary>The ui_state note: null when nothing was dropped.</summary>
        public static string DipNote(DipSonarInfo i) { return i == null ? null : i.Dropped; }
    }
}
