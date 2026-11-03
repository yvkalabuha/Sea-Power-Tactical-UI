using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>Per-unit override of the tactical flags; null = "not set, use the default of the unit kind".</summary>
    internal sealed class UnitFlags { public bool? AutoEvade, VetoNeutral; public string PostLaunch; }

    /// <summary>
    /// v0.9.0 (T-064): unit flags (auto_evade, veto_neutral, post_launch) with a doctrine-like hierarchy
    /// unit value -> default of the unit kind (ship/air) -> built-in (off), auto torpedo evasion and the "after launch"
    /// behaviour of aircraft. Everything works through the game's own states/orders; the mod invents no new AI.
    /// Flags live until the mission changes; the bot restores its profile on mission start. Main thread only.
    /// </summary>
    internal sealed partial class GameApi
    {
        // Hooks of the interface mod (T-098 E, T-107 K). No body in the voice mod: the compiler removes the calls and
        // the evaluation of their arguments, so the voice behaviour does not change. Implemented only in SeaPowerTacticalUI.
        partial void FlagStoreResolve(object u, string flag, ref bool? value);
        partial void FlagStorePostLaunch(object u, ref string mode);
        partial void FlagStoreSource(object u, string flag, ref string src);
        partial void FlagStoreDefaultsJson(ref string json);
        partial void FlagStoreSetUnit(object u, bool? autoEvade, bool? vetoNeutral, string postLaunch);
        partial void FlagStoreSetDefaults(bool? shipAutoEvade, bool? shipVeto, bool? airVeto, string airPostLaunch);
        partial void FlagStoreMissionReset();
        /// <summary>Own torpedo evasion of the interface mod: handled=true means the native TorpedoEvasion path is skipped for this unit.</summary>
        partial void AutoEvadeHook(object u, double now, ref bool handled);

        public double VetoRadiusNm = 2.0, EvadeS = 20.0;
        public string HarmonyStatus = "off";
        public bool HarmonyVersionKnown { get; set; }
        public readonly List<string> PatchesApplied = new List<string>();

        private readonly Dictionary<object, UnitFlags> _flags = new Dictionary<object, UnitFlags>(new RefEq());
        private readonly UnitFlags _defShip = new UnitFlags { AutoEvade = false, VetoNeutral = false };
        private readonly UnitFlags _defAir = new UnitFlags { VetoNeutral = false, PostLaunch = "off" };
        private object _tacticsOm;
        private double _lastMissionT = double.MaxValue;
        private double _nextTactics;

        private readonly Dictionary<object, double> _evadeStart = new Dictionary<object, double>(new RefEq());
        private readonly Dictionary<object, double> _evadeLast = new Dictionary<object, double>(new RefEq());

        private sealed class PlRun
        {
            public object Air, Target; public string Mode, Guidance; public int? TargetTrack;
            public List<double[]> Saved; public double[] Placed; public double StartT, Course;
        }
        private readonly Dictionary<object, PlRun> _plRuns = new Dictionary<object, PlRun>(new RefEq());
        private readonly Dictionary<object, object> _plWeapons = new Dictionary<object, object>(new RefEq()); // seen weapon -> its target
        private bool _plPrimed;

        // ---------- self check ----------

        private void SelfCheckTactics(bool st)
        {
            EnsureActions();
            if (Actions.Count > 0) Capabilities.Add("actions");

            bool fl = st;
            fl &= Has("unit_flags", "SeaPower.ObjectBase", "IsPlayerObject", false, true);
            fl &= Has("unit_flags", "SeaPower.ObjectBase", "UniqueID", false, true);
            if (fl) Capabilities.Add("unit_flags");

            bool ae = fl && Capabilities.Contains("contacts");
            ae &= Has("auto_evade", "SeaPower.Vessel", "_state_torpedoEvasion", false, true);
            ae &= Has("auto_evade", "SeaPower.Submarine", "_state_torpedoEvasion", false, true);
            ae &= Has("auto_evade", "SeaPower.ObjectBase", "ThreatingTorpedo", false, true);
            ae &= Has("auto_evade", "SeaPower.ObjectBase", "StateMachine", false, true);
            ae &= Has("auto_evade", "SeaPower.StateMachine", "CurrentState", false, true);
            ae &= SetStateMethod() != null;
            Checks.Add(new Check { Capability = "auto_evade", Member = "SeaPower.StateMachine.setState(IState)", Required = true, Present = SetStateMethod() != null });
            var cs = Find(FindType("SeaPower.StateMachine"), "CurrentState", false) as PropertyInfo;
            bool prio = cs != null && cs.PropertyType.GetProperty("Priority") != null;
            Checks.Add(new Check { Capability = "auto_evade", Member = "IState.Priority", Required = true, Present = prio });
            if (ae && prio) Capabilities.Add("auto_evade");

            bool pl = fl && Capabilities.Contains("weapons") && Capabilities.Contains("attack") && Capabilities.Contains("waypoints");
            pl &= Has("post_launch", "SeaPower.ObjectBaseParameters", "_weaponSystems", false, true);
            pl &= Has("post_launch", "SeaPower.WeaponSystem", "_launchedWeapons", false, true);
            pl &= Has("post_launch", "SeaPower.WeaponBase", "_launchPlatform", false, true);
            pl &= Has("post_launch", "SeaPower.WeaponBase", "_initialSetTargetObject", false, true);
            Has("post_launch", "SeaPower.WeaponBase", "_lastTargetObject", false, false);
            pl &= Has("post_launch", "SeaPower.WeaponBase", "_ap", false, true);
            pl &= Has("post_launch", "SeaPower.AmmunitionParameters", "_guidanceType", false, true);
            pl &= Has("post_launch", "SeaPower.AmmunitionParameters", "_midCourseCorrection", false, true);
            Has("post_launch", "SeaPower.AlignUnitToTarget", "_isGuidingWeapon", false, false);
            Has("post_launch", "SeaPower.ObjectBase", "_alignmentTasks", false, false);
            if (pl) Capabilities.Add("post_launch");

            if (st && Has("game_options", "SeaPower.OptionsManager", "AircraftMissileEvasion", false, true)) Capabilities.Add("game_options");
            if (Capabilities.Contains("set_course") && Capabilities.Contains("contacts")) Capabilities.Add("course_from_threat");
            if (fl && Capabilities.Contains("set_roe") && Capabilities.Contains("waypoints")) Capabilities.Add("restore_control"); // T-091
        }

        private MethodInfo SetStateMethod()
        {
            var t = FindType("SeaPower.StateMachine");
            if (t == null) return null;
            foreach (var m in t.GetMethods(AllInst))
                if (m.Name == "setState" && m.GetParameters().Length == 1) return m;
            return null;
        }

        /// <summary>Mod event: Harmony patches are applied on a game version that is not in the verified list (they stay on).</summary>
        internal void EmitHarmonyWarning(string version, string known)
        {
            EvLine("harmony_unverified_version", "{\"game_version\":" + Json.Str(version) + ",\"known\":" + Json.Str(known) + "}");
        }

        // ---------- mission reset ----------

        /// <summary>Flags and runs belong to one mission: a new ObjectsManager instance or a time jump back clears them.</summary>
        private void TacticsEnsureMission()
        {
            var om = ObjectsManagerInstance();
            var mt = MissionTimeS();
            bool changed = !ReferenceEquals(om, _tacticsOm) || (mt.HasValue && mt.Value + 2.0 < _lastMissionT && _lastMissionT != double.MaxValue);
            if (mt.HasValue) _lastMissionT = mt.Value;
            if (!changed) return;
            _tacticsOm = om;
            _flags.Clear(); _evadeStart.Clear(); _evadeLast.Clear(); _plRuns.Clear(); _plWeapons.Clear(); _plPrimed = false; _ctlMem.Clear();
            _defShip.AutoEvade = false; _defShip.VetoNeutral = false;
            _defAir.VetoNeutral = false; _defAir.PostLaunch = "off";
            VetoRadiusNm = 2.0; EvadeS = 20.0;
            FlagStoreMissionReset();
            _lastMissionT = mt ?? double.MaxValue;
        }

        // ---------- resolution ----------

        internal bool ResolveFlag(object u, string flag)
        {
            bool? ov = null;
            FlagStoreResolve(u, flag, ref ov);
            if (ov.HasValue) return ov.Value;
            UnitFlags f;
            if (u != null && _flags.TryGetValue(u, out f))
            {
                var v = flag == "auto_evade" ? f.AutoEvade : f.VetoNeutral;
                if (v.HasValue) return v.Value;
            }
            var d = UnitKindOfObj(u) == "air" ? _defAir : _defShip;
            return (flag == "auto_evade" ? d.AutoEvade : d.VetoNeutral) ?? false;
        }

        internal string ResolvePostLaunch(object u)
        {
            string plOv = null;
            FlagStorePostLaunch(u, ref plOv);
            if (plOv != null) return plOv;
            UnitFlags f;
            if (u != null && _flags.TryGetValue(u, out f) && f.PostLaunch != null) return f.PostLaunch;
            return _defAir.PostLaunch ?? "off";
        }

        private string FlagSrc(object u, string flag)
        {
            string srcOv = null;
            FlagStoreSource(u, flag, ref srcOv);
            if (srcOv != null) return srcOv;
            UnitFlags f;
            if (u != null && _flags.TryGetValue(u, out f))
            {
                if (flag == "auto_evade" && f.AutoEvade.HasValue) return "unit";
                if (flag == "veto_neutral" && f.VetoNeutral.HasValue) return "unit";
                if (flag == "post_launch" && f.PostLaunch != null) return "unit";
            }
            return "default";
        }

        internal string FlagsJson(object u)
        {
            return "{\"auto_evade\":" + Json.Bool(ResolveFlag(u, "auto_evade")) + ",\"veto_neutral\":" + Json.Bool(ResolveFlag(u, "veto_neutral")) +
                   ",\"post_launch\":" + Json.Str(ResolvePostLaunch(u)) + "}";
        }

        private string FlagsSrcJson(object u)
        {
            return "{\"auto_evade\":\"" + FlagSrc(u, "auto_evade") + "\",\"veto_neutral\":\"" + FlagSrc(u, "veto_neutral") + "\",\"post_launch\":\"" + FlagSrc(u, "post_launch") + "\"}";
        }

        internal string TacticsJson()
        {
            var sb = new StringBuilder("{\"harmony\":").Append(Json.Str(HarmonyStatus));
            sb.Append(",\"version_known\":").Append(Json.Bool(HarmonyVersionKnown));
            sb.Append(",\"patches\":[").Append(string.Join(",", PatchesApplied.ConvertAll(Json.Str).ToArray())).Append(']');
            sb.Append(",\"veto_radius_nm\":").Append(Json.Num(VetoRadiusNm)).Append(",\"evade_s\":").Append(Json.Num(EvadeS));
            return sb.Append('}').ToString();
        }

        private string DefaultsJson()
        {
            string json = "{\"ship\":{\"auto_evade\":" + Json.Bool(_defShip.AutoEvade ?? false) + ",\"veto_neutral\":" + Json.Bool(_defShip.VetoNeutral ?? false) +
                   "},\"air\":{\"post_launch\":" + Json.Str(_defAir.PostLaunch ?? "off") + ",\"veto_neutral\":" + Json.Bool(_defAir.VetoNeutral ?? false) + "}}";
            FlagStoreDefaultsJson(ref json);
            return json;
        }

        // ---------- commands ----------

        private static bool IsMode(string v) { return v == "off" || v == "evade" || v == "retarget"; }

        private string FlagsCmd(string id, Dictionary<string, object> cmd)
        {
            TacticsEnsureMission();
            var sb = new StringBuilder("\"defaults\":" + DefaultsJson() + ",\"veto_radius_nm\":" + Json.Num(VetoRadiusNm) + ",\"evade_s\":" + Json.Num(EvadeS) + ",\"units\":[");
            bool first = true;
            if (InMission())
            {
                var shown = new List<object>();
                foreach (var kv in _flags) if (IsAlive(kv.Key)) shown.Add(kv.Key);
                string err;
                if (cmd.ContainsKey("uid") && cmd["uid"] != null)
                {
                    var one = Target(cmd, out err);
                    if (one == null) return Err(id, err, null);
                    if (!shown.Contains(one)) shown.Add(one);
                }
                foreach (var u in shown)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"uid\":").Append(UidOf(u)).Append(",\"name\":").Append(Json.Str(NameOf(u))).Append(",\"kind\":").Append(Json.Str(UnitKindOfObj(u)));
                    sb.Append(",\"flags\":").Append(FlagsJson(u)).Append(",\"src\":").Append(FlagsSrcJson(u)).Append('}');
                }
            }
            return Ok(id, sb.Append(']').ToString());
        }

        private string SetFlagsCmd(string id, Dictionary<string, object> cmd)
        {
            bool? ae = null, vn = null; string pl = null;
            if (cmd.ContainsKey("auto_evade")) { if (!(cmd["auto_evade"] is bool)) return Err(id, "bad_args", "auto_evade must be true/false"); ae = (bool)cmd["auto_evade"]; }
            if (cmd.ContainsKey("veto_neutral")) { if (!(cmd["veto_neutral"] is bool)) return Err(id, "bad_args", "veto_neutral must be true/false"); vn = (bool)cmd["veto_neutral"]; }
            if (cmd.ContainsKey("post_launch"))
            {
                pl = cmd["post_launch"] as string;
                if (pl == null || (!IsMode(pl) && pl != "default")) return Err(id, "bad_args", "post_launch: off|evade|retarget|default");
            }
            if (!ae.HasValue && !vn.HasValue && pl == null) return Err(id, "bad_args", "auto_evade, veto_neutral or post_launch required");
            if (pl == "retarget")
            {
                if (!AllowAttack) return Err(id, "attack_disabled", "AllowAttack=false in mod config");
                if (!Confirmed(cmd)) return Err(id, "confirm_required", "post_launch \"retarget\" needs \"confirm\":true");
            }
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            Need("unit_flags");
            string kind = UnitKindOfObj(u);
            if ((ae.HasValue && kind != "ship") || (pl != null && kind != "air") || kind == null)
                return Err(id, "not_applicable", ae.HasValue ? "auto_evade is for ships and submarines" : "post_launch is for aircraft");
            TacticsEnsureMission();
            string previous = FlagsJson(u);
            UnitFlags f;
            if (!_flags.TryGetValue(u, out f)) { f = new UnitFlags(); _flags[u] = f; }
            if (ae.HasValue) f.AutoEvade = ae;
            if (vn.HasValue) f.VetoNeutral = vn;
            if (pl != null) f.PostLaunch = pl == "default" ? null : pl;
            FlagStoreSetUnit(u, ae, vn, pl);
            string now = FlagsJson(u);
            if (EventsAvailable)
                EvLine("flags_changed", "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"flags\":" + now + "}");
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"flags\":" + now + ",\"previous\":" + previous);
        }

        private static string DefaultsSection(Dictionary<string, object> cmd, string key, string[] allowed, out Dictionary<string, object> sec)
        {
            sec = null;
            if (!cmd.ContainsKey(key) || cmd[key] == null) return null;
            sec = cmd[key] as Dictionary<string, object>;
            if (sec == null) return key + " must be an object";
            foreach (var k in sec.Keys) if (Array.IndexOf(allowed, k) < 0) return key + "." + k + " unknown";
            return null;
        }

        private string SetDoctrineDefaultsCmd(string id, Dictionary<string, object> cmd)
        {
            Dictionary<string, object> ship, air;
            var e = DefaultsSection(cmd, "ship", new[] { "auto_evade", "veto_neutral" }, out ship);
            var e2 = DefaultsSection(cmd, "air", new[] { "post_launch", "veto_neutral" }, out air);
            if (e == null) e = e2;
            if (e != null) return Err(id, "bad_args", e);
            bool? sAe = null, sVn = null, aVn = null; string aPl = null;
            if (ship != null)
            {
                if (ship.ContainsKey("auto_evade")) { if (!(ship["auto_evade"] is bool)) return Err(id, "bad_args", "ship.auto_evade must be true/false"); sAe = (bool)ship["auto_evade"]; }
                if (ship.ContainsKey("veto_neutral")) { if (!(ship["veto_neutral"] is bool)) return Err(id, "bad_args", "ship.veto_neutral must be true/false"); sVn = (bool)ship["veto_neutral"]; }
            }
            if (air != null)
            {
                if (air.ContainsKey("veto_neutral")) { if (!(air["veto_neutral"] is bool)) return Err(id, "bad_args", "air.veto_neutral must be true/false"); aVn = (bool)air["veto_neutral"]; }
                if (air.ContainsKey("post_launch")) { aPl = air["post_launch"] as string; if (aPl == null || !IsMode(aPl)) return Err(id, "bad_args", "air.post_launch: off|evade|retarget"); }
            }
            double? rad = null, evs = null;
            if (cmd.ContainsKey("veto_radius_nm")) { rad = D(cmd["veto_radius_nm"]); if (!rad.HasValue || rad.Value < 0.5 || rad.Value > 10) return Err(id, "bad_args", "veto_radius_nm 0.5..10"); }
            if (cmd.ContainsKey("evade_s")) { evs = D(cmd["evade_s"]); if (!evs.HasValue || evs.Value < 5 || evs.Value > 120) return Err(id, "bad_args", "evade_s 5..120"); }
            if (!sAe.HasValue && !sVn.HasValue && !aVn.HasValue && aPl == null && !rad.HasValue && !evs.HasValue) return Err(id, "bad_args", "nothing to set");
            if (aPl == "retarget")
            {
                if (!AllowAttack) return Err(id, "attack_disabled", "AllowAttack=false in mod config");
                if (!Confirmed(cmd)) return Err(id, "confirm_required", "air.post_launch \"retarget\" needs \"confirm\":true");
            }
            if (!InMission()) return Err(id, "not_in_session", null);
            Need("unit_flags");
            TacticsEnsureMission();
            if (sAe.HasValue) _defShip.AutoEvade = sAe;
            if (sVn.HasValue) _defShip.VetoNeutral = sVn;
            if (aVn.HasValue) _defAir.VetoNeutral = aVn;
            if (aPl != null) _defAir.PostLaunch = aPl;
            if (rad.HasValue) VetoRadiusNm = rad.Value;
            if (evs.HasValue) EvadeS = evs.Value;
            FlagStoreSetDefaults(sAe, sVn, aVn, aPl);
            if (EventsAvailable) EvLine("flags_changed", "{\"uid\":null,\"defaults\":" + DefaultsJson() + "}");
            return Ok(id, "\"defaults\":" + DefaultsJson() + ",\"veto_radius_nm\":" + Json.Num(VetoRadiusNm) + ",\"evade_s\":" + Json.Num(EvadeS));
        }

        private string SetGameOptionCmd(string id, Dictionary<string, object> cmd)
        {
            string key = cmd.ContainsKey("key") ? cmd["key"] as string : null;
            if (key != "aircraft_missile_evasion") return Err(id, "bad_args", "key: aircraft_missile_evasion");
            if (!cmd.ContainsKey("value") || !(cmd["value"] is bool)) return Err(id, "bad_args", "value must be true/false");
            Need("game_options");
            var om = SingletonIfExists("SeaPower.OptionsManager");
            if (om == null) return Err(id, "unsupported", "OptionsManager not available");
            var p = om.GetType().GetProperty("AircraftMissileEvasion", AllInst);
            if (p == null || !p.CanWrite) return Err(id, "unsupported", "AircraftMissileEvasion");
            object prev = p.GetValue(om, null);
            p.SetValue(om, cmd["value"], null);
            return Ok(id, "\"key\":" + Json.Str(key) + ",\"value\":" + Json.Bool((bool)cmd["value"]) + ",\"previous\":" + (prev is bool ? Json.Bool((bool)prev) : "null"));
        }

        private string CourseFromThreatCmd(string id, Dictionary<string, object> cmd)
        {
            Need("course_from_threat");
            double dist = 20;
            if (cmd.ContainsKey("distance_nm") && cmd["distance_nm"] != null)
            {
                var dn = D(cmd["distance_nm"]);
                if (!dn.HasValue || dn.Value < 0.5 || dn.Value > 300) return Err(id, "bad_args", "distance_nm 0.5..300");
                dist = dn.Value;
            }
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            double lat0, lon0, h;
            if (!OwnPos(u, out lat0, out lon0, out h)) return Err(id, "no_position", null);
            object v = null;
            if (cmd.ContainsKey("track_id") && cmd["track_id"] != null)
            {
                var tid = D(cmd["track_id"]);
                if (!tid.HasValue) return Err(id, "bad_args", "track_id must be a number");
                v = FindContact((int)tid.Value);
                if (v == null || IsGone(v)) return Err(id, "unknown_target", "no such contact in the plotting table");
            }
            else
            {
                double best = double.MaxValue;
                var vehicles = Vehicles();
                if (vehicles != null)
                    foreach (var c in vehicles)
                    {
                        if (Relation(c) != "hostile" || IsGone(c)) continue;
                        var p = TrackPos(c); if (p == null) continue;
                        double d = MetricDistanceNm(lat0, lon0, p[0], p[1]);
                        if (d < best) { best = d; v = c; }
                    }
                if (v == null) return Err(id, "no_hostile_contact", "no hostile contact in the plotting table");
            }
            var tp = TrackPos(v);
            if (tp == null) return Err(id, "no_position", "threat track has no position");
            double brg = MetricBearing(lat0, lon0, tp[0], tp[1]);
            double course = Math.Round(TacticsMath.AwayCourse(brg));
            if (course >= 360) course -= 360;
            double lat, lon;
            MetricDestination(lat0, lon0, course, dist, out lat, out lon);
            var tid2 = D(Get(v, "Id"));
            return PlaceWaypoint(id, u, lat, lon, false, ",\"course\":" + Json.Num(course) + ",\"threat_track_id\":" + (tid2.HasValue ? Json.Num(tid2.Value) : "null") +
                ",\"threat_bearing_deg\":" + Json.Num(Math.Round(brg, 1)));
        }

        // ---------- pump (main thread, ~2 Hz) ----------

        public void PumpTactics(double realNow)
        {
            if (realNow < _nextTactics) return;
            _nextTactics = realNow + 0.5;
            if (!InMission()) { TacticsEnsureMission(); return; }
            TacticsEnsureMission();
            try { PumpAutoEvade(realNow); } catch { }
            try { PumpPostLaunch(); } catch { }
        }

        private bool PlayerKnowsTorpedo(object u, double lat0, double lon0, out int? track)
        {
            track = null;
            bool found = false; double ib = 0, ir = 0; int? it = null;
            LastInboundTorpedo(u, ref found, ref ib, ref ir, ref it);
            if (found) { track = it; return true; }
            var vehicles = Vehicles();
            if (vehicles == null) return false;
            foreach (var c in vehicles)
            {
                if (Kind(c) != "Torpedo" || Relation(c) == "friendly" || IsGone(c)) continue;
                var p = TrackPos(c); if (p == null) continue;
                if (MetricDistanceNm(lat0, lon0, p[0], p[1]) > 10.0) continue;
                var d = D(Get(c, "Id")); if (d.HasValue) track = (int)d.Value;
                return true;
            }
            return false;
        }

        private void PumpAutoEvade(double now)
        {
            if (!Capabilities.Contains("auto_evade")) return;
            foreach (var u in OwnUnits())
            {
                if (UnitKindOfObj(u) != "ship" || !IsAlive(u)) continue;
                bool evHandled = false;
                AutoEvadeHook(u, now, ref evHandled);
                if (evHandled) continue;
                var sm = Get(u, "StateMachine");
                var cur = Get(sm, "CurrentState");
                bool inEv = cur != null && cur.GetType().Name == "TorpedoEvasion";
                double started;
                if (_evadeStart.TryGetValue(u, out started))
                {
                    if (!inEv)
                    {
                        _evadeStart.Remove(u); _evadeLast[u] = now;
                        EvLine("auto_evade", "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"phase\":\"end\",\"torpedo_track_id\":null}");
                    }
                    continue;
                }
                if (!ResolveFlag(u, "auto_evade")) continue;
                var tor = Get(u, "ThreatingTorpedo");
                bool hasThreat = tor != null && IsAlive(tor);
                if (!hasThreat) continue;
                var ev = Get(u, "_state_torpedoEvasion");
                if (ev == null) continue;
                double lat0, lon0, h;
                if (!OwnPos(u, out lat0, out lon0, out h)) continue;
                int? track;
                bool known = PlayerKnowsTorpedo(u, lat0, lon0, out track);
                int curPrio = (int)(D(Get(cur, "Priority")) ?? 99);
                int evPrio = (int)(D(Get(ev, "Priority")) ?? 2);
                double last; double since = _evadeLast.TryGetValue(u, out last) ? now - last : 1e9;
                if (!TacticsMath.ShouldStartEvasion(true, hasThreat, known, curPrio, evPrio, inEv, since)) continue;
                var set = SetStateMethod();
                if (set == null) continue;
                set.Invoke(sm, new[] { ev });
                _evadeStart[u] = now;
                EvLine("auto_evade", "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"phase\":\"start\",\"torpedo_track_id\":" + (track.HasValue ? track.Value.ToString() : "null") + "}");
            }
        }

        // ---------- after launch ----------

        private static string MidcourseName(object mcc)
        {
            if (mcc == null) return "none";
            if (BoolOf(Get0(mcc, "IsRadioCommand"))) return "radio";
            if (BoolOf(Get0(mcc, "IsWireGuided"))) return "wire";
            if (BoolOf(Get0(mcc, "IsDatalink"))) return "datalink";
            return "none";
        }

        private static object Get0(object o, string name)
        {
            try { var p = o.GetType().GetProperty(name, AllInst); return p == null ? null : p.GetValue(o, null); } catch { return null; }
        }

        private string GuidanceOf(object ap, out string midcourse)
        {
            midcourse = MidcourseName(Get(ap, "_midCourseCorrection"));
            var g = Get(ap, "_guidanceType");
            return TacticsMath.GuidanceName(g == null ? 0 : Convert.ToInt32(g));
        }

        private void PumpPostLaunch()
        {
            if (!Capabilities.Contains("post_launch")) return;
            var live = new List<object>();
            foreach (var air in AirUnits())
            {
                if (!InFlight(air)) continue;
                var wss = Get(Get(air, "_obp"), "_weaponSystems") as IEnumerable;
                if (wss == null) continue;
                foreach (var ws in wss)
                {
                    var lw = Get(ws, "_launchedWeapons") as IEnumerable;
                    if (lw == null) continue;
                    foreach (var w in lw)
                    {
                        if (w == null || !IsAlive(w)) continue;
                        live.Add(w);
                        if (_plWeapons.ContainsKey(w)) continue;
                        var target = Get(w, "_initialSetTargetObject") ?? Get(w, "_lastTargetObject");
                        _plWeapons[w] = IsAlive(target) ? target : null;
                        if (_plPrimed) { try { OnNewLaunch(air, w, target); } catch { } }
                    }
                }
            }
            _plPrimed = true;
            var dead = new List<object>();
            foreach (var k in _plWeapons.Keys) if (!IsAlive(k)) dead.Add(k);
            foreach (var k in dead) _plWeapons.Remove(k);
            PumpPostLaunchRuns();
        }

        private string PlEvent(object air, string mode, string phase, string action, string guidance, int? targetTrack, double? course, int? nextTarget)
        {
            return "{\"uid\":" + UidOf(air) + ",\"name\":" + Json.Str(NameOf(air)) + ",\"mode\":" + Json.Str(mode) + ",\"phase\":" + Json.Str(phase) +
                ",\"action\":" + Json.Str(action) + ",\"guidance\":" + Json.Str(guidance) + ",\"target_track_id\":" + (targetTrack.HasValue ? targetTrack.Value.ToString() : "null") +
                (course.HasValue ? ",\"course\":" + Json.Num(course.Value) : "") + ",\"next_target_id\":" + (nextTarget.HasValue ? nextTarget.Value.ToString() : "null") + "}";
        }

        private int? TrackIdOfObject(object obj)
        {
            if (obj == null) return null;
            var vehicles = Vehicles();
            if (vehicles == null) return null;
            foreach (var v in vehicles)
                if (ReferenceEquals(Get(v, "Object"), obj)) { var d = D(Get(v, "Id")); return d.HasValue ? (int?)d.Value : null; }
            return null;
        }

        private void OnNewLaunch(object air, object w, object target)
        {
            string mode = ResolvePostLaunch(air);
            if (mode == "off" || _plRuns.ContainsKey(air)) return;
            string mid; string guidance = GuidanceOf(Get(w, "_ap"), out mid);
            bool targetIsAir = target != null && IsAlive(target) && UnitKindOfObj(target) == "air";
            double lat0, lon0, h, lat1 = 0, lon1 = 0;
            double range = 99;
            if (OwnPos(air, out lat0, out lon0, out h) && targetIsAir && OwnPos(target, out lat1, out lon1, out h)) range = MetricDistanceNm(lat0, lon0, lat1, lon1);
            bool hold = WeaponStatusName(air) == "hold";
            bool guidingOther = false;
            var tasks = Get(air, "_alignmentTasks") as IEnumerable;
            if (tasks != null)
                foreach (var t in tasks)
                    if (BoolOf(Get(t, "_isGuidingWeapon")) && !ReferenceEquals(Get(t, "_guidedWeapon"), w)) guidingOther = true;
            string decision = TacticsMath.PostLaunchDecision(mode, guidance, mid, targetIsAir, range, hold, guidingOther);
            int? ttrack = TrackIdOfObject(target);
            if (decision == "none") return;
            if (decision.StartsWith("skip_"))
            {
                EvLine("post_launch", PlEvent(air, mode, "skip", decision, guidance, ttrack, null, null));
                return;
            }
            if (decision == "retarget" && !AllowAttack) decision = "evade";
            var saved = Waypoints(air);
            double hdg = D(RxGet(air, "Heading")) ?? 0;
            double brg = MetricBearing(lat0, lon0, lat1, lon1);
            double course = Math.Round(TacticsMath.BeamCourse(hdg, brg));
            if (course >= 360) course -= 360;
            double lat, lon;
            MetricDestination(lat0, lon0, course, 20.0, out lat, out lon);
            string ack = PlaceWaypoint("0", air, lat, lon, false, "");
            if (!ack.Contains("\"ok\":true")) return;
            _plRuns[air] = new PlRun { Air = air, Target = target, Mode = decision, Guidance = guidance, TargetTrack = ttrack, Saved = saved, Placed = new[] { lat, lon }, StartT = WatchNow(), Course = course };
            EvLine("post_launch", PlEvent(air, mode, "start", decision, guidance, ttrack, course, null));
        }

        private void PumpPostLaunchRuns()
        {
            if (_plRuns.Count == 0) return;
            foreach (var air in new List<object>(_plRuns.Keys))
            {
                var run = _plRuns[air];
                if (!IsAlive(air) || !InFlight(air)) { _plRuns.Remove(air); continue; }
                if (WatchNow() - run.StartT < EvadeS) continue;
                _plRuns.Remove(air);
                var wps = Waypoints(air);
                if (wps.Count == 0 || MetricDistanceNm(wps[0][0], wps[0][1], run.Placed[0], run.Placed[1]) > 0.2)
                {
                    EvLine("post_launch", PlEvent(air, run.Mode, "end", "overridden", run.Guidance, run.TargetTrack, null, null)); // somebody else gave an order: leave it
                    continue;
                }
                if (run.Saved.Count == 0) Call(air, "RemoveWaypoints");
                for (int i = 0; i < run.Saved.Count; i++) PlaceWaypoint("0", air, run.Saved[i][0], run.Saved[i][1], i > 0, "");
                string action = "evade"; int? next = null;
                if (run.Mode == "retarget")
                {
                    next = PickRetarget(air, run);
                    if (next.HasValue)
                    {
                        var ac = new Dictionary<string, object> { { "uid", (double)UidOf(air) }, { "target_id", (double)next.Value }, { "weapon", "sam" }, { "salvo", 1.0 }, { "confirm", true } };
                        string r = Attack("0", ac, false);
                        if (r.Contains("\"ok\":true")) action = "retarget"; else next = null;
                    }
                }
                EvLine("post_launch", PlEvent(air, run.Mode, "end", action, run.Guidance, run.TargetTrack, null, next));
            }
        }

        /// <summary>Nearest hostile air track in the player's table that is not the old target, not the target of another live missile, within the remaining air-to-air range.</summary>
        private int? PickRetarget(object air, PlRun run)
        {
            double lat0, lon0, h;
            if (!OwnPos(air, out lat0, out lon0, out h)) return null;
            double maxRange = 0;
            foreach (var r in WeaponRows(air))
            {
                if (r[2] != "Missile" || r[3] != "AAW" || r[7] != "true") continue;
                double rg; if (double.TryParse(r[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rg)) maxRange = Math.Max(maxRange, rg);
            }
            if (maxRange <= 0) return null;
            var excluded = new List<int>();
            if (run.TargetTrack.HasValue) excluded.Add(run.TargetTrack.Value);
            foreach (var kv in _plWeapons)
                if (IsAlive(kv.Key) && kv.Value != null) { var t = TrackIdOfObject(kv.Value); if (t.HasValue) excluded.Add(t.Value); }
            var cands = new List<double[]>();
            var vehicles = Vehicles();
            if (vehicles == null) return null;
            foreach (var v in vehicles)
            {
                string k = Kind(v);
                if (Relation(v) != "hostile" || (k != "Air" && k != "Helicopter") || IsGone(v)) continue;
                var p = TrackPos(v); var id = D(Get(v, "Id"));
                if (p == null || !id.HasValue) continue;
                cands.Add(new[] { p[0], p[1], id.Value });
            }
            int idx = TacticsMath.PickNextTarget(new[] { lat0, lon0 }, cands, excluded, maxRange);
            return idx < 0 ? (int?)null : (int)cands[idx][2];
        }
    }
}
