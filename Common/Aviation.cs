using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// v0.8 (T-060): aviation. Reading (state.air_units[], state.decks[], flight_deck) and commands launch_aircraft,
    /// set_altitude, return_to_base, recover_all. Every call repeats a path of the game's own UI (flight deck window,
    /// Orders menu, Set Altitude menu, Recover All button). Main thread only.
    /// </summary>
    internal sealed partial class GameApi
    {
        private const double FeetPerUnit = 220.47266;

        // ---------- pure helpers (checked offline in selfcheck) ----------

        internal struct LoadoutInfo
        {
            public string Name, Display;
            public int Year;
            public bool CanLaunch;
        }

        /// <summary>Index of the preset (in feet) closest to the wanted altitude; the first one wins a tie; -1 if none.</summary>
        internal static int NearestPreset(float[] presetsFt, double wantFt)
        {
            int best = -1;
            double bestD = double.MaxValue;
            if (presetsFt == null) return -1;
            for (int i = 0; i < presetsFt.Length; i++)
            {
                double d = Math.Abs(presetsFt[i] - wantFt);
                if (d < bestD - 1e-9) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// Loadout choice, T-060 rule P2. Only loadouts in service in the mission year (year &lt;= 0: no filter).
        /// No wish: the first one the deck can launch (the game window picks the first one in service).
        /// A wish: Name, then DisplayName (case-insensitive), then a substring of either. Found but impossible: "no_ammo";
        /// nothing found: "unknown_loadout". Returns the index into items or -1 (err is then set).
        /// </summary>
        internal static int PickLoadout(IList<LoadoutInfo> items, string want, int year, out string err)
        {
            err = null;
            Func<LoadoutInfo, bool> inService = x => year <= 0 || x.Year <= 0 || x.Year <= year;
            string w = (want ?? "").Trim();
            if (w.Length == 0)
            {
                bool any = false;
                for (int i = 0; i < items.Count; i++)
                {
                    if (!inService(items[i])) continue;
                    any = true;
                    if (items[i].CanLaunch) return i;
                }
                err = any ? "no_ammo" : "unknown_loadout";
                return -1;
            }
            int hit = -1;
            for (int pass = 0; pass < 3 && hit < 0; pass++)
            {
                for (int i = 0; i < items.Count && hit < 0; i++)
                {
                    if (!inService(items[i])) continue;
                    string n = items[i].Name ?? "", d = items[i].Display ?? "";
                    bool ok;
                    if (pass == 0) ok = string.Equals(n, w, StringComparison.OrdinalIgnoreCase);
                    else if (pass == 1) ok = string.Equals(d, w, StringComparison.OrdinalIgnoreCase);
                    else ok = n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 || d.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (ok) hit = i;
                }
            }
            if (hit < 0) { err = "unknown_loadout"; return -1; }
            if (!items[hit].CanLaunch) { err = "no_ammo"; return -1; }
            return hit;
        }

        /// <summary>Does a deck vehicle type fit the wish: helicopter | plane (Plane/VTOL/STOL) | any/empty | substring of file name or display name.</summary>
        internal static bool TypeMatches(string want, string vehicleType, string fileName, string display)
        {
            string w = (want ?? "").Trim().ToLowerInvariant();
            if (w.Length == 0 || w == "any") return true;
            if (w == "helicopter") return vehicleType == "Helicopter";
            if (w == "plane") return vehicleType == "Plane" || vehicleType == "VTOL" || vehicleType == "STOL";
            return (fileName ?? "").ToLowerInvariant().Contains(w) || (display ?? "").ToLowerInvariant().Contains(w);
        }

        // ---------- self-check ----------

        private void SelfCheckAviation(bool st)
        {
            bool av = st;
            av &= Has("aviation", "SeaPower.ObjectBaseParameters", "_flightDeck", false, true);
            av &= Has("aviation", "SeaPower.FlightDeck", "_vehiclesOnBoard", false, true);
            av &= Has("aviation", "SeaPower.FlightDeck", "FlightDeckTasks", false, true);
            av &= Has("aviation", "SeaPower.FlightDeck", "TotalVehiclesInAir", false, true);
            av &= Has("aviation", "SeaPower.VehicleTypeOnBoard", "_fileName", false, true);
            av &= Has("aviation", "SeaPower.VehicleTypeOnBoard", "_type", false, true);
            av &= Has("aviation", "SeaPower.VehicleTypeOnBoard", "Numbers", false, true);
            av &= Has("aviation", "SeaPower.VehicleTypeOnBoard", "Loadouts", false, true);
            av &= Has("aviation", "SeaPower.VehicleTypeOnBoard", "Squadrons", false, true);
            av &= Has("aviation", "SeaPower.VehicleTypeOnBoard", "DisplayName", false, true);
            av &= Has("aviation", "SeaPower.Loadout", "Name", false, true);
            av &= Has("aviation", "SeaPower.Loadout", "DisplayName", false, true);
            av &= Has("aviation", "SeaPower.Loadout", "ServiceEntryYear", false, true);
            av &= Has("aviation", "SeaPower.Squadron", "_name", false, true);
            av &= Has("aviation", "SeaPower.Squadron", "Numbers", false, true);
            av &= Has("aviation", "SeaPower.Squadron", "Callsigns", false, true);
            av &= Has("aviation", "SeaPower.ObjectBase", "_childAircraft", false, true);
            av &= Has("aviation", "SeaPower.ObjectBase", "_homeBase", false, true);
            av &= Has("aviation", "SeaPower.ObjectBase", "DesiredAltitude", false, true);
            av &= Has("aviation", "SeaPower.ObjectBase", "getHeightInFeet", false, true);
            av &= Has("aviation", "SeaPower.ObjectBase", "CurrentOrder", false, true);
            av &= Has("aviation", "SeaPower.Aircraft", "_isInFlight", false, true);
            av &= Has("aviation", "SeaPower.Helicopter", "_isInFlight", false, true);
            av &= Has("aviation", "SeaPower.Taskforce", "_taskforceAircraft", false, true);
            av &= Has("aviation", "SeaPower.Taskforce", "_taskforceHelicopters", false, true);
            Has("aviation", "SeaPower.Aircraft", "FlightTimeLeftInSec", false, false);
            Has("aviation", "SeaPower.Aircraft", "ActualRangeInKm", false, false);
            Has("aviation", "SeaPower.Helicopter", "FlightTimeLeftInSec", false, false);
            Has("aviation", "SeaPower.Helicopter", "ActualRangeInKm", false, false);
            Has("aviation", "SeaPower.OptionsManager", "AircraftDefaultWeaponState", false, false);
            Has("aviation", "SeaPower.PendingLaunchTask", "_duration", false, false);
            Has("aviation", "SeaPower.ObjectBase", "_spawnTime", false, false);
            if (av) Capabilities.Add("aviation");

            bool la = av && Capabilities.Contains("set_roe");
            la &= Has("launch_aircraft", "SeaPower.FlightDeck", "createLaunchTask", false, true);
            la &= Has("launch_aircraft", "SeaPower.FlightDeck", "CanLaunchVehicle", false, true);
            bool ltp = FindType("SeaPower.LaunchTaskParameters") != null;
            Checks.Add(new Check { Capability = "launch_aircraft", Member = "SeaPower.LaunchTaskParameters", Required = true, Present = ltp });
            la &= ltp;
            la &= Has("launch_aircraft", "SeaPower.LaunchTaskParameters", "_launchCount", false, true);
            Has("launch_aircraft", "SeaPower.FlightDeck", "CanOperateHelicopterInCurrentSeaState", false, false);
            Has("launch_aircraft", "SeaPower.Environment", "Year", false, false);
            if (la) Capabilities.Add("launch_aircraft");

            bool al = av;
            al &= Has("set_altitude", "SeaPower.Aircraft", "setPresetHeight", false, true);
            al &= Has("set_altitude", "SeaPower.Helicopter", "setPresetHeight", false, true);
            al &= Has("set_altitude", "SeaPower.Aircraft", "Ap", false, true);
            al &= Has("set_altitude", "SeaPower.Helicopter", "_hp", false, true);
            al &= Has("set_altitude", "SeaPower.AircraftParameters", "_presetAltitudes", false, true);
            al &= Has("set_altitude", "SeaPower.HelicopterParameters", "_presetAltitudes", false, true);
            al &= Has("set_altitude", "SeaPower.ObjectBase", "setPlayerHeightControl", false, true);
            if (al) Capabilities.Add("set_altitude");

            bool rb = av;
            var ot = FindType("SeaPower.Order+Type");
            Checks.Add(new Check { Capability = "return_to_base", Member = "SeaPower.Order+Type", Required = true, Present = ot != null });
            rb &= ot != null;
            rb &= Has("return_to_base", "SeaPower.ObjectBase", "getHomeBase", false, true);
            bool so = SetOrderMethod() != null;
            Checks.Add(new Check { Capability = "return_to_base", Member = "SeaPower.ObjectBase.setOrder(Order+Type, ObjectBase, bool)", Required = true, Present = so });
            rb &= so;
            if (rb) Capabilities.Add("return_to_base");

            if (av & Has("recover_all", "SeaPower.FlightDeck", "RecoverAll", false, true)) Capabilities.Add("recover_all");

            // v0.8.1: the deck window's Launch button / launch toggle for a task with launch blocked
            bool alw = av;
            alw &= Has("allow_launch", "SeaPower.PendingLaunchTask", "launchAllowed", false, true);
            alw &= Has("allow_launch", "SeaPower.PendingLaunchTask", "AllowLaunchFunc", false, true);
            alw &= Has("allow_launch", "SeaPower.PendingLaunchTask", "ToggleLaunchAllowedFunc", false, true);
            alw &= Has("allow_launch", "SeaPower.PendingLaunchTask", "LaunchCount", false, true);
            Has("allow_launch", "SeaPower.FlightDeckTask", "_stateMachine", false, false);
            if (alw) Capabilities.Add("allow_launch");
            Has("aviation", "SeaPower.BaseSystem", "Inoperable", false, false);
        }

        /// <summary>The setOrder overload taking a target object (ObjectBase has two); picked by parameter types, never by name.</summary>
        private MethodInfo SetOrderMethod()
        {
            var ob = FindType("SeaPower.ObjectBase");
            var ot = FindType("SeaPower.Order+Type");
            if (ob == null || ot == null) return null;
            return ob.GetMethod("setOrder", AllInst, null, new[] { ot, ob, typeof(bool) }, null);
        }

        // ---------- small helpers ----------

        private bool IsAirUnit(object u)
        {
            if (u == null) return false;
            for (var t = u.GetType(); t != null; t = t.BaseType)
                if (t.Name == "Aircraft" || t.Name == "Helicopter") return true;
            return false;
        }

        private bool IsHelicopter(object u)
        {
            for (var t = u == null ? null : u.GetType(); t != null; t = t.BaseType)
                if (t.Name == "Helicopter") return true;
            return false;
        }

        private bool InFlight(object u) { return BoolOf(Get(u, "_isInFlight")); }

        /// <summary>DeSpawn (end of a landing) sets _spawnTime = double.MaxValue before destroyObject: this is a landing, not a loss.</summary>
        internal bool IsRecoveredLanding(object u)
        {
            var st = D(Get(u, "_spawnTime"));
            return st.HasValue && st.Value >= 1e300;
        }

        private object SingletonInstance(string typeName)
        {
            var t = FindType(typeName);
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

        private int MissionYear()
        {
            var env = SingletonInstance("SeaPower.Environment");
            var y = D(Get(env, "Year"));
            return y.HasValue ? (int)y.Value : 0;
        }

        private object DeckOf(object ship) { return Get(Get(ship, "_obp"), "_flightDeck"); }

        private List<object> AirUnits()
        {
            var res = new List<object>();
            var tf = GetStatic("SeaPower.Globals", "_playerTaskforce");
            foreach (var listName in new[] { "_taskforceAircraft", "_taskforceHelicopters" })
            {
                var list = Get(tf, listName) as IEnumerable;
                if (list == null) continue;
                foreach (var u in list) { if (u != null && IsAlive(u)) res.Add(u); }
            }
            res.Sort((a, b) => (D(Get(a, "UniqueID")) ?? 0).CompareTo(D(Get(b, "UniqueID")) ?? 0));
            return res;
        }

        /// <summary>Own ships that have a flight deck, in uid order.</summary>
        private List<object> DeckShips()
        {
            var res = new List<object>();
            foreach (var u in OwnUnits())
            {
                if (!IsAlive(u) || IsAirUnit(u)) continue;
                if (DeckOf(u) != null) res.Add(u);
            }
            res.Sort((a, b) => (D(Get(a, "UniqueID")) ?? 0).CompareTo(D(Get(b, "UniqueID")) ?? 0));
            return res;
        }

        private static string KindOf(string vehicleType) { return vehicleType == "Helicopter" ? "helicopter" : "plane"; }

        internal int UidOf(object u) { return (int)(D(Get(u, "UniqueID")) ?? -1); }
        private string NameOf(object u) { return RxGet(u, "Name") as string; }

        private object AirTarget(Dictionary<string, object> cmd, out string err)
        {
            var u = Target(cmd, out err);
            if (u == null) return null;
            if (!IsAirUnit(u)) { err = "not_air_unit"; return null; }
            return u;
        }

        private float[] PresetsUnits(object u)
        {
            object obj = IsHelicopter(u) ? Get(u, "_hp") : Get(u, "Ap");
            return Get(obj, "_presetAltitudes") as float[];
        }

        // ---------- state ----------

        private string AirUnitJson(object u)
        {
            string baseJson = UnitJson(u, null, false);
            var sb = new StringBuilder(baseJson.Substring(0, baseJson.Length - 1)); // reopen the object
            sb.Append(",\"in_flight\":").Append(Json.Bool(InFlight(u)));
            var home = Get(u, "_homeBase");
            if (home != null && IsAlive(home))
                sb.Append(",\"home_uid\":").Append(UidOf(home)).Append(",\"home_name\":").Append(Json.Str(NameOf(home)));
            else sb.Append(",\"home_uid\":null,\"home_name\":null");
            sb.Append(",\"state\":").Append(Json.Str(StateName(u, "CurrentStateName")));
            try
            {
                var ord = RxGet(u, "CurrentOrder");
                var ot = ord == null ? null : Get(ord, "OrderType");
                sb.Append(",\"order\":").Append(Json.Str(ot == null ? null : ot.ToString()));
            }
            catch { }
            try
            {
                var h = D(Call(u, "getHeightInFeet"));
                if (h.HasValue) sb.Append(",\"alt_ft\":").Append(Json.Num(Math.Round(h.Value)));
                var da = D(RxGet(u, "DesiredAltitude"));
                if (da.HasValue) sb.Append(",\"alt_cmd_ft\":").Append(Json.Num(Math.Round(da.Value * FeetPerUnit)));
            }
            catch { }
            var ft = D(RxGet(u, "FlightTimeLeftInSec"));
            if (ft.HasValue) sb.Append(",\"flight_time_left_s\":").Append(Json.Num(Math.Round(ft.Value)));
            var rk = D(RxGet(u, "ActualRangeInKm"));
            if (rk.HasValue) sb.Append(",\"range_left_km\":").Append(Json.Num(Math.Round(rk.Value, 1)));
            try { sb.Append(",\"loadout\":").Append(Json.Str(Get(Get(u, "_obp"), "_loadoutVariant") as string)); } catch { }
            string ws = WeaponStatusName(u);
            if (ws != null) sb.Append(",\"weapon_status\":").Append(Json.Str(ws));
            try
            {
                sb.Append(",\"sensors\":{");
                bool first = true;
                foreach (var kv in new[] { new[] { "emcon", "Emcon" }, new[] { "air_radar", "IsAirSearchRadarsOn" }, new[] { "surface_radar", "IsSurfaceSearchRadarsOn" } })
                {
                    var v = RxGet(u, kv[1]);
                    if (!(v is bool)) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append(Json.Str(kv[0])).Append(':').Append(Json.Bool((bool)v));
                }
                sb.Append('}');
            }
            catch { }
            AswAirUnitHook(u, sb); // UI mod only (T-095): "asw" block; no-op in the voice mod
            return sb.Append('}').ToString();
        }

        /// <summary>Extra fields of an air unit; implemented only in the interface mod (ADR-0033), removed by the compiler in the voice mod.</summary>
        partial void AswAirUnitHook(object u, StringBuilder sb);

        private string AirUnitsJson()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var u in AirUnits())
            {
                string j;
                try { j = AirUnitJson(u); } catch { continue; }
                if (!first) sb.Append(',');
                first = false;
                sb.Append(j);
            }
            return sb.Append(']').ToString();
        }

        private int PendingCount(object deck)
        {
            int n = 0;
            var tasks = Get(deck, "FlightDeckTasks") as IEnumerable;
            if (tasks != null)
                foreach (var t in tasks) { if (t != null && t.GetType().Name == "PendingLaunchTask") n++; }
            return n;
        }

        private int OnBoard(object deck)
        {
            var n = D(Get(deck, "AircraftCurrentlyOnBoard"));
            if (n.HasValue) return (int)n.Value;
            int sum = 0;
            var vs = Get(deck, "_vehiclesOnBoard") as IEnumerable;
            if (vs != null) foreach (var v in vs) sum += (int)(D(Get(v, "Numbers")) ?? 0);
            return sum;
        }

        private int InAir(object deck)
        {
            try { return (int)Call(deck, "TotalVehiclesInAir"); } catch { return 0; }
        }

        private string DecksJson()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var ship in DeckShips())
            {
                var deck = DeckOf(ship);
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"uid\":").Append(UidOf(ship)).Append(",\"name\":").Append(Json.Str(NameOf(ship)));
                sb.Append(",\"on_board\":").Append(OnBoard(deck)).Append(",\"in_air\":").Append(InAir(deck));
                sb.Append(",\"pending\":").Append(PendingCount(deck));
                sb.Append(",\"operable\":").Append(Json.Bool(!BoolOf(RxGet(deck, "Inoperable"))));
                var mx = D(Get(deck, "MaxAircraftOnBoard"));
                sb.Append(",\"max_on_board\":").Append(mx.HasValue ? Json.Num(mx.Value) : "null").Append('}');
            }
            return sb.Append(']').ToString();
        }

        private string DefaultWeaponStateName()
        {
            var om = SingletonInstance("SeaPower.OptionsManager");
            var v = Get(om, "AircraftDefaultWeaponState");
            if (v == null) v = RxGet(om, "AircraftDefaultWeaponState");
            return v == null ? null : v.ToString().ToLowerInvariant();
        }

        private string DeckFullJson(object ship)
        {
            var deck = DeckOf(ship);
            var sb = new StringBuilder("{\"uid\":");
            sb.Append(UidOf(ship)).Append(",\"name\":").Append(Json.Str(NameOf(ship)));
            var mx = D(Get(deck, "MaxAircraftOnBoard"));
            sb.Append(",\"max_on_board\":").Append(mx.HasValue ? Json.Num(mx.Value) : "null");
            sb.Append(",\"in_air\":").Append(InAir(deck));
            sb.Append(",\"operable\":").Append(Json.Bool(!BoolOf(RxGet(deck, "Inoperable"))));
            sb.Append(",\"default_weapon_state\":").Append(Json.Str(DefaultWeaponStateName()));
            sb.Append(",\"vehicles\":[");
            bool first = true;
            var vs = Get(deck, "_vehiclesOnBoard") as IEnumerable;
            if (vs != null)
                foreach (var v in vs)
                {
                    if (!first) sb.Append(','); first = false;
                    string vt = Get(v, "_type") == null ? "None" : Get(v, "_type").ToString();
                    sb.Append("{\"type\":").Append(Json.Str(Get(v, "_fileName") as string));
                    sb.Append(",\"type_name\":").Append(Json.Str(Get(v, "DisplayName") as string));
                    sb.Append(",\"kind\":").Append(Json.Str(KindOf(vt)));
                    sb.Append(",\"numbers\":").Append(Json.Num(D(Get(v, "Numbers")) ?? 0));
                    sb.Append(",\"squadrons\":[");
                    bool f2 = true;
                    var sq = Get(v, "Squadrons") as IEnumerable;
                    if (sq != null)
                        foreach (var s in sq)
                        {
                            if (!f2) sb.Append(','); f2 = false;
                            sb.Append("{\"name\":").Append(Json.Str(Get(s, "_name") as string)).Append(",\"numbers\":").Append(Json.Num(D(Get(s, "Numbers")) ?? 0)).Append('}');
                        }
                    sb.Append("],\"loadouts\":[");
                    bool f3 = true;
                    var lo = Get(v, "Loadouts") as IEnumerable;
                    if (lo != null)
                        foreach (var l in lo)
                        {
                            if (!f3) sb.Append(','); f3 = false;
                            bool can = false;
                            try { can = (bool)CallOpt(deck, "CanLaunchVehicle", l, 1); } catch { }
                            sb.Append("{\"name\":").Append(Json.Str(Get(l, "Name") as string));
                            sb.Append(",\"display\":").Append(Json.Str(Get(l, "DisplayName") as string));
                            sb.Append(",\"year\":").Append(Json.Num(D(Get(l, "ServiceEntryYear")) ?? 0));
                            var av = D(RxGet(l, "Available"));
                            sb.Append(",\"available\":").Append(av.HasValue ? Json.Num(av.Value) : "null");
                            sb.Append(",\"can_launch\":").Append(Json.Bool(can)).Append('}');
                        }
                    sb.Append("]}");
                }
            sb.Append("],\"tasks\":[");
            bool f4 = true;
            var tasks = Get(deck, "FlightDeckTasks") as IEnumerable;
            double now = MissionTimeS() ?? 0;
            if (tasks != null)
                foreach (var t in tasks)
                {
                    if (t == null) continue;
                    bool pend = t.GetType().Name == "PendingLaunchTask";
                    if (!f4) sb.Append(','); f4 = false;
                    sb.Append("{\"kind\":").Append(Json.Str(pend ? "pending_launch" : "other"));
                    sb.Append(",\"type_name\":").Append(Json.Str(Get(t, "AircraftType") as string));
                    if (pend)
                    {
                        sb.Append(",\"count\":").Append(Json.Num(D(Get(t, "LaunchCount")) ?? 0));
                        sb.Append(",\"launch_allowed\":").Append(Json.Bool(BoolOf(Get(t, "launchAllowed"))));
                        var dur = D(Get(t, "_duration")); var t0 = D(Get(t, "_startTime"));
                        if (dur.HasValue && dur.Value >= 0)
                        {
                            double rem = dur.Value - ((t0.HasValue && t0.Value > 0 && now > 0) ? now - t0.Value : 0); // approximate
                            sb.Append(",\"duration_s\":").Append(Json.Num(Math.Round(dur.Value))).Append(",\"remaining_s\":").Append(Json.Num(Math.Round(Math.Max(0, rem))));
                        }
                    }
                    sb.Append('}');
                }
            return sb.Append("]}").ToString();
        }

        private string FlightDeckCmd(string id, Dictionary<string, object> cmd)
        {
            Need("aviation");
            if (!InMission()) return Err(id, "not_in_session", null);
            var ships = new List<object>();
            if (cmd.ContainsKey("uid") && cmd["uid"] != null)
            {
                var s = FindOwnUnit((int)(D(cmd["uid"]) ?? -1));
                if (s == null) return Err(id, "unit_not_found", null);
                if (DeckOf(s) == null) return Err(id, "no_flight_deck", NameOf(s));
                ships.Add(s);
            }
            else ships = DeckShips();
            if (ships.Count == 0) return Err(id, "no_flight_deck", null);
            var sb = new StringBuilder("\"decks\":[");
            for (int i = 0; i < ships.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(DeckFullJson(ships[i]));
            }
            return Ok(id, sb.Append(']').ToString());
        }

        // ---------- launch ----------

        private sealed class TightWatch
        {
            public object Ship;
            public int Remaining;
            public HashSet<object> Known;
            public double Deadline;
        }
        private readonly List<TightWatch> _tight = new List<TightWatch>();
        private sealed class AirTrack { public bool InFlight; public string State; }
        private readonly Dictionary<object, AirTrack> _airTracks = new Dictionary<object, AirTrack>(new RefEq());

        private double WatchNow() { return MissionTimeS() ?? Clock(); }

        private string LaunchAircraftCmd(string id, Dictionary<string, object> cmd)
        {
            // arguments first (offline smoke test has no capabilities)
            int count = 1;
            if (cmd.ContainsKey("count") && cmd["count"] != null)
            {
                var c = D(cmd["count"]);
                if (!c.HasValue || c.Value < 1 || c.Value > 4 || c.Value != Math.Floor(c.Value)) return Err(id, "bad_args", "count 1..4");
                count = (int)c.Value;
            }
            string typeWish = cmd.ContainsKey("type") ? cmd["type"] as string : null;
            string loadoutWish = cmd.ContainsKey("loadout") ? cmd["loadout"] as string : null;
            bool readyOnly = cmd.ContainsKey("ready_only") && cmd["ready_only"] is bool && (bool)cmd["ready_only"];
            Need("launch_aircraft");
            if (!InMission()) return Err(id, "not_in_session", null);
            // v0.8.1: an aircraft already on the deck waiting for permission (scenario "launch blocked") goes first:
            // the same as the deck window's Launch button, no new ready-up. Only without an explicit loadout.
            bool useReady = !(cmd.ContainsKey("use_ready") && cmd["use_ready"] is bool && !(bool)cmd["use_ready"]);
            if (!readyOnly && useReady && string.IsNullOrEmpty(loadoutWish) && Capabilities.Contains("allow_launch"))
            {
                int? shipUid = cmd.ContainsKey("uid") && cmd["uid"] != null ? (int?)(int)(D(cmd["uid"]) ?? -1) : null;
                var bt = BlockedTasks(shipUid, typeWish);
                if (bt.Count > 0) return AllowTaskAck(id, bt[0], "launch_aircraft");
            }

            // 1. ship (P3)
            object ship = null;
            if (cmd.ContainsKey("uid") && cmd["uid"] != null)
            {
                ship = FindOwnUnit((int)(D(cmd["uid"]) ?? -1));
                if (ship == null) return Err(id, "unit_not_found", null);
                if (DeckOf(ship) == null) return Err(id, "no_flight_deck", NameOf(ship));
            }
            else
            {
                var cands = new List<object>();
                foreach (var s in DeckShips()) if (FirstVehicle(s, typeWish) != null) cands.Add(s);
                string src; var sel = SelectedUnit(out src);
                if (sel != null && cands.Contains(sel)) ship = sel;
                else if (cands.Count == 1) ship = cands[0];
                else if (cands.Count == 0) return Err(id, "no_aircraft", typeWish);
                else
                {
                    var names = new List<string>();
                    foreach (var s in cands) names.Add(NameOf(s));
                    return Err(id, "ambiguous_ship", string.Join("; ", names.ToArray()));
                }
            }
            var deck = DeckOf(ship);

            // 2. type, squadron, callsign, count
            var vehicle = FirstVehicle(ship, typeWish);
            if (vehicle == null) return Err(id, "no_aircraft", typeWish);
            string vtype = Get(vehicle, "_type") == null ? "None" : Get(vehicle, "_type").ToString();
            if (vtype == "Helicopter" && Find(deck.GetType(), "CanOperateHelicopterInCurrentSeaState", false) != null)
            {
                bool okSea = true;
                try { okSea = (bool)Call(deck, "CanOperateHelicopterInCurrentSeaState", vehicle); } catch { }
                if (!okSea) return Err(id, "sea_state", "helicopters cannot operate in the current sea state");
            }
            object squadron = null;
            var sqs = Get(vehicle, "Squadrons") as IEnumerable;
            if (sqs != null) foreach (var s in sqs) { if ((D(Get(s, "Numbers")) ?? 0) > 0) { squadron = s; break; } }
            if (squadron == null) return Err(id, "no_aircraft", "no squadron with aircraft");
            string callsign = null;
            var cs = Get(squadron, "Callsigns") as IEnumerable;
            if (cs != null) foreach (var c in cs) { callsign = c as string; break; }
            if (string.IsNullOrEmpty(callsign)) return Err(id, "no_callsign", null);
            int slots = (int)(D(Get(deck, "DeckParkSlots")) ?? 32);
            count = Math.Min(count, Math.Min((int)(D(Get(squadron, "Numbers")) ?? 1), Math.Max(1, slots)));

            // 3. loadout (P2)
            var loadouts = new List<object>();
            var infos = new List<LoadoutInfo>();
            var lo = Get(vehicle, "Loadouts") as IEnumerable;
            if (lo != null)
                foreach (var l in lo)
                {
                    bool can = false;
                    try { can = (bool)CallOpt(deck, "CanLaunchVehicle", l, count); } catch { }
                    loadouts.Add(l);
                    infos.Add(new LoadoutInfo { Name = Get(l, "Name") as string, Display = Get(l, "DisplayName") as string, Year = (int)(D(Get(l, "ServiceEntryYear")) ?? 0), CanLaunch = can });
                }
            string lerr;
            int year = MissionYear();
            int li = PickLoadout(infos, loadoutWish, year, out lerr);
            if (li < 0)
            {
                if (lerr == "unknown_loadout")
                {
                    var avail = new List<string>();
                    foreach (var x in infos) if (year <= 0 || x.Year <= 0 || x.Year <= year) avail.Add(x.Name);
                    return Err(id, lerr, string.Join(", ", avail.ToArray()));
                }
                return Err(id, lerr, loadoutWish);
            }
            var loadout = loadouts[li];

            // 4. the same call as the deck window (Launch / Ready)
            var ltpT = FindType("SeaPower.LaunchTaskParameters");
            object ltp = Activator.CreateInstance(ltpT);
            SetField(ltp, "_launchCount", count);
            var before = new HashSet<object>(new RefEq());
            var kids = Get(ship, "_childAircraft") as IEnumerable;
            if (kids != null) foreach (var k in kids) before.Add(k);
            object task;
            try { task = CallOpt(deck, "createLaunchTask", vehicle, loadout, squadron, callsign, ltp, !readyOnly); }
            catch (TargetInvocationException e) { return Err(id, "launch_failed", (e.InnerException ?? e).GetType().Name); }
            if (task == null) return Err(id, "no_ammo", "the deck cannot supply this loadout");
            if (!readyOnly) _tight.Add(new TightWatch { Ship = ship, Remaining = count, Known = before, Deadline = WatchNow() + 1800 });

            var dur = D(Get(task, "_duration"));
            if (!dur.HasValue || dur.Value < 0) dur = D(Get(loadout, "_readyUpDuration")) * count;
            double readyS = dur.HasValue && dur.Value > 0 ? dur.Value : 0;
            return Ok(id, "\"ship_uid\":" + UidOf(ship) + ",\"ship\":" + Json.Str(NameOf(ship)) +
                ",\"type\":" + Json.Str(Get(vehicle, "_fileName") as string) + ",\"type_name\":" + Json.Str(Get(vehicle, "DisplayName") as string) +
                ",\"kind\":" + Json.Str(KindOf(vtype)) +
                ",\"loadout\":" + Json.Str(Get(loadout, "Name") as string) + ",\"loadout_name\":" + Json.Str(Get(loadout, "DisplayName") as string) +
                ",\"squadron\":" + Json.Str(Get(squadron, "_name") as string) + ",\"callsign\":" + Json.Str(callsign) + ",\"count\":" + count +
                ",\"ready_s\":" + Json.Num(Math.Round(readyS)) + ",\"ready_min\":" + Json.Num(Math.Ceiling(readyS / 60.0)) +
                ",\"ready_only\":" + Json.Bool(readyOnly) +
                ",\"default_weapon_state\":" + Json.Str(DefaultWeaponStateName()) + ",\"force_tight\":" + Json.Bool(!readyOnly));
        }

        // ---------- allow launch (v0.8.1) ----------

        private sealed class BlockedTask { public object Ship, Task; }

        /// <summary>Pending launch tasks of the player's decks with launch blocked, matching the ship/type wish.</summary>
        private List<BlockedTask> BlockedTasks(int? shipUid, string typeWish)
        {
            var res = new List<BlockedTask>();
            foreach (var ship in DeckShips())
            {
                if (shipUid.HasValue && UidOf(ship) != shipUid.Value) continue;
                var tasks = Get(DeckOf(ship), "FlightDeckTasks") as IEnumerable;
                if (tasks == null) continue;
                foreach (var tk in new List<object>(Cast(tasks)))
                {
                    if (tk == null || tk.GetType().Name != "PendingLaunchTask" || BoolOf(Get(tk, "launchAllowed"))) continue;
                    if ((D(Get(tk, "LaunchCount")) ?? 0) < 1) continue;
                    var vt = Get(tk, "VehicleType");
                    string k = Get(vt, "_type") == null ? "None" : Get(vt, "_type").ToString();
                    if (!TypeMatches(typeWish, k, Get(vt, "_fileName") as string, Get(vt, "DisplayName") as string)) continue;
                    res.Add(new BlockedTask { Ship = ship, Task = tk });
                }
            }
            return res;
        }

        private string AllowLaunchCmd(string id, Dictionary<string, object> cmd)
        {
            Need("allow_launch");
            if (!InMission()) return Err(id, "not_in_session", null);
            int? shipUid = cmd.ContainsKey("uid") && cmd["uid"] != null ? (int?)(int)(D(cmd["uid"]) ?? -1) : null;
            if (shipUid.HasValue && FindOwnUnit(shipUid.Value) == null) return Err(id, "unit_not_found", null);
            string typeWish = cmd.ContainsKey("type") ? cmd["type"] as string : null;
            var bt = BlockedTasks(shipUid, typeWish);
            if (bt.Count == 0) return Err(id, "no_blocked_launch", typeWish);
            return AllowTaskAck(id, bt[0], "allow_launch");
        }

        /// <summary>The deck window path: "Launch" button while the task waits to spawn, the toggle while it still readies up.</summary>
        private string AllowTaskAck(string id, BlockedTask b, string via)
        {
            var tk = b.Task;
            string st = null;
            try { st = Get(Get(tk, "_stateMachine"), "CurrentState").GetType().Name; } catch { }
            try
            {
                if (st == "HandleAwaitSpawnTask") Call(tk, "AllowLaunchFunc", new object[] { null });
                else Call(tk, "ToggleLaunchAllowedFunc", new object[] { null });
            }
            catch (TargetInvocationException e) { return Err(id, "launch_failed", (e.InnerException ?? e).GetType().Name); }
            if (!BoolOf(Get(tk, "launchAllowed"))) return Err(id, "launch_failed", "launch still blocked");
            int count = (int)(D(Get(tk, "LaunchCount")) ?? 1);
            var before = new HashSet<object>(new RefEq());
            var kids = Get(b.Ship, "_childAircraft") as IEnumerable;
            if (kids != null) foreach (var k in kids) before.Add(k);
            _tight.Add(new TightWatch { Ship = b.Ship, Remaining = count, Known = before, Deadline = WatchNow() + 1800 });
            var vt = Get(tk, "VehicleType");
            string kind = Get(vt, "_type") == null ? "None" : Get(vt, "_type").ToString();
            double readyS = 0;
            if (st != "HandleAwaitSpawnTask")
            {
                var dur = D(Get(tk, "_duration")); var t0 = D(Get(tk, "_startTime")); double now = MissionTimeS() ?? 0;
                if (dur.HasValue && dur.Value > 0) readyS = Math.Max(0, dur.Value - ((t0.HasValue && t0.Value > 0 && now > 0) ? now - t0.Value : 0));
            }
            string lo = null;
            try
            {
                var los = Get(vt, "Loadouts") as IList; var li = D(Get(tk, "_loadoutIndex"));
                if (los != null && li.HasValue && li.Value >= 0 && li.Value < los.Count) lo = Get(los[(int)li.Value], "Name") as string;
            }
            catch { }
            return Ok(id, "\"ship_uid\":" + UidOf(b.Ship) + ",\"ship\":" + Json.Str(NameOf(b.Ship)) +
                ",\"type\":" + Json.Str(Get(vt, "_fileName") as string) + ",\"type_name\":" + Json.Str(Get(vt, "DisplayName") as string) +
                ",\"kind\":" + Json.Str(KindOf(kind)) + ",\"loadout\":" + Json.Str(lo) + ",\"count\":" + count +
                ",\"allowed_pending\":true,\"via\":" + Json.Str(via) + ",\"task_state\":" + Json.Str(st) +
                ",\"ready_s\":" + Json.Num(Math.Round(readyS)) + ",\"ready_min\":" + Json.Num(Math.Ceiling(readyS / 60.0)) +
                ",\"ready_only\":false,\"default_weapon_state\":" + Json.Str(DefaultWeaponStateName()) + ",\"force_tight\":true");
        }

        /// <summary>First deck vehicle type with aircraft on board that fits the wish.</summary>
        private object FirstVehicle(object ship, string typeWish)
        {
            var vs = Get(DeckOf(ship), "_vehiclesOnBoard") as IEnumerable;
            if (vs == null) return null;
            foreach (var v in vs)
            {
                if ((D(Get(v, "Numbers")) ?? 0) < 1) continue;
                string vt = Get(v, "_type") == null ? "None" : Get(v, "_type").ToString();
                if (TypeMatches(typeWish, vt, Get(v, "_fileName") as string, Get(v, "DisplayName") as string)) return v;
            }
            return null;
        }

        // ---------- altitude / RTB / recover ----------

        private string SetAltitudeCmd(string id, Dictionary<string, object> cmd)
        {
            var f = cmd.ContainsKey("feet") ? D(cmd["feet"]) : null;
            if (!f.HasValue || f.Value < 0 || f.Value > 100000) return Err(id, "bad_args", "feet 0..100000");
            Need("set_altitude");
            string err; var u = AirTarget(cmd, out err);
            if (u == null) return Err(id, err, null);
            if (!InFlight(u)) return Err(id, "not_in_flight", null);
            var units = PresetsUnits(u);
            if (units == null || units.Length == 0) return Err(id, "no_presets", null);
            var ft = new float[units.Length];
            for (int i = 0; i < units.Length; i++) ft[i] = (float)Math.Round(units[i] * FeetPerUnit);
            int idx = NearestPreset(ft, f.Value);
            // Orders menu "Set Altitude": setPresetHeight(i, updateAltForWaypoints:true) + setPlayerHeightControl(false)
            Call(u, "setPresetHeight", idx, true);
            Call(u, "setPlayerHeightControl", false);
            var sb = new StringBuilder();
            for (int i = 0; i < ft.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Num(ft[i])); }
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"requested_ft\":" + Json.Num(f.Value) +
                ",\"alt_ft\":" + Json.Num(ft[idx]) + ",\"preset\":" + idx + ",\"presets_ft\":[" + sb + "]");
        }

        private string ReturnToBaseCmd(string id, Dictionary<string, object> cmd)
        {
            Need("return_to_base");
            string err; var u = AirTarget(cmd, out err);
            if (u == null) return Err(id, err, null);
            if (!InFlight(u)) return Err(id, "not_in_flight", null);
            var home = Call(u, "getHomeBase");
            if (home == null || !IsAlive(home)) return Err(id, "no_home_base", null);
            var m = SetOrderMethod();
            var en = Enum.Parse(FindType("SeaPower.Order+Type"), "ReturnToBase");
            m.Invoke(u, new object[] { en, home, true }); // Orders menu "Return To Base"
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"base_uid\":" + UidOf(home) + ",\"base_name\":" + Json.Str(NameOf(home)));
        }

        private string RecoverAllCmd(string id, Dictionary<string, object> cmd)
        {
            if (!Confirmed(cmd)) return Err(id, "confirm_required", "recover_all needs \"confirm\":true");
            Need("recover_all");
            if (!InMission()) return Err(id, "not_in_session", null);
            var ships = new List<object>();
            if (cmd.ContainsKey("uid") && cmd["uid"] != null)
            {
                var s = FindOwnUnit((int)(D(cmd["uid"]) ?? -1));
                if (s == null) return Err(id, "unit_not_found", null);
                if (DeckOf(s) == null) return Err(id, "no_flight_deck", NameOf(s));
                ships.Add(s);
            }
            else ships = DeckShips();
            if (ships.Count == 0) return Err(id, "no_flight_deck", null);
            int total = 0;
            var sb = new StringBuilder("[");
            var counts = new List<int>();
            foreach (var s in ships)
            {
                int n = 0;
                var kids = Get(s, "_childAircraft") as IEnumerable;
                if (kids != null) foreach (var k in kids) { if (k != null && IsAlive(k) && IsAirUnit(k) && InFlight(k)) n++; }
                counts.Add(n); total += n;
            }
            if (total == 0) return Err(id, "no_air_units", null);
            for (int i = 0; i < ships.Count; i++)
            {
                if (counts[i] > 0) CallOpt(DeckOf(ships[i]), "RecoverAll"); // the Recover All button: instant=false
                if (i > 0) sb.Append(',');
                sb.Append("{\"uid\":").Append(UidOf(ships[i])).Append(",\"name\":").Append(Json.Str(NameOf(ships[i]))).Append(",\"recalled\":").Append(counts[i]).Append('}');
            }
            return Ok(id, "\"recalled\":" + total + ",\"ships\":" + sb.Append(']'));
        }

        // ---------- events (1 Hz from PumpEvents) ----------

        private static bool IsReturnState(string s) { return s == "ReturnToBase" || s == "BingoFuel" || s == "Winchester"; }

        private void PumpAviation()
        {
            if (!Capabilities.Contains("aviation")) return;
            var air = AirUnits();
            // 1. state transitions of own aircraft
            foreach (var u in air)
            {
                bool fl = InFlight(u);
                string st = StateName(u, "CurrentStateName");
                AirTrack tr;
                if (!_airTracks.TryGetValue(u, out tr)) { _airTracks[u] = new AirTrack { InFlight = fl, State = st }; continue; }
                if (fl && !tr.InFlight)
                    EvLine("aircraft_airborne", "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"class\":" + Json.Str(Get(u, "ClassName") as string) +
                        ",\"ship_uid\":" + (Get(u, "_homeBase") == null ? "null" : UidOf(Get(u, "_homeBase")).ToString()) + "}");
                if (st != tr.State && IsReturnState(st))
                    EvLine("air_state", "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"state\":" + Json.Str(st) + ",\"prev\":" + Json.Str(tr.State) + "}");
                tr.InFlight = fl; tr.State = st;
            }
            // 2. v0.8.1: pending launches that vanished without launching (deck made inoperable -> KillAircraft clears the tasks, abort)
            PumpPendingLaunches();
            try { PumpDeckOperable(air); } catch { }
            // 3. aircraft launched by the bot: force Tight (customer decision 2026-10-15: only the bot's own launches)
            if (_tight.Count == 0) return;
            double now = WatchNow();
            for (int i = _tight.Count - 1; i >= 0; i--)
            {
                var w = _tight[i];
                var kids = Get(w.Ship, "_childAircraft") as IEnumerable;
                if (kids != null && IsAlive(w.Ship))
                    foreach (var k in new List<object>(Cast(kids)))
                    {
                        if (k == null || w.Known.Contains(k) || w.Remaining <= 0) continue;
                        w.Known.Add(k); w.Remaining--;
                        string was = WeaponStatusName(k);
                        bool forced = false;
                        if (was != "tight")
                        {
                            try
                            {
                                CallOpt(k, "SetWeaponStatus", Enum.Parse(FindType("SeaPower.ObjectBase+WeaponStatus"), "Tight"), true);
                                forced = true;
                            }
                            catch { }
                        }
                        EvLine("aircraft_launching", "{\"uid\":" + UidOf(k) + ",\"name\":" + Json.Str(NameOf(k)) + ",\"class\":" + Json.Str(Get(k, "ClassName") as string) +
                            ",\"ship_uid\":" + UidOf(w.Ship) + ",\"weapon_status\":" + Json.Str(WeaponStatusName(k)) + ",\"was\":" + Json.Str(was) + ",\"forced\":" + Json.Bool(forced) + "}");
                    }
                if (w.Remaining <= 0 || now > w.Deadline || !IsAlive(w.Ship)) _tight.RemoveAt(i);
            }
        }

        private sealed class PendWatch { public object Ship; public string Type, Name; public int Count; }
        private readonly Dictionary<object, PendWatch> _pending = new Dictionary<object, PendWatch>(new RefEq());

        private readonly HashSet<object> _deckDown = new HashSet<object>(new RefEq());

        /// <summary>v0.8.1: a hit makes the flight deck inoperable (BaseSystem.MakeInoperable -> KillAircraft): pending launches
        /// are cleared and returning aircraft lose their home base. Reported once per ship.</summary>
        private void PumpDeckOperable(List<object> air)
        {
            foreach (var ship in DeckShips())
            {
                bool down = BoolOf(RxGet(DeckOf(ship), "Inoperable"));
                if (!down) { _deckDown.Remove(ship); continue; }
                if (!_deckDown.Add(ship)) continue;
                var homeless = new StringBuilder("[");
                bool first = true;
                foreach (var u in air)
                {
                    if (!InFlight(u) || Get(u, "_homeBase") != null) continue;
                    if (!first) homeless.Append(','); first = false;
                    homeless.Append("{\"uid\":").Append(UidOf(u)).Append(",\"name\":").Append(Json.Str(NameOf(u))).Append('}');
                }
                EvLine("deck_inoperable", "{\"ship_uid\":" + UidOf(ship) + ",\"ship\":" + Json.Str(NameOf(ship)) + ",\"aircraft_without_base\":" + homeless.Append(']') + "}");
            }
        }

        private void PumpPendingLaunches()
        {
            var seen = new HashSet<object>(new RefEq());
            foreach (var ship in DeckShips())
            {
                var tasks = Get(DeckOf(ship), "FlightDeckTasks") as IEnumerable;
                if (tasks == null) continue;
                foreach (var tk in new List<object>(Cast(tasks)))
                {
                    if (tk == null || tk.GetType().Name != "PendingLaunchTask") continue;
                    seen.Add(tk);
                    PendWatch w;
                    if (!_pending.TryGetValue(tk, out w)) _pending[tk] = w = new PendWatch { Ship = ship };
                    var vt = Get(tk, "VehicleType");
                    w.Type = Get(vt, "_fileName") as string; w.Name = Get(vt, "DisplayName") as string;
                    w.Count = (int)(D(Get(tk, "LaunchCount")) ?? 0);
                }
            }
            foreach (var kv in new List<KeyValuePair<object, PendWatch>>(_pending))
            {
                if (seen.Contains(kv.Key)) continue;
                _pending.Remove(kv.Key);
                var w = kv.Value;
                // a task that spawned all its aircraft has LaunchCount 0 when it leaves the deck
                int left = (int)(D(Get(kv.Key, "LaunchCount")) ?? w.Count);
                if (left < 1) continue;
                var deck = IsAlive(w.Ship) ? DeckOf(w.Ship) : null;
                string reason = !IsAlive(w.Ship) || BoolOf(Get(w.Ship, "IsDestroyed")) ? "ship_lost"
                    : BoolOf(RxGet(deck, "Inoperable")) ? "deck_inoperable" : "aborted";
                EvLine("launch_aborted", "{\"ship_uid\":" + (IsAlive(w.Ship) ? UidOf(w.Ship).ToString() : "null") + ",\"ship\":" + Json.Str(IsAlive(w.Ship) ? NameOf(w.Ship) : null) +
                    ",\"type\":" + Json.Str(w.Type) + ",\"type_name\":" + Json.Str(w.Name) + ",\"count\":" + left + ",\"reason\":" + Json.Str(reason) + "}");
            }
        }

        /// <summary>A landed aircraft is reported as aircraft_recovered, not as a loss (DeSpawn marks it with _spawnTime = double.MaxValue).</summary>
        private bool ReportRecovered(object sender)
        {
            if (!IsAirUnit(sender) || !IsRecoveredLanding(sender)) return false;
            if (!BoolOf(Get(sender, "IsPlayerObject"))) return false;
            var home = Get(sender, "_homeBase");
            bool homeOk = home != null && IsAlive(home);
            _reportedDestroyed.Add(sender);
            _airTracks.Remove(sender);
            EvLine("aircraft_recovered", "{\"uid\":" + UidOf(sender) + ",\"name\":" + Json.Str(NameOf(sender)) + ",\"class\":" + Json.Str(Get(sender, "ClassName") as string) +
                ",\"base_uid\":" + (homeOk ? UidOf(home).ToString() : "null") + ",\"base_name\":" + Json.Str(homeOk ? NameOf(home) : null) + "}");
            Forget(sender);
            return true;
        }
    }
}
