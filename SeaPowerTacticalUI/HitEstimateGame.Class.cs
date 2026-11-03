using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-168 / ADR-0064: game side of the hits-to-kill estimate for a FOREIGN unit. The track comes from the player's table (RrVehicle), the side is read for the side
    /// check only, the class name (getObjectIniName) only after Vehicle.Identified, the class data from the game's own ini loader by that name (variant-free).
    /// Never the instance of the foreign unit: no compartments, limits, integrity, load-out, variant or blast zones. The own armament is in HitEstimateGame.Own.cs.
    /// Scanner: tests/test_sea_power_hit_estimate_static.py (test_class_file_has_no_forbidden_members).
    /// </summary>
    internal sealed partial class GameApi
    {
        private readonly Dictionary<string, HeClass> _heClasses = new Dictionary<string, HeClass>();
        private readonly Dictionary<string, int> _heMissAt = new Dictionary<string, int>();
        private bool _heLoggedNoLoader;
        private readonly Dictionary<string, string> _heClassWhy = new Dictionary<string, string>();

        /// <summary>Why the class of this ini name has (no) data: ok:cache | ok:loaded | loader missing | not in cache and not loadable | unsupported type X | failed: ...</summary>
        internal string HeClassWhy(string iniName)
        {
            string w;
            return iniName != null && _heClassWhy.TryGetValue(iniName, out w) ? w : "not asked";
        }

        /// <summary>The mission was left: the class cache and the ammunition cache belong to it.</summary>
        internal void HeLeaveMission()
        {
            _heClasses.Clear(); _heMissAt.Clear(); _heClassWhy.Clear();
            HeLeaveMissionOwn();
        }

        internal bool HeIsForeign(int trackId) { return RrTrackSide(trackId) == UnitSide.Other && RrHasTrack(trackId); }

        internal bool HeIsIdentified(int trackId) { return RrIsIdentified(trackId); }

        /// <summary>The class name of an IDENTIFIED track; null before the identification.</summary>
        internal string HeClassIniName(int trackId) { return RrClassIniName(trackId); }

        private static double HeNum(string s, double dflt)
        {
            if (string.IsNullOrEmpty(s)) return dflt;
            int cut = s.IndexOf("//", StringComparison.Ordinal);
            if (cut >= 0) s = s.Substring(0, cut);
            double v;
            return double.TryParse(s.Trim().Split(' ', '\t')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : dflt;
        }

        private string HeIni(object ini, string section, string key)
        {
            try { return Call(ini, HitEstimateSpecs.IniReadValue, section, key, "", false) as string; }
            catch { return null; }
        }

        /// <summary>The class data by ini name; null = no data (not loaded yet, not a ship / submarine / land unit). Loaded rows are kept for the mission, misses for 10 s.</summary>
        internal HeClass HeClassOf(string iniName)
        {
            if (string.IsNullOrEmpty(iniName)) return null;
            HeClass c;
            if (_heClasses.TryGetValue(iniName, out c)) return c;
            int at;
            if (_heMissAt.TryGetValue(iniName, out at) && unchecked(Environment.TickCount - at) < 10000) return null;
            try
            {
                if (!Present(HitEstimateSpecs.FindCached)) { if (!_heLoggedNoLoader) { _heLoggedNoLoader = true; Log("[HitEstimate] class loader is missing"); } _heClassWhy[iniName] = "loader missing"; return null; }
                var p = CallStatic(HitEstimateSpecs.FindCached, "vessels", iniName) ?? CallStatic(HitEstimateSpecs.FindCached, "land_units", iniName);
                string how = "cache";
                if (p == null && Present(HitEstimateSpecs.LoadSimple))
                {
                    // the game's metadata cache is dropped after an ini reload: read the one class with the game's own loader
                    foreach (var folder in new[] { "vessels", "land_units" })
                    {
                        try { p = CallStatic(HitEstimateSpecs.LoadSimple, iniName, folder, 0); } catch { p = null; }
                        if (p != null && StrOrNull(Get(p, HitEstimateSpecs.SopObjectType)) != "Other") break;
                        p = null;
                    }
                    how = "loaded";
                }
                if (p == null) _heClassWhy[iniName] = "not in cache and not loadable";
                else
                {
                    c = HeReadClass(iniName, p);
                    _heClassWhy[iniName] = c == null ? "unsupported type " + StrOrNull(Get(p, HitEstimateSpecs.SopObjectType)) : "ok:" + how;
                }
            }
            catch (Exception e) { c = null; _heClassWhy[iniName] = "failed: " + e.GetType().Name + ": " + e.Message; Log("[HitEstimate] class " + iniName + " failed: " + e.GetType().Name + ": " + e.Message); }
            if (c == null) { _heMissAt[iniName] = Environment.TickCount; return null; }
            _heClasses[iniName] = c;
            return c;
        }

        private HeClass HeReadClass(string iniName, object p)
        {
            var ini = Get(p, HitEstimateSpecs.SopIni);
            if (ini == null) return null;
            string type = StrOrNull(Get(p, HitEstimateSpecs.SopObjectType));
            var c = new HeClass { IniName = iniName };
            var roles = Get(p, HitEstimateSpecs.SopUnitRoles) as IEnumerable;
            if (roles != null)
                foreach (var r in roles)
                {
                    string n = r == null ? null : r.ToString();
                    if (n == "LifeRaft" || n == "SeaMine") c.RaftOrMine = true;
                }
            if (type == "Vessel" || type == "Submarine")
            {
                c.Kind = c.RaftOrMine ? HeTargetKind.None : type == "Submarine" ? HeTargetKind.Sub : HeTargetKind.Ship;
                c.Displacement = D(Get(p, HitEstimateSpecs.SopDisplacement)) ?? 0;
                c.Civilian = BoolOf(Get(p, HitEstimateSpecs.SopIsCivilian));
                string armor = HeIni(ini, "General", "ArmorType");
                c.Armor = string.IsNullOrWhiteSpace(armor) ? "minor" : armor.Trim().ToLowerInvariant();   // no key = Minor (ADR-0064 item 22)
                c.IntegrityOverride = HeNum(HeIni(ini, "Physics", "IntegrityOverride"), -1);
                c.DcTeams = (int)HeNum(HeIni(ini, "General", "NumberOfDCTeams"), -1);
                return c;
            }
            if (type != "LandUnit") return null;
            c.LandSubType = (HeIni(ini, "General", "LandUnitSubType") ?? "").Trim();
            string mob = (HeIni(ini, "General", "IsMobile") ?? "").Trim().ToLowerInvariant();
            c.Mobile = c.LandSubType == "MobileUnit" || mob == "true";
            string a0 = HeIni(ini, "General", "ArmorType");
            c.Armor = string.IsNullOrWhiteSpace(a0) ? "none" : a0.Trim().ToLowerInvariant();
            c.DamagePoints = HeNum(HeIni(ini, "General", "DamagePoints"), -1);
            var names = Present(HitEstimateSpecs.IniSections) ? Call(ini, HitEstimateSpecs.IniSections) as IEnumerable : null;
            if (names != null)
                foreach (var o in names)
                {
                    string sec = o as string;
                    if (string.IsNullOrEmpty(sec) || sec == "General") continue;
                    string integ = HeIni(ini, sec, "Integrity");
                    if (string.IsNullOrWhiteSpace(integ)) continue;
                    string ar = HeIni(ini, sec, "ArmorType");
                    c.Systems.Add(new HeSystem
                    {
                        Section = sec,
                        Integrity = HeNum(integ, 0),
                        Armor = string.IsNullOrWhiteSpace(ar) ? "none" : ar.Trim().ToLowerInvariant(),
                        Radar = string.Equals((HeIni(ini, sec, "Type") ?? "").Trim(), "Radar", StringComparison.OrdinalIgnoreCase),
                    });
                }
            bool large = c.RaftOrMine ? false : c.LandSubType == "Airbase" || c.LandSubType == "Port" || c.LandSubType == "Bridge" || c.LandSubType == "OilRig"
                || (c.LandSubType == "Installation" && c.DamagePoints > HeLargeInstallationDp);
            c.Kind = c.RaftOrMine ? HeTargetKind.None : large ? HeTargetKind.Large : HeTargetKind.Land;
            if (c.Kind == HeTargetKind.Large && Capabilities.Contains("hit_estimate_zones")) HeReadZones(ini, c);
            return c;
        }

        /// <summary>T-170 / ADR-0070 item 2: the static zone data of a large class: per system the Name, the collider names (Collider=) and the geometry of the
        /// [coll_*] sections they name (Collider=Box|Sphere, Position, Rotation, Scale; defaults 0,0,0 / 0,0,0 / 1,1,1). Ini of the class only, never the object.
        /// A section that cannot be read does not break the class: its collider just gets no box.</summary>
        private void HeReadZones(object ini, HeClass c)
        {
            var cache = new Dictionary<string, HeBox>();
            foreach (var s in c.Systems)
            {
                try
                {
                    string nm = HeIni(ini, s.Section, "Name");
                    s.Name = string.IsNullOrWhiteSpace(nm) ? null : nm.Trim();
                    string list = HeIni(ini, s.Section, "Collider");
                    if (string.IsNullOrWhiteSpace(list)) continue;
                    foreach (var raw in list.Split(','))
                    {
                        string cn = raw.Trim();
                        if (cn.Length == 0) continue;
                        s.Colliders++;
                        HeBox box;
                        if (!cache.TryGetValue(cn, out box))
                        {
                            box = HeReadBox(ini, cn);
                            cache[cn] = box;
                        }
                        if (box == null) continue;
                        s.Boxes.Add(box);
                    }
                    HitEstimateGeo.FillSize(s);
                }
                catch (Exception e) { Log("[HitEstimate] zones of " + c.IniName + "/" + s.Section + " failed: " + e.GetType().Name); }
            }
            c.ZonesRead = true;
        }

        private HeBox HeReadBox(object ini, string collName)
        {
            string kind = (HeIni(ini, collName, "Collider") ?? "").Trim();
            bool sphere = string.Equals(kind, "Sphere", StringComparison.OrdinalIgnoreCase);
            if (!sphere && !string.Equals(kind, "Box", StringComparison.OrdinalIgnoreCase)) return null;
            double px, py, pz, rx, ry, rz, sx, sy, sz;
            if (!HeVec(HeIni(ini, collName, "Position"), 0, 0, 0, out px, out py, out pz)) return null;
            if (!HeVec(HeIni(ini, collName, "Rotation"), 0, 0, 0, out rx, out ry, out rz)) return null;
            if (!HeVec(HeIni(ini, collName, "Scale"), 1, 1, 1, out sx, out sy, out sz)) return null;
            return HeBox.Make(collName, sphere, px, py, pz, ry, sx, sy, sz);
        }

        private static bool HeVec(string text, double dx, double dy, double dz, out double x, out double y, out double z)
        {
            if (string.IsNullOrWhiteSpace(text)) { x = dx; y = dy; z = dz; return true; }
            return HeBox.TryVec3(text, out x, out y, out z);
        }

        /// <summary>An Installation above this DamagePoints (factory, depot) is a large object (phase 2: no estimate); below it (a battery, a command post) a land unit.</summary>
        private const double HeLargeInstallationDp = 20;
    }
}
