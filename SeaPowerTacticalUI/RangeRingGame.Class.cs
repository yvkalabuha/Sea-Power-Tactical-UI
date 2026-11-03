using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-137 / ADR-0055: game side of the range rings for a FOREIGN unit. Reads the player's track (Vehicle of the PlottingTable) and its
    /// Vehicle.Identified; only after the identification the class name (getObjectIniName) and the encyclopedia entry (variant 0, no load-out).
    /// The side of the object behind a track is read for the side check only. Never the real armament, sensors, mode, heading or position of the object.
    /// Own units live in RangeRingGame.Own.cs. Scanner: tests/test_sea_power_range_rings_static.py (test_class_file_has_no_forbidden_members).
    /// </summary>
    internal sealed partial class GameApi
    {
        private readonly Dictionary<int, object> _rrVehicles = new Dictionary<int, object>();

        /// <summary>The mission was left; the cached vehicles are objects of it.</summary>
        internal void RrLeaveMission() { _rrVehicles.Clear(); _rrMountMiss.Clear(); }

        private object RrVehicle(int trackId)
        {
            object v;
            if (_rrVehicles.TryGetValue(trackId, out v) && v != null) return v;
            RrScan();
            return _rrVehicles.TryGetValue(trackId, out v) ? v : null;
        }

        /// <summary>Rebuilds _rrVehicles from the plotting table; false = the table is not readable.</summary>
        private bool RrScan()
        {
            try
            {
                _rrVehicles.Clear();
                var vehicles = Vehicles();
                if (vehicles == null) return false;
                foreach (var t in vehicles)
                {
                    var id = D(Get(t, OverviewSpecs.VehId));
                    if (id.HasValue && !_rrVehicles.ContainsKey((int)id.Value)) _rrVehicles[(int)id.Value] = t;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>T-162: rebuilds _rrVehicles from the plotting table (called at most once per rebuild while tracks are pinned). false = the table is not readable.</summary>
        internal bool RrRescanTracks()
        {
            return RrScan();
        }

        /// <summary>T-162: the track is in the player's table after the last RrRescanTracks / RrVehicle scan.</summary>
        internal bool RrHasTrack(int trackId)
        {
            object v;
            return _rrVehicles.TryGetValue(trackId, out v) && v != null;
        }

        /// <summary>T-162: the clicked object -> Vehicle.Id of the PLAYER'S track of it (Globals._playerTaskforce; the game resolves the track); -1 = an own unit,
        /// not a unit, no track. Reads of the object: IsPlayer, isUnit, GetMapVehicle only. Seeds _rrVehicles.</summary>
        internal int RrMenuTrack(object targetObject)
        {
            try
            {
                if (targetObject == null) return -1;
                if (BoolOf(Get(targetObject, ContactInfoSpecs.ObjIsPlayer))) return -1;
                if (!BoolOf(Call(targetObject, InterceptSpecs.ObjIsUnit))) return -1;
                var tf = GetStatic(CoreSpecs.PlayerTaskforce);
                if (tf == null) return -1;
                var v = Call(targetObject, InterceptSpecs.ObjMapVehicle, tf);   // leak-ok: the game resolves the player's own track of the unit
                if (v == null) return -1;
                var id = D(Get(v, OverviewSpecs.VehId));
                if (!id.HasValue) return -1;
                int k = (int)id.Value;
                _rrVehicles[k] = v;
                return k;
            }
            catch { return -1; }
        }

        /// <summary>2026-10-22: null = the track is there; "lost" = it is not in the player's plotting table (after the last scan); "destroyed" = the unit
        /// behind it is destroyed or removed (the game reports the kill to the player). Of the object only "is it still there" is read (null / destroyed
        /// Unity object / IsDestroyed), as the intercept module does for its target track; nothing of its state.</summary>
        internal string RrTrackGone(int trackId)
        {
            try
            {
                if (!RrHasTrack(trackId)) return "lost";
                var o = Get(RrVehicle(trackId), OverviewSpecs.VehObject);   // leak-ok: only "is it still there"
                if (o == null || !IsAlive(o) || BoolOf(Get(o, ContactInfoSpecs.ObjIsDestroyed))) return "destroyed";
                return null;
            }
            catch { return null; }
        }

        /// <summary>Id of the selected track, or -1.</summary>
        internal int RrSelectedTrack() { return SelectedTrackId(); }

        /// <summary>The side of the track's unit (Player / Ally / Other); the side only.</summary>
        internal UnitSide RrTrackSide(int trackId)
        {
            try
            {
                var v = RrVehicle(trackId);
                var o = v == null ? null : Get(v, OverviewSpecs.VehObject);
                if (o == null || !IsAlive(o)) return UnitSide.Other;
                var side = Get(Get(o, RangeRingSpecs.ObjTaskforce), RangeRingSpecs.TaskforceSide);
                var s = side == null ? null : side.ToString();
                return s == "Player" ? UnitSide.Player : s == "Ally" ? UnitSide.Ally : UnitSide.Other;
            }
            catch { return UnitSide.Other; }
        }

        internal bool RrIsIdentified(int trackId)
        {
            try
            {
                var v = RrVehicle(trackId);
                return v != null && BoolOf(RxGet(v, OverviewSpecs.VehIdentified));
            }
            catch { return false; }
        }

        /// <summary>The position of the player's track and whether it is an air contact.</summary>
        internal bool RrTrackPose(int trackId, out RingPt p, out bool air)
        {
            p = default(RingPt);
            air = false;
            try
            {
                var v = RrVehicle(trackId);
                double lat, lon;
                if (v == null || !GeoOf(RxGet(v, OverviewSpecs.VehPosition), out lat, out lon)) return false;
                p = new RingPt(lat, lon);
                air = Kind(v) == "Air";
                return true;
            }
            catch { return false; }
        }

        /// <summary>The class name (ini name) of an IDENTIFIED track; null before the identification.</summary>
        internal string RrClassIniName(int trackId)
        {
            if (!RrIsIdentified(trackId)) return null;
            try
            {
                var o = Get(RrVehicle(trackId), OverviewSpecs.VehObject);
                return o == null || !IsAlive(o) ? null : Call(o, OverviewSpecs.ObjIniName) as string;
            }
            catch { return null; }
        }

        private object RrEncyclopedia()
        {
            var enc = SingletonInstance(RangeRingSpecs.EncyclopediaType);
            return enc != null && BoolOf(Get(enc, RangeRingSpecs.EncUnitsLoaded)) ? enc : null;
        }

        /// <summary>The encyclopedia is built (it is rebuilt when the display names change).</summary>
        internal bool RrEncyclopediaReady()
        {
            try { return RrEncyclopedia() != null; } catch { return false; }
        }

        private static double RrNm(GameApi a, object length)
        {
            var d = length == null ? null : D(a.Get(length, RangeRingSpecs.LengthNm));
            return d.HasValue && !double.IsNaN(d.Value) ? d.Value : 0;
        }

        /// <summary>The offensive weapons and the passive sonar ranges of the entry (IniRef == iniRef, VariantIndex == 0: the class without a load-out). false = no entry.</summary>
        internal bool RrClassEntry(string iniRef, List<ClassWeapon> weapons, List<double> passiveNm)
        {
            try
            {
                var enc = RrEncyclopedia();
                var entries = enc == null ? null : Get(enc, RangeRingSpecs.EncEntries) as IEnumerable;
                if (entries == null || iniRef == null) return false;
                object entry = null;
                foreach (var e in entries)
                {
                    if (e == null) continue;
                    if ((Get(e, RangeRingSpecs.EntryIniRef) as string) == iniRef && D(Get(e, RangeRingSpecs.EntryVariantIndex)) == 0) { entry = e; break; }
                }
                if (entry == null) return false;
                var ws = Get(entry, RangeRingSpecs.EntryWeapons) as IEnumerable;
                if (ws != null)
                    foreach (var w in ws)
                    {
                        if (w == null) continue;
                        weapons.Add(new ClassWeapon
                        {
                            Key = Get(w, RangeRingSpecs.EntryIniRef) as string,
                            Name = Get(w, InterceptSpecs.EntryDisplayName) as string,
                            Air = BoolOf(Get(w, RangeRingSpecs.WeaponAirLaunched)),
                            Primary = RrTarget(Get(w, RangeRingSpecs.WeaponTargetType)),
                            Secondary = RrTarget(Get(w, RangeRingSpecs.WeaponSecondaryTargetType)),
                            HasSecondary = BoolOf(Get(w, RangeRingSpecs.WeaponHasSecondary)),
                            Type = RrAmmoType(Get(w, RangeRingSpecs.WeaponType)),
                            MinNm = RrNm(this, Get(w, RangeRingSpecs.WeaponMinRange)),
                            MaxNm = RrNm(this, Get(w, RangeRingSpecs.WeaponMaxRange)),
                            GunAaNm = RrNm(this, Get(w, RangeRingSpecs.WeaponGunAaRange)),
                        });
                    }
                var ss = Get(entry, RangeRingSpecs.EntrySensors) as IEnumerable;
                if (ss != null)
                    foreach (var s in ss)
                        if (s != null && IsOfType(s, RangeRingSpecs.SonarEntryType)) passiveNm.Add(RrNm(this, Get(s, RangeRingSpecs.SonarEntryPassive)));
                return true;
            }
            catch { return false; }
        }

        // ---- T-171 / ADR-0068: the mounts of the CLASS ini and the course of the TRACK ----
        private readonly Dictionary<string, int> _rrMountMiss = new Dictionary<string, int>();

        private string RrIniText(object ini, string section, string key)
        {
            string v = HeIni(ini, section, key);
            if (v == null) return null;
            int cut = v.IndexOf("//", StringComparison.Ordinal);
            if (cut >= 0) v = v.Substring(0, cut);
            v = v.Trim();
            return v.Length == 0 ? null : v;
        }

        private void RrAddAmmoRef(object ini, ClassMount m, string name)
        {
            if (string.IsNullOrEmpty(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase)) return;
            if (name.StartsWith("DateBased_", StringComparison.OrdinalIgnoreCase))
            {
                if (!Present(RangeRingSpecs.UtilsResolveDateAmmo)) return;
                try { name = CallStatic(RangeRingSpecs.UtilsResolveDateAmmo, ini, name) as string; } catch { return; }
                if (string.IsNullOrEmpty(name)) return;
            }
            m.AmmoRefs.Add(name);
        }

        /// <summary>The mounts (type, system, ammunition files, firing arcs) of the CLASS ini by its name: the game's loader (vessels, then land_units), no unit instance.
        /// false = no ini (aircraft, loader missing). Sections of load-outs are not read. A miss is not asked again for 10 s.</summary>
        internal bool RrClassMounts(string iniName, List<ClassMount> into)
        {
            if (string.IsNullOrEmpty(iniName) || into == null) return false;
            int at;
            if (_rrMountMiss.TryGetValue(iniName, out at) && unchecked(Environment.TickCount - at) < 10000) return false;
            try
            {
                object p = null;
                if (Present(HitEstimateSpecs.FindCached)) p = CallStatic(HitEstimateSpecs.FindCached, "vessels", iniName) ?? CallStatic(HitEstimateSpecs.FindCached, "land_units", iniName);
                if (p == null && Present(HitEstimateSpecs.LoadSimple))
                {
                    foreach (var folder in new[] { "vessels", "land_units" })
                    {
                        try { p = CallStatic(HitEstimateSpecs.LoadSimple, iniName, folder, 0); } catch { p = null; }
                        if (p != null && StrOrNull(Get(p, HitEstimateSpecs.SopObjectType)) != "Other") break;
                        p = null;
                    }
                }
                var ini = p == null ? null : Get(p, HitEstimateSpecs.SopIni);
                if (ini == null) { _rrMountMiss[iniName] = Environment.TickCount; return false; }
                int n = (int)HeNum(RrIniText(ini, "WeaponSystems", "NumberOfWeaponSystems"), 0);
                for (int k = 1; k <= n && k <= 64; k++)
                {
                    string sec = "WeaponSystem" + k.ToString(CultureInfo.InvariantCulture);
                    var m = new ClassMount { Type = RrIniText(ini, sec, "Type"), System = RrIniText(ini, sec, "SystemName") };
                    m.Arcs = RangeRingMath.ParseArcs(RrIniText(ini, sec, "FiringArcs"));
                    RrAddAmmoRef(ini, m, RrIniText(ini, sec, "Ammunition"));
                    string mag = RrIniText(ini, sec, "AssociatedMagazine");
                    if (mag != null)
                    {
                        int cnt = (int)HeNum(RrIniText(ini, mag, "NumberOfAmmunitionTypes"), 0);
                        for (int a = 1; a <= cnt && a <= 32; a++) RrAddAmmoRef(ini, m, RrIniText(ini, mag, "Ammunition" + a.ToString(CultureInfo.InvariantCulture)));
                    }
                    into.Add(m);
                }
                return true;
            }
            catch { _rrMountMiss[iniName] = Environment.TickCount; return false; }
        }

        /// <summary>The course of the player's TRACK from the velocity of the track (never the object). false = no track / slower than minKn.</summary>
        internal bool RrTrackCourse(int trackId, double minKn, out double courseDeg)
        {
            courseDeg = 0;
            try
            {
                var v = RrVehicle(trackId);
                if (v == null) return false;
                double tlat, tlon;
                if (!GeoOf(RxGet(v, ContactInfoSpecs.VehPosition), out tlat, out tlon)) { tlat = double.NaN; tlon = double.NaN; }   // the track's position only (T-171: never the object)
                double vx, vz;
                if (!GridVelTrue(v, ContactInfoSpecs.VehVelocity, ContactInfoSpecs.VecX, ContactInfoSpecs.VecZ, tlat, tlon, out vx, out vz)) return false;   // T-308: true axes
                return RangeRingMath.CourseFromVel(vx * OverviewMath.KnPerUnity, vz * OverviewMath.KnPerUnity, minKn, out courseDeg);
            }
            catch { return false; }
        }
    }
}
