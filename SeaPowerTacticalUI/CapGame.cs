// T-112 section 3.5: the real ICapGame (game layer of the CAP window), GameApi partial, reflection only. Owner: stream G.
// Main thread only. Outside a mission every method returns empty or an error code and never throws into the game.
// Offline-unverified: needs the live checks C1-C9 of the plan. Game members were verified against Seapower-Scripts.dll (decompile).
// Compiled into the mod and the UI selfcheck; not into the captest stand (no game assemblies).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        internal CapController Cap;
        private CapGameImpl _capImpl;
        private string _capLastSave; private bool _capLastSaveSeen; private double _capSaveCheckW = -1e9;
        private string _capPendingClear; private double _capPendingClearAt;   // T-130: a mark without groups clears its slot after a grace (see AswController.SlotDeleteGraceS)
        private const double CapSlotClearGraceS = 3.0;
        // T-317 (ADR-0124): AI combat window and selection memory per uid; shared by CAP and SAR (uids are unique)
        internal readonly CapTakeoverTracker TakeTracker = new CapTakeoverTracker();
        private readonly Ui.FormationFacts _takeFormation = new Ui.FormationFacts();
        private bool _capRestoring;           // Restore() -> RestoreControlCmd -> ReleaseRegistry -> cap participant: do not recurse into the controller

        /// <summary>BepInEx/config/SeaPowerTacticalUI/cap, set by the plugin (BepInEx is not referenced here so the selfcheck can compile this file).</summary>
        internal string CapSaveDir = null;

        /// <summary>Opens the CAP window (set by the plugin). Null = no window.</summary>
        internal Ui.ICapWindow CapWindow = null;

        /// <summary>The CAP controller on the game layer. The ownership object is shared with the ASW/SAR controllers (one machine = one automation).</summary>
        internal CapController CreateCapController(CapParams p, AirOwnership own)
        {
            _capImpl = new CapGameImpl(this, p);
            Cap = new CapController(_capImpl, p, own);
            WireAirRadar();   // T-241
            return Cap;
        }

        /// <summary>Per frame, main thread: new mission -> OnMissionChanged (caches reset), save marker. The controller tick comes from the air tick (AirTickCap).</summary>
        internal void PumpCap()
        {
            var c = Cap;
            if (c == null || _capImpl == null) return;
            try
            {
                if (!Frame.InMission) return;
                double t = Frame.MissionTimeS ?? 0;
                NoteTakeSelection(t);   // T-317
                // a new last_save marker = the game wrote a save: persist the groups next to it (file read at most once per second)
                double w = Clock != null ? Clock() : 0;
                if (w - _capSaveCheckW >= 1.0)
                {
                    _capSaveCheckW = w;
                    string sp = _capImpl.LastSavePath;
                    if (!_capLastSaveSeen) { _capLastSaveSeen = true; _capLastSave = sp; }
                    else if (sp != _capLastSave)
                    {
                        _capLastSave = sp;
                        if (SaveHookActive) { }   // T-198: the file is written by the save hook (SaveHookGame.cs); the marker is only followed
                        else if (sp != null && (c.View().Groups.Count > 0 || c.RestoreWaiting)) { _capPendingClear = null; c.PersistNow(); }
                        else if (sp != null) { _capPendingClear = sp; _capPendingClearAt = t + CapSlotClearGraceS; }   // T-130 / P2-13: nothing to keep for this slot
                    }
                    if (_capPendingClear != null && t >= _capPendingClearAt)
                    {
                        string slot = _capPendingClear; _capPendingClear = null;
                        if (c.View().Groups.Count > 0 || c.RestoreWaiting) c.PersistNow(); else c.ClearSlot(slot);   // T-211: groups waiting for the world are kept
                    }
                }
            }
            catch (Exception e)
            {
                try { _capImpl.Log("[Cap] pump error: " + e.GetType().Name + ": " + e.Message); } catch { }
            }
        }

        /// <summary>One step (1 s of game time) of the CAP controller, called by the air tick between BeginTick and EndTick of the deck broker.</summary>
        internal void AirTickCap(double t)
        {
            var c = Cap;
            if (c == null || _capImpl == null) return;
            try
            {
                if (!Frame.InMission) return;
                c.Tick(t);
            }
            catch (Exception e)
            {
                try { _capImpl.Log("[Cap] tick error: " + e.GetType().Name + ": " + e.Message); } catch { }
            }
        }

        /// <summary>The CAP window owner of the shared mouse flag (UiMouseBlock, see SetMouseBlock).</summary>
        internal void SetCapBlock(bool on) { SetMouseBlock("cap", on); }

        /// <summary>"Return control" participant (X4, ReleaseRegistry): a CAP-group member leaves its group; ROE and route come back through the normal restore.</summary>
        internal void CapRelease(int uid)
        {
            try
            {
                if (_capRestoring || Cap == null) return;
                Cap.ReleaseUid(uid);
            }
            catch { }
        }


        /// <summary>cap_group|cap_order|cap_loadout|cap_params|cap_stop (v0.18)|cap_scramble (T-179). Main thread. Every command and its answer go to the [Cap] log.</summary>
        internal string CapCmd(string id, string name, Dictionary<string, object> cmd)
        {
            if (Cap == null) return Err(id, "unsupported", "cap controller is off");
            string r = CapRaw(name, cmd);
            if (r == null) return null;
            try
            {
                string line = "[Cap] cap cmd " + name + " " + AswArgs(cmd) + " -> " + (AswController.IsErr(r) ? r : "ok");
                var sink = AswLogSink;
                if (sink != null) sink(line); else UnityDebugLog(line);
            }
            catch { }
            return AswAck(id, r);
        }

        /// <summary>The controller answer as is: ack json or "!code" (the window path; the window logs by itself). null = not a cap command.</summary>
        internal string CapRaw(string name, Dictionary<string, object> cmd)
        {
            var c = Cap;
            if (c == null) return "!unsupported";
            switch (name)
            {
                case "cap_group": return c.Group(cmd);
                case "cap_order": return c.Order(cmd);
                case "cap_loadout": return c.Loadout(cmd);
                case "cap_params": return c.Params(cmd);
                case "cap_stop": return c.Stop(cmd);
                case "cap_scramble": return c.ScrambleNow(cmd);   // T-179 / ADR-0079
                default: return null;
            }
        }

        /// <summary>T-317: every frame, the unit the player has selected goes into the takeover tracker (orders and menus act on the selected unit only).</summary>
        internal void NoteTakeSelection(double t)
        {
            try
            {
                string src; var u = SelectedUnit(out src);
                if (u == null) return;
                int uid = UidOf(u);
                if (uid > 0) TakeTracker.NoteSelected(uid, t);
            }
            catch { }
        }

        /// <summary>T-317: an order to a formation goes through its leader: the member counts as selected when the leader was.</summary>
        internal void TakeInheritLeader(object unit, int uid)
        {
            try
            {
                if (ReadFormation(unit, _takeFormation) && _takeFormation.InFormation && _takeFormation.LeaderUid > 0) TakeTracker.Inherit(uid, _takeFormation.LeaderUid);
            }
            catch { }
        }

        /// <summary>Position of an own unit (bird markers of the overlay, anchor ship); null = unknown.</summary>
        internal GeoPt? CapUnitPos(int uid) { return _capImpl == null ? (GeoPt?)null : _capImpl.UnitPos(uid); }

        /// <summary>Uid of the unit selected in the game (0 = none): origin and anchor of a new zone.</summary>
        internal int CapSelectedUid()
        {
            try { string src; var u = SelectedUnit(out src); return u == null ? 0 : UidOf(u); }
            catch { return 0; }
        }

        /// <summary>T-178 C3: the course of an own ship, degrees (the compass of the zone by bearing); null = unknown or slower than 3 kn.</summary>
        internal double? CapShipCourse(int uid)
        {
            try
            {
                var u = FindOwnUnit(uid);
                if (u == null) return null;
                double kn = Math.Abs(D(Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0);
                if (kn < 3) return null;
                double? h = D(RxGet(u, EvasionSpecs.ObjHeading));
                return h.HasValue ? (double?)CompassMath.Norm(h.Value) : null;
            }
            catch { return null; }
        }

        /// <summary>CAP loadout names of the game for a vehicle type (AirOps loadouts of the first deck that carries it); empty = unknown.</summary>
        internal IList<string> CapLoadoutNames(string type)
        {
            return _capImpl == null ? new List<string>() : _capImpl.CapLoadoutNames(type);
        }

        /// <summary>The game's display name of a loadout of a vehicle type (null = unknown / none).</summary>
        /// <summary>T-175: minutes to prepare the loadout on the deck (ReadyUpMin); "auto" = the usual pick of the planner; -1 = unknown.</summary>
        internal double CapLoadoutReadyMin(string type, string name)
        {
            return _capImpl == null ? -1 : _capImpl.CapLoadoutReadyMin(type, name);
        }

        internal string CapLoadoutDisplay(string type, string name)
        {
            return _capImpl == null ? null : _capImpl.CapLoadoutDisplay(type, name);
        }

        /// <summary>Fragment "cap" for link_state; null when the controller is off.</summary>
        internal string CapStateJson() { return Cap == null ? null : Cap.StateJson(); }

        private sealed class CapGameImpl : ICapGame
        {
            private const double KmPerNm = 1.852;
            private const double MapUnitsPerKm = 1000.0 * 0.0148809375;   // Aircraft.RangeOnMap = km * 1000 * 0.0148809375
            private const double FeetPerUnitAlt = 220.47266;              // as Aviation.cs FeetPerUnit
            private const double DefaultPatrolKn = 420, CruiseDefaultKn = 450;

            private readonly GameApi _a;
            private readonly CapParams _p;
            private readonly Dictionary<string, CapAmmo> _ammo = new Dictionary<string, CapAmmo>();
            private readonly Dictionary<string, string> _admission = new Dictionary<string, string>();
            private readonly Dictionary<string, KeyValuePair<double, List<LoadoutOption>>> _losCache = new Dictionary<string, KeyValuePair<double, List<LoadoutOption>>>();
            private readonly Dictionary<int, Dictionary<string, int>> _loadSeen = new Dictionary<int, Dictionary<string, int>>();   // T-325: uid -> max count per weapon seen
            private readonly Dictionary<int, int[]> _start = new Dictionary<int, int[]>();   // uid -> max {aam, long} seen (AamStart/LongStart)
            private readonly HashSet<int> _rtbByUs = new HashSet<int>();
            private readonly HashSet<string> _hiddenLogged = new HashSet<string>();
            private readonly Dictionary<string, string> _classOf = new Dictionary<string, string>();   // type -> VehicleType seen on a deck (log only)

            private readonly Dictionary<int, string> _takeDetail = new Dictionary<int, string>();   // T-317: the last deviating TakenByPlayer check
            private readonly HashSet<int> _takeDrift = new HashSet<int>();

            public CapGameImpl(GameApi a, CapParams p) { _a = a; _p = p; }

            internal void ResetMission()
            {
                _ammo.Clear(); _admission.Clear(); _losCache.Clear(); _start.Clear(); _rtbByUs.Clear(); _hiddenLogged.Clear(); _classOf.Clear(); _loadSeen.Clear();
                _a.TakeTracker.Clear(); _takeDetail.Clear(); _takeDrift.Clear();   // T-317
                _icptVeh.Clear();   // T-333: Vehicle refs of the intercept targets do not outlive the mission
            }

            private object Get(object o, MemberSpec s) { return _a.Get(o, s); }
            private object Unit(int uid) { try { return uid < 0 ? null : _a.FindOwnUnit(uid); } catch { return null; } }

            public double Now { get { try { return _a.Frame.MissionTimeS ?? 0; } catch { return 0; } } }

            public double TimeScale
            {
                get
                {
                    try
                    {
                        var v = D(_a.GetStatic(CoreSpecs.TimeCompression));
                        return v ?? 1;
                    }
                    catch { return 1; }
                }
            }

            public string LastSavePath
            {
                get
                {
                    return ReadLastSavePath();
                }
            }

            public string SaveDir { get { return _a.CapSaveDir; } }

            // ---- ammunition (game data: AmmunitionParameters.GetShared reads the game ini with the Workshop overlay already applied) ----

            public CapAmmo Ammo(string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                CapAmmo c;
                if (_ammo.TryGetValue(name, out c)) return c;
                try
                {
                    var ap = _a.CallStatic(CapSpecs.AmmoGetShared, name);
                    if (ap != null)
                    {
                        c = new CapAmmo
                        {
                            Name = name,
                            Type = Str(Get(ap, CapSpecs.AmmoType)),
                            TargetType = Str(Get(ap, CapSpecs.AmmoTargetType)),
                            SecondaryTargetType = Str(Get(ap, CapSpecs.AmmoSecondaryTarget)),
                            MaxLaunchRangeNm = D(Get(ap, CapSpecs.AmmoMaxLaunchRange)) ?? 0,
                            FuelKg = D(Get(ap, CapSpecs.AmmoFuelKg)) ?? 0,
                            Label = Str(Get(ap, CapSpecs.AmmoDisplayName))
                        };
                    }
                }
                catch { c = null; }
                _ammo[name] = c;
                return c;
            }

            private static string Str(object o) { return o == null ? null : o.ToString(); }

            private bool IsAamName(string name) { var a = Ammo(name); return a != null && CapMath.IsAam(a); }

            // ---- loadouts and admission ----

            public List<LoadoutOption> Loadouts(int deckUid, string type)
            {
                try
                {
                    string k = deckUid + "|" + type;
                    KeyValuePair<double, List<LoadoutOption>> c;
                    double now = Now;
                    if (_losCache.TryGetValue(k, out c) && now - c.Key < 5 && now >= c.Key) return c.Value;
                    var l = _a.AirOps.Loadouts(deckUid, type);
                    _losCache[k] = new KeyValuePair<double, List<LoadoutOption>>(now, l);
                    return l;
                }
                catch { return new List<LoadoutOption>(); }
            }

            public IList<string> CapLoadoutNames(string type)
            {
                var res = new List<string>();
                try
                {
                    int deck; string cls;
                    if (string.IsNullOrEmpty(type) || !FindDeckFor(type, out deck, out cls)) return res;
                    foreach (var lo in Loadouts(deck, type))
                    {
                        int aam;
                        if (lo != null && CapMath.IsCapLoadout(lo, Ammo, out aam)) res.Add(lo.Name);
                    }
                }
                catch { }
                return res;
            }

            public double CapLoadoutReadyMin(string type, string name)
            {
                try
                {
                    int deck; string cls;
                    if (string.IsNullOrEmpty(type) || !FindDeckFor(type, out deck, out cls)) return -1;
                    var los = Loadouts(deck, type);
                    LoadoutOption pick = null;
                    if (string.IsNullOrEmpty(name) || string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        int i = CapMath.PickLoadout(los, Ammo, new CapParams(), null);
                        if (i >= 0) pick = los[i];
                    }
                    else foreach (var lo in los) if (lo != null && string.Equals(lo.Name, name, StringComparison.OrdinalIgnoreCase)) { pick = lo; break; }
                    return pick == null ? -1 : pick.Available ? 0 : pick.ReadyUpMin;
                }
                catch { return -1; }
            }

            public string CapLoadoutDisplay(string type, string name)
            {
                try
                {
                    int deck; string cls;
                    if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name) || !FindDeckFor(type, out deck, out cls)) return null;
                    foreach (var lo in Loadouts(deck, type))
                        if (lo != null && string.Equals(lo.Name, name, StringComparison.OrdinalIgnoreCase)) return lo.Display;
                }
                catch { }
                return null;
            }

            /// <summary>The first own deck whose hangar list has this vehicle type (uid, vehicle class "Plane"/"VTOL"/"Helicopter"...).</summary>
            private bool FindDeckFor(string type, out int deckUid, out string vehicleClass)
            {
                deckUid = 0; vehicleClass = null;
                try
                {
                    foreach (var ship in _a.DeckShipsTick())
                    {
                        var vs = Get(_a.DeckOf(ship), AirOpsSpecs.DeckVehicles) as IEnumerable;
                        if (vs == null) continue;
                        foreach (var v in vs)
                            if (Get(v, CapSpecs.VtFileName) as string == type)
                            {
                                deckUid = _a.UidOf(ship); vehicleClass = Str(Get(v, CapSpecs.VtType));
                                return true;
                            }
                    }
                }
                catch { }
                return false;
            }

            public string TypeAdmission(string type)
            {
                if (string.IsNullOrEmpty(type)) return "no_aam";
                string r;
                if (_admission.TryGetValue(type, out r)) return r;
                try
                {
                    int deck; string cls;
                    if (!FindDeckFor(type, out deck, out cls)) return "no_aam";   // no deck knows the type yet: not cached, asked again next time
                    _classOf[type] = cls ?? "";
                    r = CapMath.TypeAdmission(cls, Loadouts(deck, type), Ammo);
                    if (cls != null && cls.IndexOf("Helicopter", StringComparison.OrdinalIgnoreCase) >= 0) r = "helicopter";
                    _admission[type] = r;
                    return r;
                }
                catch { return "no_aam"; }
            }

            private void NoteHidden(string type, string reason)
            {
                if (type == null) return;
                string cls;
                if (!_classOf.TryGetValue(type, out cls)) cls = "?";
                if (_hiddenLogged.Add(type + "|" + reason)) Log("[Cap] hidden type=" + type + " reason=" + reason + " class=" + cls);
            }

            private bool Admitted(string type)
            {
                string r = TypeAdmission(type);
                if (r == null) return true;
                NoteHidden(type, r);
                return false;
            }

            // ---- birds ----

            public IList<CapBird> Birds()
            {
                var res = new List<CapBird>();
                try
                {
                    foreach (var u in _a.AirUnitsTick())
                    {
                        try { var b = Air(u); if (b != null) res.Add(b); }
                        catch (Exception e) { if (_p.Debug) Log("[Cap] air error: " + e.GetType().Name); }
                    }
                    foreach (var ship in _a.DeckShipsTick())
                    {
                        try { Deck(ship, res); }
                        catch (Exception e) { if (_p.Debug) Log("[Cap] deck error " + _a.NameOf(ship) + ": " + e.GetType().Name); }
                    }
                }
                catch { }
                return res;
            }

            /// <summary>T-175: air (aircraft, helicopter, missile) = 1, anything else = 2, the type cannot be read = -1 (ObjectBase._type of _objectToDestroy).</summary>
            private int TargetKindOf(object tgt)
            {
                try
                {
                    string t = Str(Get(tgt, OverviewSpecs.ObjType));
                    if (string.IsNullOrEmpty(t)) return -1;
                    return t == "Aircraft" || t == "Helicopter" || t == "Missile" ? 1 : 2;
                }
                catch { return -1; }
            }

            /// <summary>T-325: the unit's current ammunition (name -> count > 0); empty when the dictionary is not readable.</summary>
            private Dictionary<string, int> ReadWeapons(object unit)
            {
                var wp = new Dictionary<string, int>();
                var am = Get(unit, CoreSpecs.AmmoCounts) as IEnumerable;
                if (am != null)
                    foreach (var kv in am)
                    {
                        string k = Get(kv, CoreSpecs.PairKey) as string; var n = D(Get(kv, CoreSpecs.PairValue));
                        if (k != null && n.HasValue && n.Value > 0) wp[k] = (int)n.Value;
                    }
                return wp;
            }

            private static Dictionary<string, int> CopyWeapons(IDictionary<string, int> w) { return w == null ? null : new Dictionary<string, int>(w); }

            private double MaxAamNm(IDictionary<string, int> weapons)
            {
                double best = 0;
                if (weapons == null) return 0;
                foreach (var kv in weapons)
                {
                    var a = kv.Value > 0 ? Ammo(kv.Key) : null;
                    if (a != null && CapMath.IsAam(a)) best = Math.Max(best, a.MaxLaunchRangeNm);
                }
                return best;
            }

            private void CountAam(IDictionary<string, int> weapons, out int aam, out int lng)
            {
                aam = 0; lng = 0;
                if (weapons == null) return;
                foreach (var kv in weapons)
                {
                    var a = Ammo(kv.Key);
                    if (a == null || !CapMath.IsAam(a)) continue;
                    aam += kv.Value;
                    string cl = CapMath.AamClass(a.MaxLaunchRangeNm);
                    if (cl == "MR" || cl == "LR") lng += kv.Value;
                }
            }

            /// <summary>An own aircraft in flight. returning: the vehicle of a deck return / recovery task, listed although the game cleared
            /// _isInFlight (HoldingPattern assigns the recovery point and sets _isInFlight = false while the aircraft still flies the circuit).</summary>
            private CapBird Air(object u, bool returning = false)
            {
                if (_a.IsHelicopter(u) || (!returning && !_a.InFlight(u))) return null;   // helicopters are never listed; on a deck: the deck rows
                var obp = Get(u, AirOpsSpecs.ObjObp);
                string type = Get(obp, AirOpsSpecs.ObpIniName) as string ?? Get(u, CapSpecs.ObjClassName) as string;
                if (!Admitted(type)) return null;
                int uid = _a.UidOf(u);
                var b = new CapBird { Uid = uid, Type = type, Where = "to_zone", Loadout = Get(obp, CapSpecs.ObpLoadoutVariant) as string };
                b.Name = _a.NameOf(u); b.TypeLabel = Get(u, CapSpecs.ObjClassName) as string;
                var home = Get(u, CapSpecs.ObjHomeBase);
                if (home != null && IsAlive(home)) { b.DeckUid = _a.UidOf(home); b.ShipName = _a.NameOf(home); }
                // ammunition now (the unit's own dictionary) and at take-off (largest seen; the loadout of the home deck when it is known)
                var wp = ReadWeapons(u);
                b.Weapons = wp;   // T-325
                int aam, lng;
                CountAam(wp, out aam, out lng);
                b.AamNow = aam; b.LongNow = lng;
                b.AamMaxNm = MaxAamNm(wp);
                int sa = aam, sl = lng;
                if (b.DeckUid != 0 && b.Loadout != null)
                {
                    foreach (var lo in Loadouts(b.DeckUid, type))
                        if (lo.Name == b.Loadout && lo.Weapons != null)
                        {
                            int la, ll; CountAam(lo.Weapons, out la, out ll);
                            sa = Math.Max(sa, la); sl = Math.Max(sl, ll);
                            b.LoadWeapons = CopyWeapons(lo.Weapons); b.LoadoutLabel = lo.Display;   // T-325
                            break;
                        }
                }
                if (b.LoadWeapons == null)
                {
                    // T-325: no deck data for the variant: the largest count per weapon seen for this uid
                    Dictionary<string, int> seen;
                    if (!_loadSeen.TryGetValue(uid, out seen)) { seen = new Dictionary<string, int>(); _loadSeen[uid] = seen; }
                    foreach (var kv in wp) { int old; if (!seen.TryGetValue(kv.Key, out old) || kv.Value > old) seen[kv.Key] = kv.Value; }
                    b.LoadWeapons = CopyWeapons(seen);
                }
                int[] st;
                if (!_start.TryGetValue(uid, out st)) { st = new[] { 0, 0 }; _start[uid] = st; }
                st[0] = Math.Max(st[0], sa); st[1] = Math.Max(st[1], sl);
                b.AamStart = st[0]; b.LongStart = st[1];
                double lat, lon, h;
                if (_a.OwnPos(u, out lat, out lon, out h)) { b.Lat = lat; b.Lon = lon; }
                // fuel (0.27.8, CapMath.GameFuel): ReserveNm = path left before the return (Aircraft.ActualRangeInKm), MaxRangeNm = full tanks,
                // FuelFrac = RangeInKm / _maxRangeInKm. RangeOnMap (the old source) is the out-and-back radius, about half of that path.
                double reserve, maxNm, frac;
                var ringU = D(_a.RxGet(u, CapSpecs.ObjRangeOnMap));
                CapMath.GameFuel(D(_a.RxGet(u, CapSpecs.AircraftActualRangeKm)), ringU, MapUnitsPerKm,
                                 D(_a.RxGet(u, CapSpecs.ObjRangeKm)), D(Get(obp, CapSpecs.ObpMaxRangeKm)), out reserve, out maxNm, out frac);
                b.ReserveNm = reserve; b.MaxRangeNm = maxNm;
                // correction 2026-10-22 (ADR-0040 D5): the game's map range ring of the aircraft (RangeOnMapViewModel draws RangeOnMap; < 280000 = finite)
                b.RingNm = ringU.HasValue && !double.IsNaN(ringU.Value) && ringU.Value >= 0 && ringU.Value < 280000 ? ringU.Value / MapUnitsPerKm / 1.852 : -1;
                b.FuelFrac = frac >= 0 ? frac : FuelFraction(u);
                b.FuelFracLive = frac >= 0;   // T-315: live fraction of the game, not the frozen spawn value
                double kn = 0;
                try { kn = Math.Abs(D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0); } catch { }
                b.PatrolKn = kn > 150 ? kn : DefaultPatrolKn;
                b.CruiseKn = Math.Max(CruiseDefaultKn, kn);
                string state = _a.StateName(u, "CurrentStateName");
                b.AiState = state;   // T-317
                b.GameWinchester = state == "Winchester";
                b.GameBingo = state == "BingoFuel";
                b.Evading = state == "EvadeMissile" || state == "AdvancedMissileEvasion" || _a.SteeredByAirEvasion(uid);
                bool rtbOrder = OrderIs(u, "ReturnToBase");
                b.Returning = returning || rtbOrder;
                b.GameRtb = !_rtbByUs.Contains(uid) && rtbOrder;
                var tasks = Get(u, CapSpecs.ObjEngageTasks) as ICollection;
                b.Engaged = CapMath.EngagedNow(state, tasks != null ? tasks.Count : 0);   // T-319
                b.AiActive = CapTakeoverMath.AiCombatState(state) || (tasks != null && tasks.Count > 0);   // T-317
                var tgt = Get(Get(u, CapSpecs.ObjAi), CapSpecs.AiTarget);
                b.HasTarget = tgt != null && IsAlive(tgt);
                b.TargetKind = b.HasTarget ? TargetKindOf(tgt) : 0;
                b.TargetId = b.HasTarget ? _a.RrMenuTrack(tgt) : -1;   // T-333: the player's own track of the AI target (the game resolves it); -1 = none
                var ctl = Get(u, CapSpecs.ObjIsControllable); var acc = Get(u, CapSpecs.ObjAcceptsOrders);
                b.Controllable = !(ctl is bool && !(bool)ctl) && !(acc is bool && !(bool)acc);
                if (InGameCapZone(uid)) b.Where = "game_zone";
                else if (b.GameRtb || b.GameWinchester || b.GameBingo) b.Where = "leaving";
                return b;
            }

            private bool OrderIs(object u, string orderType)
            {
                try
                {
                    var o = _a.RxGet(u, CapSpecs.ObjCurrentOrder);
                    var t = o == null ? null : Get(o, CapSpecs.OrderType);
                    return t != null && t.ToString() == orderType;
                }
                catch { return false; }
            }

            /// <summary>Deck side: pending/cooling launch tasks, then the vehicles still in the hangar (one row per airframe, at most 16 per type).</summary>
            private void Deck(object ship, List<CapBird> res)
            {
                int shipUid = _a.UidOf(ship);
                string shipName = _a.NameOf(ship);
                var labels = new Dictionary<string, string>();   // file name of a type -> its display name (the pending tasks only know the file name)
                var vlist = Get(_a.DeckOf(ship), AirOpsSpecs.DeckVehicles) as IEnumerable;
                if (vlist != null)
                    foreach (var vt in vlist)
                    {
                        string fn = Get(vt, CapSpecs.VtFileName) as string, dn = Get(vt, CapSpecs.VtDisplayName) as string;
                        if (fn != null && !string.IsNullOrEmpty(dn)) labels[fn] = dn;
                    }
                // The pending task turns into a LaunchTask: the aircraft is spawned (final uid) and taxis out for minutes before it is in flight.
                // Air() skips it (not in flight yet), so list it here, otherwise a member that follows its task loses the machine (0.27.1 live).
                var tasksRaw = Get(_a.DeckOf(ship), AirOpsSpecs.DeckTasks) as IEnumerable;
                if (tasksRaw != null)
                    foreach (var lt in tasksRaw)
                    {
                        if (lt == null) continue;
                        string ltn = lt.GetType().Name;
                        if (ltn == "ReturnTask" || ltn == "RecoveryTask")
                        {
                            // 0.27.8 live: an aircraft in the landing circuit / approach has _isInFlight = false; Air() skipped it and a leaving CAP member
                            // was dropped while it still flew. List it from the deck task (same uid as in flight).
                            try
                            {
                                var rv = Get(lt, AirOpsSpecs.TaskVehicle);
                                if (rv == null || !IsAlive(rv) || _a.IsHelicopter(rv)) continue;
                                int ruid = _a.UidOf(rv);
                                bool rdup = false; foreach (var o in res) if (o.Uid == ruid) { rdup = true; break; }
                                if (rdup) continue;
                                var rb = Air(rv, true);
                                if (rb == null) continue;
                                rb.Recovering = ltn == "RecoveryTask";
                                if (rb.DeckUid == 0) { rb.DeckUid = shipUid; rb.ShipName = shipName; }
                                res.Add(rb);
                            }
                            catch (Exception) { }
                            continue;
                        }
                        if (ltn != "LaunchTask") continue;
                        try
                        {
                            var veh = Get(lt, AirOpsSpecs.TaskVehicle);
                            if (veh == null || !IsAlive(veh) || _a.IsHelicopter(veh)) continue;
                            int vuid = _a.UidOf(veh);
                            bool dup = false; foreach (var o in res) if (o.Uid == vuid) { dup = true; break; }
                            if (dup) continue;
                            var vobp = Get(veh, AirOpsSpecs.ObjObp);
                            string vtype = Get(vobp, AirOpsSpecs.ObpIniName) as string ?? Get(Get(lt, AirOpsSpecs.TaskVehicleType), AirOpsSpecs.VtFileName) as string;
                            if (vtype == null || !Admitted(vtype)) continue;
                            var lb = new CapBird { Uid = vuid, DeckUid = shipUid, Type = vtype, Where = "deck_launch", ShipName = shipName, Name = _a.NameOf(veh), Loadout = Get(vobp, CapSpecs.ObpLoadoutVariant) as string, PatrolKn = DefaultPatrolKn, CruiseKn = CruiseDefaultKn };
                            string llbl; if (labels.TryGetValue(vtype, out llbl)) lb.TypeLabel = llbl;
                            lb.Weapons = ReadWeapons(veh); lb.LoadWeapons = lb.Weapons;   // T-325: the variant's weapons when the deck lists it
                            if (lb.Loadout != null)
                                foreach (var lo3 in Loadouts(shipUid, vtype))
                                    if (lo3.Name == lb.Loadout && lo3.Weapons != null) { lb.LoadWeapons = CopyWeapons(lo3.Weapons); lb.LoadoutLabel = lo3.Display; break; }
                            double la, lo2, lh;
                            if (_a.OwnPos(veh, out la, out lo2, out lh)) { lb.Lat = la; lb.Lon = lo2; }
                            res.Add(lb);
                        }
                        catch (Exception) { }
                    }
                foreach (var t in _a.AirOps.DeckTasks(shipUid))
                {
                    if (t == null || t.Key == null || t.Type == null) continue;
                    bool cool = t.Key.StartsWith("cool:", StringComparison.Ordinal);
                    if (!cool && !t.Key.StartsWith("task:", StringComparison.Ordinal)) continue;
                    if (!Admitted(t.Type)) continue;
                    var b = new CapBird { Uid = AirOpsMath.TaskUid(t.Key), DeckUid = shipUid, Type = t.Type, Loadout = t.Loadout, PendingKey = cool ? null : t.Key, ReadyInS = t.RemainS, Ours = t.Ours, ShipName = shipName };
                    string lbl; if (labels.TryGetValue(t.Type, out lbl)) b.TypeLabel = lbl;
                    b.Where = cool ? "deck_cool" : (t.RemainS > 1 || t.State == "await_deck" || t.State == "await_crew" ? "deck_prep" : "deck_ready");
                    if (!cool && t.Loadout != null)
                        foreach (var lo in Loadouts(shipUid, t.Type))
                            if (lo.Name == t.Loadout) { int a, l; CountAam(lo.Weapons, out a, out l); b.AamNow = b.AamStart = a; b.LongNow = b.LongStart = l; b.Weapons = b.LoadWeapons = CopyWeapons(lo.Weapons); b.LoadoutLabel = lo.Display; break; }
                    double lat, lon, h;
                    if (_a.OwnPos(ship, out lat, out lon, out h)) { b.Lat = lat; b.Lon = lon; }
                    b.PatrolKn = DefaultPatrolKn; b.CruiseKn = CruiseDefaultKn;
                    res.Add(b);
                }
                var vs = Get(_a.DeckOf(ship), AirOpsSpecs.DeckVehicles) as IEnumerable;
                if (vs == null) return;
                // T-186 (ADR-0082 s.1): records of one type on a ship add up (Numbers of the pair); one set of slots per (ship, type)
                var typeSum = new Dictionary<string, int>();
                foreach (var v in vs)
                {
                    string tn = Get(v, CapSpecs.VtFileName) as string;
                    int c0 = (int)(D(Get(v, CapSpecs.VtNumbers)) ?? 0);
                    if (tn != null && c0 >= 1) { int acc; typeSum.TryGetValue(tn, out acc); typeSum[tn] = acc + c0; }
                }
                var typeDone = new HashSet<string>();
                foreach (var v in vs)
                {
                    string type = Get(v, CapSpecs.VtFileName) as string;
                    int count = (int)(D(Get(v, CapSpecs.VtNumbers)) ?? 0);
                    if (count < 1 || type == null || !Admitted(type) || !typeDone.Add(type)) continue;
                    count = typeSum[type];
                    // hangar row: the quickest CAP loadout gives the readiness; the load itself is chosen by the controller
                    double ready = double.MaxValue; int bestA = 0, bestL = 0;
                    foreach (var lo in Loadouts(shipUid, type))
                    {
                        int aam;
                        if (!CapMath.IsCapLoadout(lo, Ammo, out aam)) continue;
                        double r = lo.Available ? 0 : lo.ReadyUpMin * 60.0;
                        if (r < ready) { ready = r; int a, l; CountAam(lo.Weapons, out a, out l); bestA = a; bestL = l; }
                    }
                    bool known = ready != double.MaxValue;
                    if (!known) ready = 900;   // planning value only (ReadyKnown = false): the window shows no time for a guess
                    for (int k = 1; k <= count && k <= AirOpsMath.MaxSlots; k++)
                    {
                        var b = new CapBird { Uid = AirOpsMath.HangarSlotUid(shipUid, type, k), HangarK = k, HangarN = count, DeckUid = shipUid, Type = type, Where = "hangar", ShipName = shipName, TypeLabel = Get(v, CapSpecs.VtDisplayName) as string, ReadyInS = ready, ReadyKnown = known, AamNow = bestA, AamStart = bestA, LongNow = bestL, LongStart = bestL, PatrolKn = DefaultPatrolKn, CruiseKn = CruiseDefaultKn };
                        double lat, lon, h;
                        if (_a.OwnPos(ship, out lat, out lon, out h)) { b.Lat = lat; b.Lon = lon; }
                        res.Add(b);
                    }
                }
            }

            // ---- contacts: only what the player knows (the plotting table; hostile = the map's own relationship, unidentified is NOT hostile) ----

            public IList<CapContact> AirContacts() { return _a.AgAirContacts(); }   // T-175: the reading moved to the air threat service layer (same selection, plus course, speed and posture)

            public GeoPt? UnitPos(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    double lat, lon, h;
                    if (u != null && IsAlive(u) && _a.OwnPos(u, out lat, out lon, out h)) return new GeoPt(lat, lon);
                }
                catch { }
                return null;
            }

            // ---- deck (shared IAirOps) ----

            public string DeckTimingsMode() { try { return _a.AirOps.DeckTimingsMode(); } catch { return "Realistic"; } }
            public List<AirDeckTask> DeckTasks(int deckUid) { try { return _a.AirOps.DeckTasks(deckUid); } catch { return new List<AirDeckTask>(); } }
            public bool DeckDown(int deckUid) { try { return _a.AirOps.DeckInoperable(deckUid); } catch { return false; } }

            /// <summary>T-204: a RecoveryTask of this type is still on the deck (the landed unit is gone, its cool-down row is not listed yet).</summary>
            public bool Landing(int deckUid, string type)
            {
                if (type == null) return false;
                try
                {
                    var ship = _a.FindOwnUnit(deckUid);
                    var tasks = ship == null ? null : Get(_a.DeckOf(ship), AirOpsSpecs.DeckTasks) as IEnumerable;
                    if (tasks == null) return false;
                    foreach (var t in tasks)
                    {
                        if (t == null || t.GetType().Name != "RecoveryTask") continue;
                        if (Get(Get(t, AirOpsSpecs.TaskVehicleType), AirOpsSpecs.VtFileName) as string == type) return true;
                    }
                }
                catch (Exception) { }
                return false;
            }

            /// <summary>Birds() uid of the launch task: the deck rows of tasks are keyed by AirOpsMath.TaskUid(key) (see Deck()).</summary>
            public int TaskBirdUid(int deckUid, string key)
            {
                if (key == null) return 0;
                try { foreach (var t in _a.AirOps.DeckTasks(deckUid)) if (t != null && t.Key == key) return AirOpsMath.TaskUid(key); } catch { }
                return 0;
            }

            /// <summary>Fuel 0..1 from Ap._currentFuelMass / _maxFuel, -1 when the game does not give it.</summary>
            private double FuelFraction(object u)
            {
                try
                {
                    var ap = Get(u, CapSpecs.AircraftAp);
                    var cur = D(Get(ap, CapSpecs.ObpCurrentFuel)); var max = D(Get(ap, CapSpecs.ObpMaxFuel));
                    if (cur.HasValue && max.HasValue && max.Value > 0 && !double.IsNaN(cur.Value)) return Math.Max(0, Math.Min(1, cur.Value / max.Value));
                }
                catch { }
                return -1;
            }

            // ---- orders ----

            private static string AckError(string ack)
            {
                try
                {
                    var d = Json.Parse(ack) as Dictionary<string, object>;
                    if (d == null) return "bad_ack";
                    object ok;
                    if (d.TryGetValue("ok", out ok) && ok is bool && (bool)ok) return null;
                    object e;
                    return d.TryGetValue("error", out e) && e is string ? (string)e : "failed";
                }
                catch { return "bad_ack"; }
            }

            public string Patrol(int uid, IList<GeoPt> pts, int altFt, int keepStep = 0)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                if (pts == null || pts.Count == 0) return "bad_args";
                _a.WpqNotePlaced(uid, "cap_patrol");   // T-331: the last route setter, for the "waypoint reached" log line
                try
                {
                    double lat0, lon0, h0;
                    if (!_a.OwnPos(u, out lat0, out lon0, out h0)) return "no_position";
                    double h = h0;
                    if (altFt > 0) h = altFt / FeetPerUnitAlt;
                    else { var da = D(_a.RxGet(u, CapSpecs.ObjDesiredAltitude)); if (da.HasValue && da.Value > 0) h = da.Value; }
                    if (!_a.Present(AirOpsSpecs.ObjSetWaypoint)) return "unsupported";
                    _a.TacticsEnsureMission();
                    _a._ctlMem.RememberRoute(u, _a.Waypoints(u), _a.DemandedCourse(u));   // first mod route only: what "Return control" gives back
                    int? tgBefore = _a.Telegraph(u);
                    if (keepStep > 0) tgBefore = keepStep;   // T-257: the step the mod set a moment ago wins over a possibly stale read
                    _a.Call(u, AirOpsSpecs.ObjRemoveWaypoints);
                    object firstGeo = null;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var geo = _a.MakeGeo(pts[i].Lat, pts[i].Lon, h);
                        if (firstGeo == null) firstGeo = geo;
                        _a.CallOpt(u, AirOpsSpecs.ObjSetWaypoint, geo);   // the other parameters keep their defaults
                    }
                    try
                    {
                        if (firstGeo != null) _a.Set(u, AirOpsSpecs.ObjCommandPosition, firstGeo);
                    }
                    catch { }
                    int? tgAfter = _a.Telegraph(u);
                    if (tgBefore.HasValue && tgAfter.HasValue && tgBefore.Value != tgAfter.Value) { try { _a.Call(u, AirOpsSpecs.ObjSetTelegraph, tgBefore.Value); } catch { } }
                    var last = pts[pts.Count - 1];
                    _a._ctlMem.NoteRoutePlaced(u, last.Lat, last.Lon);
                    _rtbByUs.Remove(uid);
                    return null;
                }
                catch (Exception e) { return "waypoint_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            public int WaypointsLeft(int uid)
            {
                try { var u = Unit(uid); return u == null ? 0 : _a.Waypoints(u).Count; }
                catch { return 0; }
            }

            public string SetRoe(int uid, string roe, out string previous)
            {
                previous = null;
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                try
                {
                    string want = (roe ?? "").ToLowerInvariant();
                    if (want != "tight" && want != "hold" && want != "free") return "bad_roe";
                    previous = _a.WeaponStatusName(u);
                    _a._ctlMem.RememberRoe(u, previous);
                    if (previous != want)
                    {
                        var cmd = new Dictionary<string, object> { { "uid", (double)uid }, { "status", want } };
                        if (want == "free") cmd["confirm"] = true;
                        string ack = _a.RoeCmd("null", cmd);
                        if (ack == null || !ack.Contains("\"ok\":true")) return "roe_failed";
                    }
                    _a._ctlMem.NoteRoeSet(u, want);
                    return null;
                }
                catch (Exception e) { return "roe_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            public string Rtb(int uid)
            {
                if (Unit(uid) == null) return "unit_not_found";
                try
                {
                    string err = AckError(_a.ReturnToBaseCmd("0", new Dictionary<string, object> { { "uid", (double)uid } }));
                    if (err == null) _rtbByUs.Add(uid);
                    return err;
                }
                catch (Exception e) { return e.GetType().Name; }
            }

            /// <summary>T-175: drops the fire task on a surface target: the game's own CeaseFire(report: false) like BreakPursuit (checked live, G9); "unsupported" = no member.</summary>
            public string ClearSurfaceTarget(int uid)
            {
                if (!_a.Present(CapSpecs.ObjCeaseFire)) return "unsupported";
                return BreakPursuit(uid);
            }

            public string BreakPursuit(int uid)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                try
                {
                    // the game's own CeaseFire(report: false) clears _ai._objectToDestroy / the target list and the attack tasks (the ROE set stays); the controller then re-issues the route
                    _a.CallOpt(u, CapSpecs.ObjCeaseFire, false);
                    return null;
                }
                catch (Exception e) { return "cease_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            public string Restore(int uid)
            {
                var u = Unit(uid);
                _rtbByUs.Remove(uid);
                if (u == null) return "unit_not_found";
                RestoreBase(u, uid);   // T-323
                bool was = _a._capRestoring;
                _a._capRestoring = true;
                try { return AckError(_a.RestoreControlCmd("0", new Dictionary<string, object> { { "uid", (double)uid } })); }
                catch (Exception e) { return e.GetType().Name; }
                finally { _a._capRestoring = was; }
            }

            // T-323: ControlMemory.DecideRoute compares the FIRST current waypoint, the CAP base is the LAST one: rebase before the restore.
            private void RestoreBase(object u, int uid)
            {
                try
                {
                    var e = _a._ctlMem.Peek(u);
                    if (e == null || !e.HasRoute || e.PlacedByMod == null) return;
                    var wps = _a.Waypoints(u);
                    var p = CapTakeoverMath.RestoreBase(wps, e.PlacedByMod, DistanceNm);
                    if (p == null) return;
                    _a._ctlMem.NoteRoutePlaced(u, p[0], p[1]);
                    Log("[Cap] restore base uid=" + uid + " wps=" + wps.Count);
                }
                catch { }
            }

            // ---- ADR-0040 D2: speed steps (Aircraft.setTelegraph(n): n = 1..Ap._speedValuesInMach.Length; getTelegraph() = n - 1) ----
            private const double MachKn = 638.4;   // 1 Mach at 10 000 ft (AircraftLoader converts knots to Mach with the same sound speed)

            public int Throttle(int uid)
            {
                var u = Unit(uid);
                if (u == null) return -1;
                try { var t = D(_a.Call(u, CapSpecs.ObjGetTelegraph)); return t.HasValue ? (int)t.Value + 1 : -1; } catch { return -1; }
            }

            public string SetThrottle(int uid, int step)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                try { _a.Call(u, AirOpsSpecs.ObjSetTelegraph, step); return null; }
                catch (Exception e) { return "throttle_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            /// <summary>Steps ascending by speed; afterburner excluded. K = Ap._rangeMultiplierCurve at the step Mach (burn per mile; AircraftLoader: cruise range / range at the step).</summary>
            public IList<CapStep> ThrottleSteps(int uid)
            {
                var res = new List<CapStep>();
                var u = Unit(uid);
                if (u == null) return res;
                try
                {
                    var ap = Get(u, CapSpecs.AircraftAp);
                    var mach = Get(ap, CapSpecs.ApSpeedMach) as float[];
                    var names = Get(ap, CapSpecs.ApSpeedNames) as string[];
                    var curve = Get(ap, CapSpecs.ApRangeCurve);
                    if (mach == null) return res;
                    for (int i = 0; i < mach.Length; i++)
                    {
                        string nm = names != null && i < names.Length ? names[i] : null;
                        if ((nm != null && nm.IndexOf("afterburner", StringComparison.OrdinalIgnoreCase) >= 0) || (mach.Length > 4 && i == 4 && nm == null)) continue;
                        double k = 0;
                        try { if (curve != null) k = D(_a.Call(curve, CapSpecs.CurveEvaluate, mach[i])) ?? 0; } catch { }
                        res.Add(new CapStep { Step = i + 1, Kn = mach[i] * MachKn, K = k, Name = nm });
                    }
                    res.Sort((x, y) => x.Kn.CompareTo(y.Kn));
                }
                catch { res.Clear(); }
                return res;
            }

            private TakeProbe ProbeOf(object u, string st)
            {
                var p = new TakeProbe { State = st };
                try
                {
                    var al = Get(u, CapSpecs.ObjAlignmentTasks) as ICollection;
                    p.Aligning = al != null && al.Count > 0;
                    var tasks = Get(u, CapSpecs.ObjEngageTasks) as ICollection;
                    p.EngageTasks = tasks == null ? 0 : tasks.Count;
                }
                catch { }
                return p;
            }

            public bool TakenByPlayer(int uid)
            {
                var u = Unit(uid);
                if (u == null) return false;
                try
                {
                    var e = _a._ctlMem.Peek(u);
                    if (e == null) return false;
                    // 0.27.8 live: the deck's return route (ReturnToBase state) sets weapons to Hold and replaces the waypoints; on our RTB and on the
                    // game's own RTB / bingo / winchester that is not the player
                    if (_rtbByUs.Contains(uid) || OrderIs(u, "ReturnToBase")) return false;
                    string st = _a.StateName(u, "CurrentStateName");
                    if (st == "ReturnToBase" || st == "BingoFuel" || st == "Winchester") return false;
                    // T-317 (ADR-0124): what differs from the mod's own values, then who did it (the game AI in its air combat or the player)
                    var p = ProbeOf(u, st);
                    if (e.HasRoe && e.RoeSetByMod != null) { p.RoeSet = e.RoeSetByMod; p.RoeNow = _a.WeaponStatusName(u); }
                    List<double[]> wps = null;
                    if (e.HasRoute && e.PlacedByMod != null)
                    {
                        wps = _a.Waypoints(u);
                        p.Wps = wps.Count;
                        if (wps.Count > 0) { var last = wps[wps.Count - 1]; p.DevNm = WorldDistanceNm(last[0], last[1], e.PlacedByMod[0], e.PlacedByMod[1]); }
                    }
                    double now = Now;
                    if (CapTakeoverMath.Deviation(p) != null) _a.TakeInheritLeader(u, uid);
                    string why;
                    var v = _a.TakeTracker.Check(uid, p, now, out why);
                    if (v == TakeVerdict.None) return false;
                    bool win = _a.TakeTracker.InWindow(uid, now), sel = _a.TakeTracker.SelectedInWindow(uid, now);
                    string detail = "why=" + why + " " + CapTakeoverMath.Detail(p) + " sel=" + (sel ? 1 : 0) + " win=" + (win ? 1 : 0);
                    _takeDetail[uid] = detail;
                    if (v == TakeVerdict.AiDrift)
                    {
                        // the game moved the route or the ROE: accept the current value as the new base, keep the member
                        if (why == "roe") _a._ctlMem.NoteRoeSet(u, p.RoeNow);
                        else if (why == "route" && wps != null && wps.Count > 0) { var l = wps[wps.Count - 1]; _a._ctlMem.NoteRoutePlaced(u, l[0], l[1]); }
                        _takeDrift.Add(uid);
                        if (!_a.TakeTracker.DriftLogged(uid))
                        {
                            _a.TakeTracker.NoteDriftLogged(uid);
                            Log("[Cap] ai drift uid=" + uid + " " + detail + " since_ai_s=" + _a.TakeTracker.SinceAiS(uid, now).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        return false;
                    }
                    return true;
                }
                catch { }
                return false;
            }

            public string TakenDetail(int uid) { string d; return _takeDetail.TryGetValue(uid, out d) ? d : null; }
            public bool TakeAiDrift(int uid) { return _takeDrift.Remove(uid); }

            /// <summary>T-317: DashTick - the AI window is open (the state of the aircraft now counts) and the player did not select the aircraft.</summary>
            public bool AiWindow(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return false;
                    double now = Now;
                    _a.TakeTracker.Touch(uid, ProbeOf(u, _a.StateName(u, "CurrentStateName")), now);
                    _a.TakeInheritLeader(u, uid);
                    return _a.TakeTracker.InWindow(uid, now) && !_a.TakeTracker.SelectedInWindow(uid, now);
                }
                catch { return false; }
            }

            // T-333: Vehicle cache of the tracks of the intercept targets (the shared _rrVehicles is cleared on every scan miss, then a killed target would read "lost")
            private readonly Dictionary<int, object> _icptVeh = new Dictionary<int, object>();

            public void IcptSeed(int trackId)
            {
                try { object v = _a.RrVehicle(trackId); if (v != null) _icptVeh[trackId] = v; } catch { }
            }

            public void IcptDrop(int trackId) { _icptVeh.Remove(trackId); }

            public string TrackGone(int trackId)
            {
                try
                {
                    object v;
                    if (!_icptVeh.TryGetValue(trackId, out v) || v == null) return null;
                    var o = Get(v, OverviewSpecs.VehObject);   // leak-ok: only "is it still there"
                    if (o == null || !IsAlive(o) || BoolOf(Get(o, ContactInfoSpecs.ObjIsDestroyed))) return "destroyed";
                    return "lost";
                }
                catch { return null; }
            }

            public bool UnitAlive(int uid)
            {
                try { var u = Unit(uid); return u != null && IsAlive(u); } catch { return false; }
            }

            public bool InGameCapZone(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return false;
                    var z = Get(u, AirOpsSpecs.ObjPatrolZone);
                    if (z == null) return false;
                    var zt = Get(z, AirOpsSpecs.PzZoneType);
                    return zt != null && zt.ToString() == "CAP";
                }
                catch { return false; }
            }

            public void Event(string name, string dataJson)
            {
                try { _a.EvLine(name, dataJson); } catch { }
            }

            public void Log(string line)
            {
                try { var s = _a.AswLogSink; if (s != null) s(line); else UnityEngine.Debug.Log(line); } catch { }
            }
        }
    }
}
