// T-095 stream B: ASW game primitives (sonobuoys, dipping sonar, sonobuoy depth mode, "asw" block). No state besides caches.
// Interface mod only (customer decision 2026-10-19: the voice mod is paused, ADR-0033). Types GeoPt/BuoyType/AswParams/AswMath are in AswMath.cs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        private const int AswMaxPoints = 40;

        /// <summary>Factors for R_eff (config [Asw]); the UI mod may replace it. Changing it drops the range cache.</summary>
        internal AswParams AswRangeParams
        {
            get { return _aswParams; }
            set { _aswParams = value ?? new AswParams(); _aswRangeCache.Clear(); }
        }
        private AswParams _aswParams = new AswParams();
        private readonly Dictionary<string, KeyValuePair<double, string>> _aswRangeCache = new Dictionary<string, KeyValuePair<double, string>>();
        private readonly Dictionary<string, BuoyType> _aswBuoyCache = new Dictionary<string, BuoyType>();
        internal void AswLeaveMission() { _aswEnvRef = null; _aswRangeCache.Clear(); _aswBuoyCache.Clear(); _dipTypeCache.Clear(); _dipSystemCache.Clear(); }   // T-130, T-188
        private object _aswEnvRef; // Globals._sonarEnvironmentParameters: a new object means a new mission, caches are dropped

        // ---------- hooks of Common/Aviation.cs ----------

        partial void AswAirUnitHook(object u, StringBuilder sb)
        {
            if (!Capabilities.Contains("asw_info")) return;
            try { sb.Append(",\"asw\":").Append(AswJson(u)); } catch { }
        }

        // ---------- small helpers ----------

        private void AswEnvCheck()
        {
            var env = GetStatic(AswSpecs.SonarEnvironment);
            if (!ReferenceEquals(env, _aswEnvRef)) { _aswEnvRef = env; _aswRangeCache.Clear(); _aswBuoyCache.Clear(); _dipTypeCache.Clear(); _dipSystemCache.Clear(); }
        }

        private static string AswEnumName(object o) { return o == null ? null : o.ToString(); }

        /// <summary>Own ammunition: key (file name) and count, only count &gt; 0.</summary>
        private List<KeyValuePair<string, int>> AmmoOf(object u)
        {
            var res = new List<KeyValuePair<string, int>>();
            var dict = Get(u, CoreSpecs.AmmoCounts) as IEnumerable;
            if (dict == null) return res;
            foreach (var kv in dict)
            {
                var key = Get(kv, CoreSpecs.PairKey) as string;
                var val = D(Get(kv, CoreSpecs.PairValue));
                if (key != null && val.HasValue && val.Value > 0) res.Add(new KeyValuePair<string, int>(key, (int)val.Value));
            }
            return res;
        }

        private int AmmoCount(object u, string ammo)
        {
            try { var n = D(Call(u, AswSpecs.AmmoAmountByName, ammo)); return n.HasValue ? (int)n.Value : 0; } catch { return 0; }
        }

        private object AmmoParams(object u, string ammo)
        {
            object a = null;
            try { a = Call(u, CoreSpecs.AmmoByName, ammo); } catch { }
            return Get(a, AswSpecs.AmmoParamsOf);
        }

        private bool IsSonobuoyAp(object ap) { return AswEnumName(Get(ap, AswSpecs.ApSubType)) == "Sonobuoy"; }

        private string FirstSensor(object ap)
        {
            var list = Get(ap, AswSpecs.ApSonarList) as string;
            if (string.IsNullOrEmpty(list)) return null;
            var first = list.Split(',')[0].Trim();
            return first.Length == 0 ? null : first;
        }

        /// <summary>SonarParameters (cached by the game for the encyclopedia is private, so we build our own and drop it).</summary>
        private object SonarParamsOf(string sensor)
        {
            if (string.IsNullOrEmpty(sensor)) return null;
            try { return New(AswSpecs.SonarCtor, sensor); } catch { return null; }
        }

        private double GameEstimateNm(string sensor)
        {
            if (string.IsNullOrEmpty(sensor) || !Present(AswSpecs.ScorerEstimate)) return 0;
            try
            {
                // SensorData: none (encyclopedia call); host speed 12 kn (as the encyclopedia); the last flag keeps its default
                var r = D(CallStatic(AswSpecs.ScorerEstimate, sensor, null, 12f, DefaultFor(((MethodInfo)Member(AswSpecs.ScorerEstimate)).GetParameters()[3])));
                return r.HasValue && !double.IsNaN(r.Value) && !double.IsInfinity(r.Value) ? r.Value : 0;
            }
            catch { return 0; }
        }

        private BuoyType MakeBuoyType(string ammo, object ap)
        {
            var b = new BuoyType();
            b.Ammo = ammo;
            b.Sensor = FirstSensor(ap);
            b.LifeS = D(Get(ap, AswSpecs.ApLifeTime)) ?? 0;
            var sd = D(Get(ap, AswSpecs.ApSonarDepth));
            b.SonarDepthFt = sd.HasValue && !double.IsNaN(sd.Value) && sd.Value != 0 ? Math.Abs(sd.Value) / 0.00453571 : 0;   // T-176 G1
            var sp = SonarParamsOf(b.Sensor);
            b.PassiveKm = D(Get(sp, AswSpecs.SonarPassiveKm)) ?? 0;
            b.ActiveKm = D(Get(sp, AswSpecs.SonarActiveKm)) ?? 0;
            b.Active = b.PassiveKm <= 0 && b.ActiveKm > 0;
            var zones = D(Get(sp, AswSpecs.SonarZones)) ?? 0;
            b.Vlad = zones > 1 || (b.Sensor != null && b.Sensor.IndexOf("77", StringComparison.Ordinal) >= 0 && b.Sensor.IndexOf("SSQ", StringComparison.OrdinalIgnoreCase) >= 0);
            b.GameEstimateNm = b.Active ? 0 : GameEstimateNm(b.Sensor);
            return b;
        }

        private static GeoPt? ReadPoint(object o)
        {
            var arr = o as IList;
            if (arr == null || arr.Count < 2) return null;
            var la = D(arr[0]); var lo = D(arr[1]);
            if (!la.HasValue || !lo.HasValue) return null;
            if (double.IsNaN(la.Value) || double.IsNaN(lo.Value) || Math.Abs(la.Value) > 90 || Math.Abs(lo.Value) > 180) return new GeoPt(double.NaN, double.NaN);
            return new GeoPt(la.Value, lo.Value);
        }

        /// <summary>points:[[lat,lon],...] -> list; err = bad_args (shape/size) or bad_point (out of range).</summary>
        private static List<GeoPt> ReadPoints(Dictionary<string, object> cmd, out string err)
        {
            err = null;
            object o; cmd.TryGetValue("points", out o);
            var arr = o as IList;
            if (arr == null || arr.Count == 0 || arr.Count > AswMaxPoints) { err = "bad_args"; return null; }
            var res = new List<GeoPt>();
            foreach (var p in arr)
            {
                var g = ReadPoint(p);
                if (!g.HasValue) { err = "bad_args"; return null; }
                if (double.IsNaN(g.Value.Lat)) { err = "bad_point"; return null; }
                res.Add(g.Value);
            }
            return res;
        }

        /// <summary>Altitude carried by the waypoint: the demanded one (as PlaceWaypoint), else the current; for a buoy clamped into the launch window.</summary>
        private double AswWaypointHeight(object u, object ap)
        {
            double lat, lon, h;
            if (!OwnPos(u, out lat, out lon, out h)) h = 0;
            var da = D(RxGet(u, AirOpsSpecs.ObjDesiredAltitude));
            if (da.HasValue && da.Value > 0) h = da.Value;
            var win = Get(ap, AswSpecs.ApLaunchAltitudes);
            if (win != null)
            {
                var lo = D(Get(win, AswSpecs.Vec2X)); var hi = D(Get(win, AswSpecs.Vec2Y));
                if (lo.HasValue && hi.HasValue && hi.Value > lo.Value)
                {
                    double margin = Math.Min(0.05 * (hi.Value - lo.Value), 5);
                    if (h < lo.Value + margin) h = lo.Value + margin;
                    if (h > hi.Value - margin) h = hi.Value - margin;
                }
            }
            return h;
        }

        // ---------- T-188: the dipping sonar of a TYPE (what the game loads from the ini), for the deck rows that have no unit ----------

        private readonly Dictionary<string, AswDipTasks.DipSonarInfo> _dipTypeCache = new Dictionary<string, AswDipTasks.DipSonarInfo>();
        private readonly Dictionary<string, bool> _dipSystemCache = new Dictionary<string, bool>();

        private bool IsDippingSystem(string name)
        {
            bool b;
            if (_dipSystemCache.TryGetValue(name, out b)) return b;
            var sp = SonarParamsOf(name);
            b = sp != null && Present(AswSpecs.SonarTypeField) && AswEnumName(Get(sp, AswSpecs.SonarTypeField)) == "DippingSonar";
            _dipSystemCache[name] = b;
            return b;
        }

        /// <summary>Null = unreadable (the caller keeps the old guess by the load).</summary>
        internal AswDipTasks.DipSonarInfo TypeDipSonar(string vehicleFile, string loadout)
        {
            if (string.IsNullOrEmpty(vehicleFile)) return null;
            string key = vehicleFile + "|" + loadout;
            AswDipTasks.DipSonarInfo info;
            if (_dipTypeCache.TryGetValue(key, out info)) return info;
            AswEnvCheck();
            try
            {
                if (!Present(AswSpecs.MetaFindCached) || !Present(AswSpecs.SopIni) || !Present(AswSpecs.IniReadStr) || !Present(AswSpecs.IniHasSection) || !Present(AswSpecs.SonarTypeField)) return null;
                var sop = CallStatic(AswSpecs.MetaFindCached, "aircraft", vehicleFile);
                if (sop == null && Present(AswSpecs.SopLoadSimple)) sop = CallStatic(AswSpecs.SopLoadSimple, vehicleFile, "aircraft", 0);
                var ini = sop == null ? null : Get(sop, AswSpecs.SopIni);
                if (ini == null) return null;
                info = AswDipTasks.DipSonarOfIni(s => (bool)Call(ini, AswSpecs.IniHasSection, s), (s, k) => Call(ini, AswSpecs.IniReadStr, s, k, "", false) as string, loadout, IsDippingSystem, null);
                _dipTypeCache[key] = info;
                Log("asw dip type " + vehicleFile + " loadout=" + loadout + " loaded=" + info.Loaded + " sys=" + info.System + " via=" + info.Via + " note=" + AswDipTasks.DipNote(info));
                return info;
            }
            catch { return null; }
        }

        private bool HasSensorSystem(object u, string typeName, bool dipping)
        {
            var list = Get(Get(u, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpSensorSystems) as IEnumerable;
            if (list == null) return false;
            foreach (var s in list)
            {
                if (s == null || s.GetType().Name != typeName) continue;
                if (!dipping || BoolOf(Get(s, AswSpecs.SensorDipping))) return true;
            }
            return false;
        }

        // ---------- controller API (no JSON, main thread) ----------

        internal List<BuoyType> BuoyTypesOn(object unit)
        {
            var res = new List<BuoyType>();
            if (unit == null) return res;
            AswEnvCheck();
            foreach (var kv in AmmoOf(unit))
            {
                var ap = AmmoParams(unit, kv.Key);
                if (ap == null || !IsSonobuoyAp(ap)) continue;
                BuoyType b;
                if (!_aswBuoyCache.TryGetValue(kv.Key, out b)) { b = MakeBuoyType(kv.Key, ap); _aswBuoyCache[kv.Key] = b; }
                res.Add(b);
            }
            return res;
        }

        /// <summary>Ammunition count of a sonobuoy type on the unit (for the not_enough detail).</summary>
        internal int BuoyCount(object unit, string ammo) { return unit == null ? 0 : AmmoCount(unit, ammo); }

        internal string DropSonobuoys(object unit, string ammo, IList<GeoPt> pts, out int queued)
        {
            queued = 0;
            if (!Capabilities.Contains("asw_info")) return "unsupported";
            if (unit == null || !IsAirUnit(unit)) return "not_air_unit";
            if (pts == null || pts.Count == 0 || pts.Count > AswMaxPoints) return "bad_args";
            foreach (var p in pts)
                if (double.IsNaN(p.Lat) || double.IsNaN(p.Lon) || Math.Abs(p.Lat) > 90 || Math.Abs(p.Lon) > 180) return "bad_point";
            if (string.IsNullOrEmpty(ammo)) ammo = AswMath.PickBuoy(BuoyTypesOn(unit), false, _aswParams);
            if (string.IsNullOrEmpty(ammo)) return "no_sonobuoy";
            var ap = AmmoParams(unit, ammo);
            if (ap == null || !IsSonobuoyAp(ap)) return "no_sonobuoy";
            int have = AmmoCount(unit, ammo);
            if (have <= 0) return "no_sonobuoy";
            if (have < pts.Count) return "not_enough";
            if (!Present(AswSpecs.AttackAtWaypoint)) return "unsupported";
            double h = AswWaypointHeight(unit, ap);
            object after = null;
            try
            {
                foreach (var p in pts)
                {
                    var geo = MakeGeo(p.Lat, p.Lon, h);
                    // ammo, targetObject (none for a buoy), targetGeoPosition, waypointGeoPosition, salvo, previous task (chains the points in order); the rest keeps its defaults
                    var task = CallOpt(unit, AswSpecs.AttackAtWaypoint, ammo, null, geo, geo, 1, after);
                    if (task == null) return "drop_failed";
                    after = task;
                    queued++;
                }
            }
            catch { return "drop_failed"; }
            return null;
        }

        internal string DipSonar(object unit, IList<GeoPt> pts)
        {
            if (!Capabilities.Contains("asw_info")) return "unsupported";
            if (unit == null || !IsAirUnit(unit)) return "not_air_unit";
            if (pts == null || pts.Count == 0 || pts.Count > AswMaxPoints) return "bad_args";
            foreach (var p in pts)
                if (double.IsNaN(p.Lat) || double.IsNaN(p.Lon) || Math.Abs(p.Lat) > 90 || Math.Abs(p.Lon) > 180) return "bad_point";
            if (!HasSensorSystem(unit, "SensorSystemSonar", true)) return "no_dipping_sonar";
            double h = AswWaypointHeight(unit, null);
            object after = null;
            try
            {
                foreach (var p in pts)
                {
                    var geo = MakeGeo(p.Lat, p.Lon, h);
                    var task = Call(unit, AswSpecs.DipSonarAtWaypoint, geo, after);
                    if (task == null) return "dip_failed";
                    after = task;
                }
            }
            catch { return "dip_failed"; }
            return null;
        }

        /// <summary>Current mode in the command vocabulary: above | below | alternate (null if unreadable).</summary>
        internal string GetSonoDepth(object unit)
        {
            var v = AswEnumName(RxGet(unit, AswSpecs.SonoDeploy));
            if (v == "AboveLayer") return "above";
            if (v == "BelowLayer") return "below";
            if (v == "Intermittent") return "alternate";
            return null;
        }

        internal string SetSonoDepth(object unit, string mode, out string previous)
        {
            previous = null;
            if (!Capabilities.Contains("asw_info")) return "unsupported";
            if (unit == null || !IsAirUnit(unit)) return "not_air_unit";
            string enumName = AswMath.ParseDepth(mode);
            if (enumName == null) return "bad_args";
            var et = TypeOf(AswSpecs.DeployEnum);
            if (et == null) return "unsupported";
            previous = GetSonoDepth(unit);
            try { RxSet(unit, AswSpecs.SonoDeploy, Enum.Parse(et, enumName)); }
            catch { return "depth_failed"; }
            return null;
        }

        /// <summary>R_eff (nm) for a sensor with the source tag (game|ini|active|clamped:...); cached until the mission environment changes.</summary>
        internal double SonobuoyRangeNm(string sensor, out string source)
        {
            source = null;
            if (string.IsNullOrEmpty(sensor)) return 0;
            AswEnvCheck();
            KeyValuePair<double, string> hit;
            if (_aswRangeCache.TryGetValue(sensor, out hit)) { source = hit.Value; return hit.Key; }
            var b = new BuoyType();
            b.Sensor = sensor;
            var sp = SonarParamsOf(sensor);
            b.PassiveKm = D(Get(sp, AswSpecs.SonarPassiveKm)) ?? 0;
            b.ActiveKm = D(Get(sp, AswSpecs.SonarActiveKm)) ?? 0;
            b.Active = b.PassiveKm <= 0 && b.ActiveKm > 0;
            b.GameEstimateNm = b.Active ? 0 : GameEstimateNm(sensor);
            var r = AswMath.REff(b, _aswParams);
            _aswRangeCache[sensor] = new KeyValuePair<double, string>(r.Nm, r.Source);
            source = r.Source;
            return r.Nm;
        }

        // ---------- state JSON ----------

        /// <summary>"asw" block of an air unit (ADR-0036 section 8).</summary>
        internal string AswJson(object unit)
        {
            var sb = new StringBuilder("{\"buoys\":{");
            int passive = 0, active = 0, vlad = 0, torp = 0;
            bool first = true;
            foreach (var kv in AmmoOf(unit))
            {
                var ap = AmmoParams(unit, kv.Key);
                if (ap == null) continue;
                if (IsSonobuoyAp(ap))
                {
                    BuoyType b;
                    AswEnvCheck();
                    if (!_aswBuoyCache.TryGetValue(kv.Key, out b)) { b = MakeBuoyType(kv.Key, ap); _aswBuoyCache[kv.Key] = b; }
                    if (!first) sb.Append(','); first = false;
                    sb.Append(Json.Str(kv.Key)).Append(':').Append(kv.Value);
                    if (b.Vlad) vlad += kv.Value; else if (b.Active) active += kv.Value; else passive += kv.Value;
                }
                else if (AswEnumName(Get(ap, AswSpecs.ApType)) == "Torpedo") torp += kv.Value;
            }
            sb.Append("},\"passive\":").Append(passive).Append(",\"active\":").Append(active).Append(",\"vlad\":").Append(vlad);
            sb.Append(",\"torpedoes\":").Append(torp);
            sb.Append(",\"dipping\":").Append(Json.Bool(HasSensorSystem(unit, "SensorSystemSonar", true)));
            sb.Append(",\"mad\":").Append(Json.Bool(HasSensorSystem(unit, "SensorSystemMAD", false)));
            try
            {
                var obp = Get(unit, AirOpsSpecs.ObjObp);
                var tinfo = TypeDipSonar(Get(obp, AirOpsSpecs.ObpIniName) as string, Get(obp, AirOpsSpecs.ObpLoadoutVariant) as string);   // T-188: why the type's sonar is not loaded (ui_state)
                string dn = AswDipTasks.DipNote(tinfo);
                if (dn != null) sb.Append(",\"dip_note\":").Append(Json.Str(dn));
            }
            catch { }
            var rng = D(RxGet(unit, AirOpsSpecs.ObjRangeKm));
            var max = D(Get(Get(unit, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpMaxRangeKm));
            if (rng.HasValue && max.HasValue && max.Value > 0)
                sb.Append(",\"fuel_frac\":").Append(Json.Num(Math.Round(Math.Max(0, Math.Min(1, rng.Value / max.Value)), 3)));
            else sb.Append(",\"fuel_frac\":null");
            var en = D(RxGet(unit, AirOpsSpecs.AircraftEndurance)) ?? D(RxGet(unit, AswSpecs.HeliEndurance));   // remaining seconds
            if (en.HasValue && !double.IsInfinity(en.Value) && !double.IsNaN(en.Value)) sb.Append(",\"endurance_s\":").Append(Json.Num(Math.Round(en.Value)));
            else sb.Append(",\"endurance_s\":null");
            sb.Append(",\"ai_state\":").Append(Json.Str(StateName(unit, "CurrentStateName")));
            sb.Append(",\"ready_in_s\":0}"); // an air unit in the list is already flying; deck readiness comes from decks[].tasks
            return sb.ToString();
        }

        /// <summary>sonobuoy_types[]: one entry per sonobuoy type carried by any own air unit.</summary>
        internal string SonobuoyTypesJson()
        {
            var seen = new HashSet<string>();
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var u in AirUnits())
            {
                List<BuoyType> types;
                try { types = BuoyTypesOn(u); } catch { continue; }
                foreach (var b in types)
                {
                    if (!seen.Add(b.Ammo)) continue;
                    string src;
                    double r = SonobuoyRangeNm(b.Sensor, out src);
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"ammo\":").Append(Json.Str(b.Ammo)).Append(",\"sensor\":").Append(Json.Str(b.Sensor));
                    sb.Append(",\"r_eff_nm\":").Append(Json.Num(Math.Round(r, 2))).Append(",\"source\":").Append(Json.Str(src));
                    sb.Append(",\"life_s\":").Append(Json.Num(Math.Round(b.LifeS))).Append(",\"active\":").Append(Json.Bool(b.Active));
                    sb.Append(",\"vlad\":").Append(Json.Bool(b.Vlad)).Append('}');
                }
            }
            return sb.Append(']').ToString();
        }

        // ---------- commands (JSON) ----------

        private object AswUnit(string id, Dictionary<string, object> cmd, out string errJson)
        {
            errJson = null;
            Need("asw_info");
            string err;
            var u = Target(cmd, out err);
            if (u == null) { errJson = Err(id, err, null); return null; }
            if (!IsAirUnit(u)) { errJson = Err(id, "not_air_unit", null); return null; }
            return u;
        }

        internal string DropSonobuoyCmd(string id, Dictionary<string, object> cmd)
        {
            string ej;
            var u = AswUnit(id, cmd, out ej);
            if (u == null) return ej;
            string perr;
            var pts = ReadPoints(cmd, out perr);
            if (pts == null) return Err(id, perr, null);
            string ammo = cmd.ContainsKey("ammo") ? cmd["ammo"] as string : null;
            string depth = cmd.ContainsKey("depth") ? cmd["depth"] as string : null;
            string prev = null;
            if (!string.IsNullOrEmpty(depth))
            {
                if (AswMath.ParseDepth(depth) == null) return Err(id, "bad_args", "depth above|below|alternate");
            }
            int queued;
            // depth is set only after the drop passed all checks, so a refused command leaves the mode untouched
            string e = DropSonobuoys(u, ammo, pts, out queued);
            if (e != null)
            {
                string used = string.IsNullOrEmpty(ammo) ? AswMath.PickBuoy(BuoyTypesOn(u), false, _aswParams) : ammo;
                int have = used == null ? 0 : AmmoCount(u, used);
                if (e == "not_enough")
                    return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":false,\"error\":\"not_enough\",\"available\":" + have + "}";
                return Err(id, e, null);
            }
            if (!string.IsNullOrEmpty(depth)) SetSonoDepth(u, depth, out prev);
            string usedAmmo = string.IsNullOrEmpty(ammo) ? AswMath.PickBuoy(BuoyTypesOn(u), false, _aswParams) : ammo;
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"ammo\":" + Json.Str(usedAmmo) + ",\"queued\":" + queued +
                ",\"available\":" + (usedAmmo == null ? 0 : AmmoCount(u, usedAmmo)) +
                (depth == null ? "" : ",\"depth\":" + Json.Str(depth) + ",\"depth_before\":" + Json.Str(prev)));
        }

        internal string DipSonarCmd(string id, Dictionary<string, object> cmd)
        {
            string ej;
            var u = AswUnit(id, cmd, out ej);
            if (u == null) return ej;
            string perr;
            var pts = ReadPoints(cmd, out perr);
            if (pts == null) return Err(id, perr, null);
            string e = DipSonar(u, pts);
            if (e != null) return Err(id, e, null);
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"queued\":" + pts.Count);
        }

        internal string SetSonobuoyDepthCmd(string id, Dictionary<string, object> cmd)
        {
            string ej;
            var u = AswUnit(id, cmd, out ej);
            if (u == null) return ej;
            string mode = cmd.ContainsKey("mode") ? cmd["mode"] as string : null;
            string prev;
            string e = SetSonoDepth(u, mode, out prev);
            if (e != null) return Err(id, e, e == "bad_args" ? "mode above|below|alternate" : null);
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"mode\":" + Json.Str(GetSonoDepth(u)) + ",\"previous\":" + Json.Str(prev));
        }
    }
}
