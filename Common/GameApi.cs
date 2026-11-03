using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// All access to Seapower-Scripts.dll is by reflection, so a renamed/removed member never crashes the mod:
    /// it only turns off the capability that needs it. Must be called ONLY from the Unity main thread.
    /// Reference game version: v0.8.5-261008 (see docs/research/seapower-mod.md).
    /// </summary>
    internal sealed partial class GameApi
    {
        private const BindingFlags AllInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AllStat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        public sealed class Check
        {
            public string Capability;
            public string Member;
            public bool Required;
            public bool Present;
        }

        public long StateSeq;
        /// <summary>Monotonic seconds for ping.data.t (Plugin sets Time.unscaledTime; selfcheck uses a stopwatch).</summary>
        public Func<double> Clock = DefaultClock;
        private static readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();
        private static double DefaultClock() { return Watch.Elapsed.TotalSeconds; }
        public readonly List<Check> Checks = new List<Check>();
        public readonly HashSet<string> Capabilities = new HashSet<string>();

        private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
        private readonly Dictionary<string, MemberInfo> _memberCache = new Dictionary<string, MemberInfo>();

        // ---------- type / member lookup ----------

        public Type FindType(string fullName)
        {
            Type t;
            if (_types.TryGetValue(fullName, out t)) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(fullName, false); } catch { t = null; }
                if (t != null) break;
            }
            _types[fullName] = t;
            return t;
        }

        private MemberInfo Find(Type t, string name, bool isStatic)
        {
            if (t == null) return null;
            string key = t.FullName + (isStatic ? "::s::" : "::i::") + name;
            MemberInfo m;
            if (_memberCache.TryGetValue(key, out m)) return m;
            var flags = isStatic ? AllStat : AllInst;
            m = (MemberInfo)t.GetProperty(name, flags) ?? t.GetField(name, flags);
            if (m == null)
            {
                // methods: pick the first by name (overloads are rare for what we use)
                foreach (var mi in t.GetMethods(flags)) { if (mi.Name == name) { m = mi; break; } }
            }
            _memberCache[key] = m;
            return m;
        }

        private bool Has(string cap, string type, string member, bool isStatic, bool required)
        {
            var m = Find(FindType(type), member, isStatic);
            Checks.Add(new Check { Capability = cap, Member = type + "." + member, Required = required, Present = m != null });
            return m != null;
        }

        // ---------- generic reflective access ----------

        public object Get(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var m = Find(obj.GetType(), name, false);
                var p = m as PropertyInfo; if (p != null) return p.GetValue(obj, null);
                var f = m as FieldInfo; if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }

        public object GetStatic(string type, string name)
        {
            try
            {
                var m = Find(FindType(type), name, true);
                var p = m as PropertyInfo; if (p != null) return p.GetValue(null, null);
                var f = m as FieldInfo; if (f != null) return f.GetValue(null);
            }
            catch { }
            return null;
        }

        public object Call(object obj, string name, params object[] args)
        {
            var m = Find(obj.GetType(), name, false) as MethodInfo;
            if (m == null) throw new MissingMemberException(obj.GetType().Name, name);
            return m.Invoke(obj, args);
        }

        public object CallStatic(string type, string name, params object[] args)
        {
            var m = Find(FindType(type), name, true) as MethodInfo;
            if (m == null) throw new MissingMemberException(type, name);
            return m.Invoke(null, args);
        }

        /// <summary>UniRx ReactiveProperty&lt;T&gt;.Value.</summary>
        public object RxGet(object obj, string name)
        {
            var rp = Get(obj, name);
            return rp == null ? null : Get(rp, "Value");
        }

        public void RxSet(object obj, string name, object value)
        {
            var rp = Get(obj, name);
            if (rp == null) throw new MissingMemberException(obj.GetType().Name, name);
            var p = rp.GetType().GetProperty("Value", AllInst);
            if (p == null) throw new MissingMemberException(rp.GetType().Name, "Value");
            p.SetValue(rp, value, null);
        }

        public static double? D(object o)
        {
            if (o == null) return null;
            try { return Convert.ToDouble(o); } catch { return null; }
        }

        // ---------- startup self-check ----------

        public void SelfCheck()
        {
            Checks.Clear();
            Capabilities.Clear();

            // state: active/selected unit + basic kinematics
            bool st = true;
            st &= Has("state", "SeaPower.ObjectsManager", "getActiveObject", false, true);
            st &= Has("state", "SeaPower.ObjectBase", "Heading", false, true);
            st &= Has("state", "SeaPower.ObjectBase", "getVelocityInKnots", false, true);
            st &= Has("state", "SeaPower.ObjectBase", "Position", false, true);
            st &= Has("state", "SeaPower.ObjectBase", "Name", false, false);
            st &= Has("state", "SeaPower.ObjectBase", "UniqueID", false, false);
            Has("state", "SeaPower.ObjectBase", "SpeedCommand", false, false);
            Has("state", "SeaPower.ObjectBase", "CourseCommand", false, false);
            Has("state", "SeaPower.ObjectBase", "IsPlayerObject", false, false);
            Has("state", "SeaPower.ObjectBase", "IsControllable", false, false);
            Has("state", "SeaPower.ObjectBase", "getTelegraph", false, false);
            Has("state", "SeaPower.RenderPosition", "getSelectedObject", false, false);
            Has("state", "SeaPower.Globals", "_playerTaskforce", true, false);
            if (st) Capabilities.Add("state");

            // waypoints (UI path): RemoveWaypoints + setWaypointTask(GeoPosition) [+ CommandPosition]; set_course/goto/add_waypoint use it
            bool wp = st;
            wp &= Has("waypoints", "SeaPower.ObjectBase", "setWaypointTask", false, true);
            wp &= Has("waypoints", "SeaPower.ObjectBase", "RemoveWaypoints", false, true);
            wp &= FindType("SeaPower.GeoPosition") != null;
            Has("waypoints", "SeaPower.ObjectBase", "CommandPosition", false, false);
            Has("waypoints", "SeaPower.ObjectBase", "GetAllWaypointsGeoPositions", false, false);
            Has("waypoints", "SeaPower.ObjectBase", "setTelegraph", false, false);
            if (wp) { Capabilities.Add("waypoints"); Capabilities.Add("set_course"); Capabilities.Add("goto"); }

            // legacy (explicit flag only): CourseCommand = new SetCourse(deg). Live test 2026-10-15: does not turn the ship, resets speed.
            bool lg = st;
            lg &= Has("legacy_set_course", "SeaPower.ObjectBase", "CourseCommand", false, true);
            bool ctor = FindType("SeaPower.SetCourse") != null && FindType("SeaPower.SetCourse").GetConstructor(new[] { typeof(float) }) != null;
            Checks.Add(new Check { Capability = "legacy_set_course", Member = "SeaPower.SetCourse..ctor(float)", Required = true, Present = ctor });
            if (lg && ctor) Capabilities.Add("legacy_set_course");

            // set_telegraph: ObjectBase.setTelegraph(int)
            if (st && Has("set_telegraph", "SeaPower.ObjectBase", "setTelegraph", false, true)) Capabilities.Add("set_telegraph");

            // pause
            bool pa = true;
            pa &= Has("pause", "SeaPower.GameTime", "IsPaused", true, true);
            pa &= Has("pause", "SeaPower.GameTime", "Pause", true, true);
            pa &= Has("pause", "SeaPower.GameTime", "UnPause", true, true);
            Has("pause", "SeaPower.GameTime", "TimeCompression", true, false);
            Has("pause", "SeaPower.GameTime", "missionElapsedTime", true, false);
            Has("pause", "SeaPower.GameTime", "missionSessionTime", true, false);
            if (pa) Capabilities.Add("pause");

            // contacts: Globals._playerTaskforce.PlottingTable.Vehicles
            bool ct = true;
            ct &= Has("contacts", "SeaPower.Globals", "_playerTaskforce", true, true);
            ct &= Has("contacts", "SeaPower.Taskforce", "PlottingTable", false, true);
            ct &= Has("contacts", "SeaPower.PlottingTable", "Vehicles", false, true);
            ct &= Has("contacts", "SeaPower.Vehicle", "Position", false, true);
            ct &= Has("contacts", "SeaPower.Vehicle", "Id", false, true);
            if (ct) Capabilities.Add("contacts");

            SelfCheckMissionData();
            SelfCheckWeapons(st);
            SelfCheckCommands(st);

            // own units list
            bool un = true;
            un &= Has("units", "SeaPower.Taskforce", "_taskforceVessels", false, true);
            un &= Has("units", "SeaPower.Taskforce", "_taskforceSubmarines", false, true);
            if (un) Capabilities.Add("units");

            SelfCheckAviation(st); // v0.8 (T-060): needs set_roe (SelfCheckCommands)
            SelfCheckFormations(st); // v0.8 (T-061)
            SelfCheckTactics(st); // v0.9 (T-064): registry, unit flags, auto evasion, after launch
            SelfCheckVeto();
            SelfCheckMod(); // interface checks (menu, coverage, map layer, radial, overview) live in the UI part
            SelfCheckNeutralGuard(); // v0.13 (T-079): neutral_near guard of attack
        }

        // ---------- scene access ----------

        public bool InMission()
        {
            // The ObjectsManager singleton only exists in a loaded mission; menu => no instance.
            return ObjectsManagerInstance() != null;
        }

        private object ObjectsManagerInstance()
        {
            var t = FindType("SeaPower.ObjectsManager");
            if (t == null) return null;
            try
            {
                var ie = t.GetMethod("InstanceExists", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                if (ie != null)
                {
                    var ps = ie.GetParameters();
                    bool exists = (bool)ie.Invoke(null, ps.Length == 0 ? null : new object[] { false });
                    if (!exists) return null;
                }
                var p = t.GetProperty("Instance", AllStat);
                return p == null ? null : p.GetValue(null, null);
            }
            catch { return null; }
        }

        private object RenderPositionInstance()
        {
            var t = FindType("SeaPower.RenderPosition");
            if (t == null) return null;
            try
            {
                var ie = t.GetMethod("InstanceExists", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                if (ie != null)
                {
                    var ps = ie.GetParameters();
                    if (!(bool)ie.Invoke(null, ps.Length == 0 ? null : new object[] { false })) return null;
                }
                var p = t.GetProperty("Instance", AllStat);
                return p == null ? null : p.GetValue(null, null);
            }
            catch { return null; }
        }

        /// <summary>
        /// Unit the player is currently working with. Tries camera/UI selection, then ObjectsManager active object.
        /// 0.12.0 (T-070): when a contact (or any non-own object) is selected, the last own controllable unit that was selected is used
        /// instead (source "last_own"), so voice commands without a uid keep going to the player's ship.
        /// </summary>
        public object SelectedUnit(out string source)
        {
            source = null;
            var rp = RenderPositionInstance();
            if (rp != null)
            {
                try
                {
                    var o = Call(rp, "getSelectedObject");
                    if (IsAlive(o))
                    {
                        bool own = !IsFalse(Get(o, "IsPlayerObject")) && !IsFalse(Get(o, "IsControllable"));
                        string pickSrc;
                        var pick = OverviewMath.PickCommandUnit(o, own, _lastOwn, LastOwnAlive(), out pickSrc);
                        if (pick != null) { source = pickSrc; return pick; }
                        source = "render_position";
                        return o;
                    }
                }
                catch { }
            }
            var om = ObjectsManagerInstance();
            if (om != null)
            {
                try
                {
                    var o = Call(om, "getActiveObject");
                    if (IsAlive(o)) { source = "objects_manager"; return o; }
                }
                catch { }
            }
            return null;
        }

        // Destroyed Unity objects compare == null but are non-null references.
        private static bool IsAlive(object o)
        {
            if (o == null) return false;
            var uo = o as UnityEngine.Object;
            return !(uo is UnityEngine.Object) || uo != null;
        }

        public object FindOwnUnit(int uid)
        {
            var tf = GetStatic("SeaPower.Globals", "_playerTaskforce");
            foreach (var listName in new[] { "_taskforceVessels", "_taskforceSubmarines", "_taskforceAircraft", "_taskforceHelicopters" })
            {
                var list = Get(tf, listName) as IEnumerable;
                if (list == null) continue;
                foreach (var u in list)
                {
                    var id = D(Get(u, "UniqueID"));
                    if (id.HasValue && (int)id.Value == uid) return u;
                }
            }
            return null;
        }


        // ---------- commands (main thread only) ----------

        public string Execute(Dictionary<string, object> cmd)
        {
            object idObj; cmd.TryGetValue("id", out idObj);
            string id = idObj == null ? "null" : (idObj is string ? Json.Str((string)idObj) : Json.Num(D(idObj) ?? 0));
            string name = cmd.ContainsKey("cmd") ? cmd["cmd"] as string : null;
            try
            {
                switch (name)
                {
                    case "ping":
                        return Ok(id, "\"t\":" + Json.Num(Math.Round(Clock(), 3)));
                    case "get_state":
                        // the full state line follows this ack (Dispatcher); its seq is StateSeq + 1
                        return Ok(id, "\"seq\":" + (StateSeq + 1));
                    case "set_course": return cmd.ContainsKey("legacy") && cmd["legacy"] is bool && (bool)cmd["legacy"] ? LegacySetCourse(id, cmd) : Scoped(id, cmd, name, (c, f) => SetCourse(id, c));
                    case "goto": return Scoped(id, cmd, name, (c, f) => GoTo(id, c, false));
                    case "add_waypoint": return Scoped(id, cmd, name, (c, f) => GoTo(id, c, true));
                    case "clear_waypoints": return Scoped(id, cmd, name, (c, f) => ClearWaypoints(id, c));
                    case "waypoints": return WaypointsCmd(id, cmd);
                    case "attack": return Scoped(id, cmd, name, (c, f) => Attack(id, c, false));
                    case "attack_nearest_hostile": return Scoped(id, cmd, name, (c, fixedTarget) => Attack(id, c, !fixedTarget));
                    case "contacts": return Ok(id, "\"contacts\":" + ContactsJson(200, SelectedUnitOrNull()));
                    case "weapons": return WeaponsCmd(id, cmd);
                    case "clear_course": return ClearCourse(id, cmd);
                    case "set_telegraph": return Scoped(id, cmd, name, (c, f) => TelegraphCmd(id, c));
                    case "pause": return PauseCmd(id, true);
                    case "unpause": return PauseCmd(id, false);
                    case "select": return SelectCmd(id, cmd);
                    case "set_emcon": return Scoped(id, cmd, name, (c, f) => EmconCmd(id, c));
                    case "set_radar": return Scoped(id, cmd, name, (c, f) => RadarCmd(id, c));
                    case "set_roe": return Scoped(id, cmd, name, (c, f) => RoeCmd(id, c));
                    case "cease_fire": return Scoped(id, cmd, name, (c, f) => CeaseFireCmd(id, c));
                    case "launch_decoy": return LaunchDecoyCmd(id, cmd);
                    case "evade_torpedo": return EvadeTorpedoCmd(id, cmd);
                    case "flight_deck": return FlightDeckCmd(id, cmd);
                    case "launch_aircraft": return LaunchAircraftCmd(id, cmd);
                    case "set_altitude": return SetAltitudeCmd(id, cmd);
                    case "return_to_base": return ReturnToBaseCmd(id, cmd);
                    case "recover_all": return RecoverAllCmd(id, cmd);
                    case "allow_launch": return AllowLaunchCmd(id, cmd);
                    case "set_time_compression": return TimeCmd(id, cmd);
                    case "actions": return ActionsCmd(id, cmd);
                    case "action": return ActionCmd(id, cmd);
                    case "flags": return FlagsCmd(id, cmd);
                    case "set_flags": return Scoped(id, cmd, name, (c, f) => SetFlagsCmd(id, c));
                    case "set_doctrine_defaults": return SetDoctrineDefaultsCmd(id, cmd);
                    case "set_game_option": return SetGameOptionCmd(id, cmd);
                    case "course_from_threat": return CourseFromThreatCmd(id, cmd);
                    case "restore_control": return RestoreControlCmd(id, cmd);
                    default:
                        // mod-specific commands (voice: events, mission; UI: overlay, overview, ...); null = unknown
                        return ExecuteMod(id, name, cmd) ?? Err(id, "unknown_cmd", name);
                }
            }
            catch (CapabilityException e) { return Err(id, "unsupported", e.Message); }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                return Err(id, "exception", inner.GetType().Name + ": " + inner.Message);
            }
        }

        // ---------- hooks implemented by each mod (ADR-0033) ----------

        /// <summary>Mod-specific protocol commands; returns the ack line or null when the command is not known to this mod.</summary>
        private partial string ExecuteMod(string id, string name, Dictionary<string, object> cmd);
        /// <summary>Mod-specific member checks (capabilities of this mod only).</summary>
        private partial void SelfCheckMod();
        /// <summary>State of a mod flag ("overview" / "overlay") for the actions registry.</summary>
        internal partial bool ModFlagOn(string flag);
        internal partial void SetModFlag(string flag, bool on, string source);

        private sealed class CapabilityException : Exception { public CapabilityException(string m) : base(m) { } }

        private void Need(string cap)
        {
            if (!Capabilities.Contains(cap)) throw new CapabilityException(cap);
        }

        private object Target(Dictionary<string, object> cmd, out string err)
        {
            err = null;
            if (!InMission()) { err = "not_in_session"; return null; }
            object u;
            if (cmd.ContainsKey("uid") && cmd["uid"] != null)
            {
                u = FindOwnUnit((int)(D(cmd["uid"]) ?? -1));
                if (u == null) { err = "unit_not_found"; return null; }
            }
            else
            {
                string src;
                u = SelectedUnit(out src);
                if (u == null) { err = "no_selected_unit"; return null; }
            }
            var own = Get(u, "IsPlayerObject");
            if (own is bool && !(bool)own) { err = "not_player_unit"; return null; }
            var ctl = Get(u, "IsControllable");
            if (ctl is bool && !(bool)ctl) { err = "not_controllable"; return null; }
            return u;
        }

        // ---- geometry (great circle; 1 nmi = 1/60 deg of arc, same constant the game uses: 111120 m/deg) ----
        private const double Rad = Math.PI / 180.0;

        internal static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double f1 = lat1 * Rad, f2 = lat2 * Rad, dl = (lon2 - lon1) * Rad;
            double y = Math.Sin(dl) * Math.Cos(f2);
            double x = Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl);
            return ((Math.Atan2(y, x) / Rad) + 360.0) % 360.0;
        }

        internal static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            double f1 = lat1 * Rad, f2 = lat2 * Rad, df = f2 - f1, dl = (lon2 - lon1) * Rad;
            double a = Math.Sin(df / 2) * Math.Sin(df / 2) + Math.Cos(f1) * Math.Cos(f2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(a))) / Rad * 60.0;
        }

        internal static void Destination(double lat1, double lon1, double brgDeg, double distNm, out double lat2, out double lon2)
        {
            double d = distNm / 60.0 * Rad, b = brgDeg * Rad, f1 = lat1 * Rad, l1 = lon1 * Rad;
            double f2 = Math.Asin(Math.Sin(f1) * Math.Cos(d) + Math.Cos(f1) * Math.Sin(d) * Math.Cos(b));
            double l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(f1), Math.Cos(d) - Math.Sin(f1) * Math.Sin(f2));
            lat2 = f2 / Rad;
            lon2 = ((l2 / Rad + 540.0) % 360.0) - 180.0;
        }

        // ---- waypoints ----
        /// <summary>Waypoint list as [lat, lon] pairs; GetAllWaypointsGeoPositions() or CommandPosition as fallback.</summary>
        private List<double[]> Waypoints(object u)
        {
            var res = new List<double[]>();
            try
            {
                var m = Find(u.GetType(), "GetAllWaypointsGeoPositions", false) as MethodInfo;
                if (m != null)
                {
                    var ps = m.GetParameters();
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++) args[i] = DefaultFor(ps[i]);
                    var list = m.Invoke(u, args) as IEnumerable;
                    if (list != null)
                    {
                        foreach (var tup in list)
                        {
                            var gp = Get(tup, "Item1");
                            var la = D(Get(gp, "_latitude")); var lo = D(Get(gp, "_longitude"));
                            if (la.HasValue && lo.HasValue) res.Add(new[] { la.Value, lo.Value });
                        }
                        return res;
                    }
                }
            }
            catch { }
            var cp = Get(u, "CommandPosition");
            if (cp != null)
            {
                var la = D(Get(cp, "_latitude")); var lo = D(Get(cp, "_longitude"));
                if (la.HasValue && lo.HasValue) res.Add(new[] { la.Value, lo.Value });
            }
            return res;
        }

        private static object DefaultFor(ParameterInfo p)
        {
            object d = p.HasDefaultValue ? p.DefaultValue : null;
            if (d == null || d == DBNull.Value)
                return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
            if (p.ParameterType.IsEnum && !(d.GetType().IsEnum)) return Enum.ToObject(p.ParameterType, d);
            return d;
        }

        private object MakeGeo(double lat, double lon, double height)
        {
            var t = FindType("SeaPower.GeoPosition");
            object gp = Activator.CreateInstance(t); // boxed struct: SetValue mutates the box
            t.GetField("_latitude").SetValue(gp, lat);
            t.GetField("_longitude").SetValue(gp, lon);
            t.GetField("_height").SetValue(gp, height);
            return gp;
        }

        private bool OwnPos(object u, out double lat, out double lon, out double h)
        {
            lat = lon = h = 0;
            var pos = RxGet(u, "Position");
            var la = D(Get(pos, "_latitude")); var lo = D(Get(pos, "_longitude"));
            if (!la.HasValue || !lo.HasValue) return false;
            lat = la.Value; lon = lo.Value; h = D(Get(pos, "_height")) ?? 0;
            return true;
        }

        private int? Telegraph(object u)
        {
            try { var t = D(Call(u, "getTelegraph")); return t.HasValue ? (int?)t.Value : null; } catch { return null; }
        }

        /// <summary>Same sequence as the game UI (UnitSelectedState): RemoveWaypoints, setWaypointTask, CommandPosition.
        /// Speed is preserved: telegraph read before, restored if the order changed it.</summary>
        private string PlaceWaypoint(string id, object u, double lat, double lon, bool append, string extra)
        {
            double lat0, lon0, h;
            if (!OwnPos(u, out lat0, out lon0, out h)) return Err(id, "no_position", null);
            // v0.8: for aircraft the waypoint carries the demanded altitude (the game copies _height into DesiredAltitude),
            // otherwise a new course would reset the altitude to the current one
            if (IsAirUnit(u)) { var da = D(RxGet(u, "DesiredAltitude")); if (da.HasValue && da.Value > 0) h = da.Value; }
            int? tgBefore = Telegraph(u);
            if (!append) Call(u, "RemoveWaypoints");
            var geo = MakeGeo(lat, lon, h);
            var m = Find(u.GetType(), "setWaypointTask", false) as MethodInfo;
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            args[0] = geo;
            for (int i = 1; i < ps.Length; i++) args[i] = DefaultFor(ps[i]);
            m.Invoke(u, args);
            if (!append)
            {
                try
                {
                    var cp = Find(u.GetType(), "CommandPosition", false) as PropertyInfo;
                    if (cp != null && cp.CanWrite) cp.SetValue(u, geo, null); // Nullable<GeoPosition> boxes as GeoPosition
                }
                catch { }
            }
            int? tgAfter = Telegraph(u);
            bool restored = false;
            if (tgBefore.HasValue && tgAfter.HasValue && tgBefore.Value != tgAfter.Value && Capabilities.Contains("set_telegraph"))
            {
                Call(u, "setTelegraph", tgBefore.Value);
                restored = true;
                tgAfter = Telegraph(u);
            }
            return Ok(id, "\"uid\":" + Json.Num(D(Get(u, "UniqueID")) ?? -1) +
                ",\"dest_lat\":" + Json.Num(lat) + ",\"dest_lon\":" + Json.Num(lon) +
                ",\"bearing_deg\":" + Json.Num(Math.Round(Bearing(lat0, lon0, lat, lon), 1)) +
                ",\"distance_nm\":" + Json.Num(Math.Round(DistanceNm(lat0, lon0, lat, lon), 1)) +
                ",\"append\":" + Json.Bool(append) +
                ",\"telegraph_before\":" + (tgBefore.HasValue ? tgBefore.Value.ToString() : "null") +
                ",\"telegraph_after\":" + (tgAfter.HasValue ? tgAfter.Value.ToString() : "null") +
                ",\"telegraph_restored\":" + Json.Bool(restored) + ManeuverAck(u) + extra);
        }

        private string ManeuverAck(object u)
        {
            try { string by; string mj = ManeuverJson(u, out by); return ",\"deferred\":" + Json.Bool(IsDeferredBy(by)) + ",\"maneuver\":" + mj; }
            catch { return ""; }
        }

        private string SetCourse(string id, Dictionary<string, object> cmd)
        {
            Need("set_course");
            var c = cmd.ContainsKey("course") ? D(cmd["course"]) : null;
            if (!c.HasValue || c.Value < 0 || c.Value > 360) return Err(id, "bad_args", "course 0..360 required");
            double far = 75; // nmi: beyond any horizon the bot cares about, still a valid map position
            if (cmd.ContainsKey("distance_nm") && cmd["distance_nm"] != null)
            {
                var dn = D(cmd["distance_nm"]);
                if (!dn.HasValue || dn.Value < 0.5 || dn.Value > 300) return Err(id, "bad_args", "distance_nm 0.5..300"); // no silent clamp
                far = dn.Value;
            }
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            double lat0, lon0, h;
            if (!OwnPos(u, out lat0, out lon0, out h)) return Err(id, "no_position", null);
            double lat, lon;
            Destination(lat0, lon0, c.Value, far, out lat, out lon);
            return PlaceWaypoint(id, u, lat, lon, false, ",\"course\":" + Json.Num(c.Value));
        }

        private string GoTo(string id, Dictionary<string, object> cmd, bool append)
        {
            Need("goto");
            var la = cmd.ContainsKey("lat") ? D(cmd["lat"]) : null;
            var lo = cmd.ContainsKey("lon") ? D(cmd["lon"]) : null;
            if (!la.HasValue || !lo.HasValue || Math.Abs(la.Value) > 90 || Math.Abs(lo.Value) > 180)
                return Err(id, "bad_args", "lat -90..90 and lon -180..180 required");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            return PlaceWaypoint(id, u, la.Value, lo.Value, append, "");
        }

        private string ClearWaypoints(string id, Dictionary<string, object> cmd)
        {
            Need("waypoints");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            Call(u, "RemoveWaypoints");
            return Ok(id, "\"cleared\":true");
        }

        private string WaypointsCmd(string id, Dictionary<string, object> cmd)
        {
            Need("waypoints");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            var sb = new StringBuilder("\"waypoints\":[");
            var w = Waypoints(u);
            for (int i = 0; i < w.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"lat\":").Append(Json.Num(w[i][0])).Append(",\"lon\":").Append(Json.Num(w[i][1])).Append('}');
            }
            return Ok(id, sb.Append(']').ToString());
        }

        // Legacy: live test (Yorktown, 0.8.6-261014) - heading did not change and telegraph dropped 5->2. Kept behind {"legacy":true}.
        private string LegacySetCourse(string id, Dictionary<string, object> cmd)
        {
            Need("legacy_set_course");
            var c = cmd.ContainsKey("course") ? D(cmd["course"]) : null;
            if (!c.HasValue || c.Value < 0 || c.Value > 360) return Err(id, "bad_args", "course 0..360 required");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            try { Call(u, "RemoveWaypoints"); } catch (MissingMemberException) { }
            var sc = Activator.CreateInstance(FindType("SeaPower.SetCourse"), new object[] { (float)c.Value });
            RxSet(u, "CourseCommand", sc);
            return Ok(id, "\"uid\":" + Json.Num(D(Get(u, "UniqueID")) ?? -1) + ",\"course\":" + Json.Num(c.Value) + ",\"legacy\":true");
        }

        private string ClearCourse(string id, Dictionary<string, object> cmd)
        {
            Need("legacy_set_course");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            RxSet(u, "CourseCommand", null);
            return Ok(id, "\"cleared\":true");
        }

        public string ReadOnlyAck(Dictionary<string, object> cmd)
        {
            object idObj; cmd.TryGetValue("id", out idObj);
            string id = idObj == null ? "null" : (idObj is string ? Json.Str((string)idObj) : Json.Num(D(idObj) ?? 0));
            return Err(id, "read_only", null);
        }

        private static string Ok(string id, string extra)
        {
            // v1: command results live in "data" (game-bridge.md section 3)
            return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":true" + (string.IsNullOrEmpty(extra) ? "" : ",\"data\":{" + extra.TrimStart(',') + "}") + "}";
        }

        private static string Err(string id, string code, string detail)
        {
            return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":false,\"error\":" + Json.Str(code) +
                   (detail == null ? "" : ",\"detail\":" + Json.Str(detail)) + "}";
        }
    }
}
