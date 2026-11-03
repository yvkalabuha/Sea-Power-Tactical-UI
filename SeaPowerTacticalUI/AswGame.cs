using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    // T-095 / ADR-0036: the real IAswGame. Everything here is main-thread, by reflection through GameApi helpers.
    // Not exercised offline (needs the game): see the live checks in the T-095 plan, section 7.
    internal sealed partial class GameApi
    {
        internal AswController CreateAswController(AswParams p, AswConfig cfg)
        {
            _aswImpl = new AswGameImpl(this, cfg);
            var c = new AswController(_aswImpl, p, cfg);
            Asw = c;
            SarAswGroupOf = c.GroupNameOf;   // the SAR controller asks which ASW/SAR group a unit is in (busy:asw_group)
            if (Sar != null) c.Sar = Sar;
            c.RaftScanOf = () => _sarImpl == null ? null : _sarImpl.LastScan;   // 0.27.10: raw raft counts for the window and the dev state
            c.DelegateDeckTask = (deck, key) => { var ops = AirOps as AirOpsGameImpl; return ops != null && ops.Delegate(deck, key); };   // live finding 38: re-arm to the SAR loadout
            AswRangeParams = p;
            WireAirRadar();   // T-241
            return c;
        }

        /// <summary>BepInEx/config/SeaPowerTacticalUI/asw, set by the plugin (BepInEx is not referenced here so the selfcheck can compile this file).</summary>
        internal string AswSaveDir;

        private AswGameImpl _aswImpl;

        /// <summary>Where the ASW log goes: the BepInEx logger of the UI plugin (set by the plugin). Null = UnityEngine.Debug.</summary>
        internal Action<string> AswLogSink;

        /// <summary>The ASW window for the XO menu item: open it, preselect the group aimed at a ship (set by the asw module). Null = no window.</summary>
        internal Ui.IAswWindow AswWindow;

        /// <summary>Air tick helpers (T-119): a deck ship name for the diag, a plain-text event. The mission identity moved to MissionGame.cs (T-130).</summary>
        internal string AirDeckName(int uid)
        {
            try { var u = FindOwnUnit(uid); return u == null ? null : NameOf(u); }
            catch { return null; }
        }

        internal void AirEvent(string name, string detail)
        {
            try { if (EventsAvailable) EvLine(name, "{\"detail\":" + Json.Str(detail ?? "") + "}"); } catch { }
        }

        /// <summary>One step (1 s of game time) of the ASW/SAR-helicopter controller, called by the air tick between BeginTick and EndTick of the deck broker.</summary>
        internal void AirTickAsw(double t)
        {
            var c = Asw;
            if (c == null || _aswImpl == null || !Frame.InMission) return;
            c.Tick(t);
        }

        /// <summary>The ASW window owner of the shared mouse flag (UiMouseBlock, see SetMouseBlock).</summary>
        internal void SetAswBlock(bool on) { SetMouseBlock("asw", on); }

        private sealed partial class AswGameImpl : IAswGame
        {
            private readonly GameApi _a;
            private readonly AswConfig _cfg;
            private readonly Dictionary<int, object> _deckShip = new Dictionary<int, object>();
            private readonly Dictionary<int, List<string>> _lastTasks = new Dictionary<int, List<string>>();
            private int _seq;

            public AswGameImpl(GameApi a, AswConfig cfg) { _a = a; _cfg = cfg; }

            private object Get(object o, MemberSpec s) { return _a.Get(o, s); }
            private object RxGet(object o, MemberSpec s) { return _a.RxGet(o, s); }
            private object GetStatic(MemberSpec s) { return _a.GetStatic(s); }
            private object CallOpt(object o, MemberSpec s, params object[] args) { return _a.CallOpt(o, s, args); }

            public double Now { get { return _a.Frame.MissionTimeS ?? 0; } }

            public double TimeScale
            {
                get
                {
                    try { return D(GetStatic(CoreSpecs.TimeCompression)) ?? 1; } catch { return 1; }
                }
            }

            public bool AllowAttack { get { return _a.AllowAttack; } }

            public string LastSavePath
            {
                get
                {
                    return ReadLastSavePath();
                }
            }

            public string SaveDir
            {
                get
                {
                    return _a.AswSaveDir;
                }
            }

            // ---- aircraft -------------------------------------------------------------------------------------------

            private bool SarActive(object u)
            {
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("AutomaticSAR.RescueCoordinator", false);
                        if (t == null) continue;
                        var m = t.GetMethod("IsActive", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (m == null || m.GetParameters().Length != 1) return false;
                        var r = m.Invoke(null, new[] { u });
                        return r is bool && (bool)r;
                    }
                }
                catch { }
                return false;
            }

            private bool _extSarNoted;

            public IList<AswMember> Members()
            {
                var res = new List<AswMember>();
                _deckShip.Clear();
                if (!_extSarNoted)
                {
                    _extSarNoted = true;
                    if (_a.ExtSarPresent()) Log("[Sar] AutomaticSAR found: units it works with are left alone (reason ext_sar); do not run both on the same units");
                }
                foreach (var u in _a.AirUnitsTick())
                {
                    try { var m = Air(u); if (m != null) res.Add(m); }
                    catch (Exception e) { Skip(u, "error:" + e.GetType().Name); }
                }
                foreach (var ship in _a.DeckShipsTick())
                {
                    try { Deck(ship, res); }
                    catch (Exception e) { Log("asw deck error " + _a.NameOf(ship) + ": " + e.GetType().Name + ": " + e.Message); }
                }
                foreach (var m in res)
                    if (m.Where != null && m.Where.StartsWith("deck", StringComparison.Ordinal)) { try { DeckSeats(m); } catch { } }
                return res;
            }

            // ---- T-109 (SAR) -----------------------------------------------------------------------------------------

            private readonly Dictionary<string, KeyValuePair<double, List<LoadoutOption>>> _losCache = new Dictionary<string, KeyValuePair<double, List<LoadoutOption>>>();

            /// <summary>Loadouts of a vehicle type on a deck with their seats; cached for 5 s (reflection).</summary>
            private List<LoadoutOption> CachedLoadouts(int deckUid, string type)
            {
                string k = deckUid + "|" + type;
                KeyValuePair<double, List<LoadoutOption>> c;
                double now = Now;
                if (_losCache.TryGetValue(k, out c) && now - c.Key < 5 && now >= c.Key) return c.Value;
                var l = _a.AirOps.Loadouts(deckUid, type);
                _losCache[k] = new KeyValuePair<double, List<LoadoutOption>>(now, l);
                return l;
            }

            private void DeckSeats(AswMember m)
            {
                if (m.Type == null || m.DeckUid == 0 || !m.Helicopter) return;
                var los = CachedLoadouts(m.DeckUid, m.Type);
                int best = 0, own = -1;
                foreach (var l in los) { best = Math.Max(best, l.Capacity); if (l.Name == m.Loadout) own = l.Capacity; }
                m.BestCapacity = best; m.Capacity = own >= 0 ? own : best;
            }

            /// <summary>People on board, seats, class and the game's own SAR patrol zone of an airborne unit.</summary>
            private void AirSar(AswMember m, object u)
            {
                try
                {
                    m.Helicopter = _a.IsHelicopter(u);
                    var obp = Get(u, AirOpsSpecs.ObjObp);
                    m.Onboard = (int)(D(RxGet(obp, AirOpsSpecs.ObpTransported)) ?? 0);
                    m.Capacity = (int)(D(Get(obp, AirOpsSpecs.ObpTransportCapacity)) ?? 0);
                    m.BestCapacity = m.Capacity;
                    var z = Get(u, AirOpsSpecs.ObjPatrolZone);
                    var zt = z == null ? null : Get(z, AirOpsSpecs.PzZoneType);
                    m.GameSarZone = zt != null && zt.ToString() == "SAR";
                }
                catch { }
            }

            public List<LoadoutOption> SarLoadouts(int deckUid, string vehicleType) { return CachedLoadouts(deckUid, vehicleType); }
            public string DeckTimingsMode() { return _a.AirOps.DeckTimingsMode(); }
            public string DeckState(int deckUid)
            {
                try { return "inoperable=" + _a.AirOps.DeckInoperable(deckUid) + " deck_tasks=" + _a.AirOps.DeckDump(deckUid); }
                catch (Exception e) { return "deck_state_error=" + e.GetType().Name; }
            }

            public IList<SarRaft> SarRafts() { return _a._sarImpl == null ? new List<SarRaft>() : _a._sarImpl.Rafts(); }
            public IList<SarThreat> SarThreats() { return _a._sarImpl == null ? new List<SarThreat>() : _a._sarImpl.Threats(); }
            public string SarRtb(int uid) { return _a._sarImpl == null ? "no_sar" : _a._sarImpl.Rtb(uid); }
            public string SarHover(int uid) { return _a._sarImpl == null ? "no_sar" : _a._sarImpl.Hover(uid); }

            public string SarHoverOver(int uid, int shipUid)
            {
                if (_a._sarImpl == null) return "no_sar";
                var t = Target(shipUid);
                return t == null ? "unit_not_found" : _a._sarImpl.HoverOver(uid, new GeoPt(t.Lat, t.Lon));
            }

            private readonly Dictionary<int, string> _skipLogged = new Dictionary<int, string>();

            /// <summary>Why an aircraft is not in the list (once per aircraft and reason, only with [Asw] Debug).</summary>
            private void Skip(object u, string reason)
            {
                int uid;
                try { uid = _a.UidOf(u); } catch { uid = 0; }
                string old;
                if (_skipLogged.TryGetValue(uid, out old) && old == reason) return;
                _skipLogged[uid] = reason;
                Log("asw air skip uid=" + uid + " reason=" + reason);
            }

            private AswMember Air(object u)
            {
                if (!_a.InFlight(u)) { Skip(u, "not_in_flight"); return null; }   // on a deck: reported by the deck rows
                var m = new AswMember { Uid = _a.UidOf(u), Name = _a.NameOf(u), Type = (Get(Get(u, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpIniName) as string) ?? (Get(u, AirOpsSpecs.ObjClassName) as string), Where = "air", LaunchS = 120 };
                { string dl; _typeLabels.TryGetValue(m.Type ?? "", out dl); m.TypeLabel = AswMath.TypeLabelOf(dl, Get(u, AirOpsSpecs.ObjClassName) as string, m.Name); }   // T-192: the type, never the callsign
                string state = _a.StateName(u, "CurrentStateName");
                if (SarActive(u)) m.Where = "sar";
                else if (state == "MaritimePatrol") m.Where = "game_patrol";
                m.Evading = state == "EvadeMissile" || state == "AdvancedMissileEvasion" || _a.SteeredByAirEvasion(m.Uid);   // T-100: a machine in an evasion turn is not led and not "taken by the player"
                AirSar(m, u);
                var home = Get(u, AirOpsSpecs.ObjHomeBase);
                if (home != null && GameApi.IsAlive(home)) { m.Base = _a.NameOf(home); try { m.ShipUid = _a.UidOf(home); } catch { m.ShipUid = 0; } }   // T-155: the "Ship" link of the window
                try { m.Loadout = Get(Get(u, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpLoadoutVariant) as string; } catch { }
                try { var sn = Get(Get(u, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpSerial); m.Serial = sn == null ? null : sn.ToString(); } catch { }
                var asw = Json.Parse(_a.AswJson(u)) as Dictionary<string, object>;
                if (asw != null)
                {
                    m.Passive = (int)(D(asw["passive"]) ?? 0); m.ActiveBuoys = (int)(D(asw["active"]) ?? 0); m.Vlad = (int)(D(asw["vlad"]) ?? 0);
                    m.Torpedoes = (int)(D(asw["torpedoes"]) ?? 0);
                    var byAmmo = asw.ContainsKey("buoys") ? asw["buoys"] as Dictionary<string, object> : null;   // T-172 B4: the count per buoy type
                    if (byAmmo != null)
                    {
                        m.BuoysByAmmo = new Dictionary<string, int>();
                        foreach (var kv in byAmmo) m.BuoysByAmmo[kv.Key] = (int)(D(kv.Value) ?? 0);
                    }
                    m.Dipping = asw["dipping"] is bool && (bool)asw["dipping"]; m.Mad = asw["mad"] is bool && (bool)asw["mad"];
                    m.FuelFrac = D(asw["fuel_frac"]) ?? 1;
                    m.EnduranceS = AswMath.FullEnduranceS(D(asw["endurance_s"]), m.FuelFrac);
                    m.AiState = asw["ai_state"] as string;
                    m.DipSrc = "live"; m.DipNote = asw.ContainsKey("dip_note") ? asw["dip_note"] as string : null;   // T-188
                }
                double lat, lon, h;
                if (_a.OwnPos(u, out lat, out lon, out h)) { m.Lat = lat; m.Lon = lon; }
                // transit back to the base at the aircraft speed (fallback 100 kn)
                if (home != null && m.Lat != 0)
                {
                    double hl, ho, hh;
                    if (_a.OwnPos(home, out hl, out ho, out hh))
                    {
                        double spd = 100;
                        try { var v = D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)); if (v.HasValue && v.Value > 30) spd = v.Value; } catch { }
                        m.TransitS = WorldDistanceNm(m.Lat, m.Lon, hl, ho) / spd * 3600.0;
                    }
                }
                return m;
            }

            private string[] RolesOf(object vt)
            {
                var res = new List<string>();
                var roles = Get(vt, AirOpsSpecs.VtUnitRoles) as IEnumerable;
                if (roles != null) foreach (var r in roles) if (r != null) res.Add(r.ToString());
                return res.ToArray();
            }

            private Dictionary<string, int> AmmoOfLoadout(object lo)
            {
                var res = new Dictionary<string, int>();
                var am = Get(lo, AirOpsSpecs.LoAmmoAmounts) as IEnumerable;
                if (am == null) return res;
                foreach (var kv in am)
                {
                    string k = Get(kv, CoreSpecs.PairKey) as string; var n = D(Get(kv, CoreSpecs.PairValue));
                    if (k != null && n.HasValue) res[k] = (int)n.Value;
                }
                return res;
            }

            private object NthLoadout(object vt, int idx)
            {
                var l = Get(vt, AirOpsSpecs.VtLoadoutsField) as IList ?? Get(vt, AirOpsSpecs.VtLoadouts) as IList;
                return l != null && idx >= 0 && idx < l.Count ? l[idx] : null;
            }

            /// <summary>Wanted loadout first, else a loadout whose name says ASW, else the first one.</summary>
            private object PickLoadout(object vt, string[] wish, out bool wanted)
            {
                wanted = false;
                var lo = Get(vt, AirOpsSpecs.VtLoadouts) as IEnumerable ?? Get(vt, AirOpsSpecs.VtLoadoutsField) as IEnumerable;
                object asw = null, first = null;
                if (lo == null) return null;
                foreach (var l in lo)
                {
                    if (first == null) first = l;
                    string ln = Get(l, AirOpsSpecs.LoName) as string ?? "";
                    if (Array.IndexOf(wish, ln) >= 0) { wanted = true; return l; }
                    if (asw == null && ln.IndexOf("ASW", StringComparison.OrdinalIgnoreCase) >= 0) asw = l;
                }
                return asw ?? first;
            }

            private AswMember Entry(AswMath.DeckEntry d) { if (d.Type != null && !string.IsNullOrEmpty(d.Label)) _typeLabels[d.Type] = d.Label; d.LoadoutAsw = AswMath.LoadoutAswOf(d.Loadout, d.Ammo, _cfg.Loadouts); return AswMath.DeckToMember(d); }   // T-192: the deck label of the type for the aircraft in the air
            private readonly Dictionary<string, string> _typeLabels = new Dictionary<string, string>();

            /// <summary>
            /// Deck aircraft as the Flight Operations window shows them: FlightDeck.FlightDeckTasks (PendingLaunchTask = being prepared / ready,
            /// LaunchTask = taxiing out, RecoveryTask = landing) plus the vehicles still in the hangar (_vehiclesOnBoard, Numbers >= 1; a vehicle in
            /// preparation is NOT counted there any more). Key of a pending task is its Guid (stable synthetic uid); the load comes from the loadout
            /// the task was started with. Verified only live (plan section 9 item 6): ReadyInS from _startTime/_duration is clamped to _duration.
            /// </summary>
            private void Deck(object ship, List<AswMember> res)
            {
                var deck = _a.DeckOf(ship);
                if (deck == null) return;
                int shipUid = _a.UidOf(ship);
                string shipName = _a.NameOf(ship);
                var wish = (_cfg.Loadouts ?? "").Split(',');
                for (int i = 0; i < wish.Length; i++) wish[i] = wish[i].Trim();
                int n = 0;
                var phases = new Dictionary<string, string>();   // T-181 §4: task key -> game state (ready_up|await_deck|...), for the "deck is full" rule
                try { foreach (var dt in _a.AirOps.DeckTasks(shipUid)) if (dt.Key != null) phases[dt.Key] = dt.State; } catch { }
                var tasks = Get(deck, AswSpecs.DeckTasks) as IEnumerable;
                if (tasks != null)
                    foreach (var t in tasks)
                    {
                        if (t == null) continue;
                        string tn = t.GetType().Name;
                        if (tn == "CoolDownTask")
                        {
                            // T-150 P6: a landed machine stays here until the cool-down ends and the hangar count goes up; it has no VehicleType
                            var cd = CoolRow(deck, t, shipUid, shipName, wish);
                            if (cd != null) { string cp; if (phases.TryGetValue(cd.Key, out cp)) cd.DeckPhase = cp; res.Add(Entry(cd)); n++; }
                            continue;
                        }
                        var vt = Get(t, AswSpecs.TaskVehicleType);
                        if (vt == null) continue;
                        if (!Interesting(vt, wish)) continue;
                        var d = new AswMath.DeckEntry { ShipUid = shipUid, ShipName = shipName, Type = Get(vt, AirOpsSpecs.VtFileName) as string, Label = Get(vt, AirOpsSpecs.VtDisplayName) as string, Squadron = Get(t, AswSpecs.TaskSquadron) as string, Roles = RolesOf(vt) };
                        d.Helicopter = Get(vt, AirOpsSpecs.VtType) != null && Get(vt, AirOpsSpecs.VtType).ToString() == "Helicopter";
                        d.AswCapableType = AswCapable(vt, wish);
                        if (tn != "PendingLaunchTask") FillDip(d, d.Type, null);
                        if (tn == "PendingLaunchTask")
                        {
                            var idx = D(Get(t, AswSpecs.PendingLoadoutIndex));
                            var lo = NthLoadout(vt, idx.HasValue ? (int)idx.Value : -1);
                            d.Loadout = lo == null ? null : Get(lo, AirOpsSpecs.LoName) as string;
                            d.Ammo = lo == null ? null : AmmoOfLoadout(lo);
                            FillDip(d, d.Type, d.Loadout);
                            var cnt = D(Get(t, AswSpecs.PendingLaunchCount)); d.Count = cnt.HasValue && cnt.Value > 0 ? (int)cnt.Value : 1;
                            var dur = D(Get(t, AswSpecs.TaskDuration)); var crew = D(Get(t, AswSpecs.PendingCrew));
                            d.ReadyInS = AirOpsMath.PendingRemainS(dur ?? 0, _a.AirOps.DeckTimingsMode(), crew.HasValue ? (int)crew.Value : 0);   // _startTime is never set on a live task (0.8.7)
                            d.State = d.ReadyInS > 1 ? "prep" : "ready";
                            var g = Get(t, AswSpecs.TaskUid);
                            d.Key = "task:" + g;
                            d.Uid = AswMath.TaskUid(d.Key);   // T-181: a pure function of the key, the controller predicts it at hand-over
                            string tp; if (phases.TryGetValue(d.Key, out tp)) d.DeckPhase = tp;
                            RememberLtp(Get(t, AswSpecs.TaskLtp), d.Key);
                        }
                        else if (tn == "LaunchTask" || tn == "RecoveryTask")
                        {
                            var veh = Get(t, AswSpecs.TaskVehicle);
                            if (veh == null || !GameApi.IsAlive(veh)) continue;
                            d.State = tn == "LaunchTask" ? "launching" : "recovering";
                            d.Uid = _a.UidOf(veh); d.Key = "unit:" + d.Uid;
                            if (tn == "LaunchTask") d.FromKey = LtpKey(Get(t, AswSpecs.TaskLtp));
                            d.UnitName = _a.NameOf(veh);   // T-192: the callsign goes to Name, Label stays the type
                            var lv = Get(Get(veh, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpLoadoutVariant) as string; d.Loadout = lv;
                            try { var sn = Get(Get(veh, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpSerial); d.Serial = sn == null ? null : sn.ToString(); } catch { }
                        }
                        else continue;
                        res.Add(Entry(d)); n++;
                    }
                var vs = Get(deck, AswSpecs.DeckVehicles) as IEnumerable;
                string timingMode = null;
                try { timingMode = _a.AirOps.DeckTimingsMode(); } catch { }
                if (vs != null)
                {
                    int idx = 0;
                    // T-186 (ADR-0082 s.1): records of one type on a ship add up (Numbers of the pair); one set of slots per (ship, type)
                    var typeSum = new Dictionary<string, double>();
                    foreach (var v in vs)
                    {
                        string tn = Get(v, AirOpsSpecs.VtFileName) as string ?? "";
                        double c0 = D(Get(v, AirOpsSpecs.VtNumbers)) ?? 0;
                        if (c0 >= 1) { double acc; typeSum.TryGetValue(tn, out acc); typeSum[tn] = acc + c0; }
                    }
                    var typeDone = new HashSet<string>();
                    foreach (var v in vs)
                    {
                        idx++;
                        double cnt = D(Get(v, AirOpsSpecs.VtNumbers)) ?? 0;
                        if (cnt < 1 || !Interesting(v, wish)) continue;
                        string tkey = Get(v, AirOpsSpecs.VtFileName) as string ?? "";
                        if (!typeDone.Add(tkey)) continue;
                        cnt = typeSum[tkey];
                        bool wanted;
                        var lo = PickLoadout(v, wish, out wanted);
                        string lname = lo == null ? null : Get(lo, AirOpsSpecs.LoName) as string;
                        bool ready = false;
                        if (lo != null) try { ready = (bool)_a.CallOpt(deck, AirOpsSpecs.DeckCanLaunch, lo, 1); } catch { }
                        double rd = lo == null ? 0 : (D(Get(lo, AirOpsSpecs.LoReadyUp)) ?? 0);
                        string vtype = Get(v, AirOpsSpecs.VtFileName) as string;
                        bool heli = Get(v, AirOpsSpecs.VtType) != null && Get(v, AirOpsSpecs.VtType).ToString() == "Helicopter";
                        var vroles = RolesOf(v);
                        bool vasw = AswCapable(v, wish);
                        var vammo = lo == null ? null : AmmoOfLoadout(lo);
                        // T-181 (ADR-0080 §1): one row per machine in the hangar, k = 1..min(Numbers, MaxSlots); all rows of a type share the state
                        var dipInfo = _a.TypeDipSonar(vtype, lname);   // T-188: once per type, not per slot
                        int slots = Math.Min((int)cnt, AswMath.MaxSlots);
                        for (int k = 1; k <= slots; k++)
                        {
                            var d = new AswMath.DeckEntry
                            {
                                ShipUid = shipUid, ShipName = shipName, Type = vtype, Label = Get(v, AirOpsSpecs.VtDisplayName) as string, Loadout = lname,
                                Roles = vroles, Count = (int)cnt, Slot = k, State = ready && wanted ? "ready" : "hangar", ReadyInS = ready ? 0 : (rd > 0 ? rd : 900),
                                Uid = AswMath.HangarSlotUid(shipUid, vtype, k), Key = AswMath.SlotKey(vtype, k), Ammo = vammo,
                                HangarPrepS = AirOpsMath.HangarPrepS(rd, timingMode)   // T-192: an order from the hangar needs the preparation, whatever CanLaunch says
                            };
                            d.Helicopter = heli;
                            d.AswCapableType = vasw;
                            d.TypeDipping = dipInfo == null ? (bool?)null : dipInfo.Loaded; d.DipNote = AswDipTasks.DipNote(dipInfo);
                            res.Add(Entry(d)); n++;
                        }
                    }
                }
                _deckCount[shipUid] = n;
            }

            /// <summary>T-188: the dipping sonar of the type from its ini (null = unreadable: the old guess by the load stays).</summary>
            private void FillDip(AswMath.DeckEntry d, string vehicleFile, string loadout)
            {
                var info = _a.TypeDipSonar(vehicleFile, loadout);
                d.TypeDipping = info == null ? (bool?)null : info.Loaded;
                d.DipNote = AswDipTasks.DipNote(info);
            }

            private readonly Dictionary<int, int> _deckCount = new Dictionary<int, int>();

            // 0.8.7: the game spawns the machine of a ready PendingLaunchTask and lists a LaunchTask (taxiing, minutes) that shares its LaunchTaskParameters
            // object. Pending task key by that object (reference identity): the "unit:" row of the LaunchTask names the task it came from (FromKey).
            private sealed class RefEq : IEqualityComparer<object>
            {
                public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
                public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
            }
            private readonly Dictionary<object, KeyValuePair<string, double>> _ltpKeys = new Dictionary<object, KeyValuePair<string, double>>(new RefEq());

            private void RememberLtp(object ltp, string key)
            {
                if (ltp == null || key == null) return;
                double now = Now;
                _ltpKeys[ltp] = new KeyValuePair<string, double>(key, now);
                if (_ltpKeys.Count <= 64) return;
                foreach (var k in new List<object>(_ltpKeys.Keys)) if (now - _ltpKeys[k].Value > 3600 || now < _ltpKeys[k].Value) _ltpKeys.Remove(k);
            }

            private string LtpKey(object ltp)
            {
                KeyValuePair<string, double> v;
                return ltp != null && _ltpKeys.TryGetValue(ltp, out v) ? v.Key : null;
            }

            /// <summary>One CoolDownTask as a deck row (state "cooling", the serial of the landed machine); null when its type is not listed.</summary>
            private AswMath.DeckEntry CoolRow(object deck, object t, int shipUid, string shipName, string[] wish)
            {
                string ini = Get(t, AswSpecs.CoolIniRef) as string;
                if (string.IsNullOrEmpty(ini)) return null;
                object vt = null;
                var vs = Get(deck, AswSpecs.DeckVehicles) as IEnumerable;
                if (vs != null) foreach (var v in vs) if (Get(v, AirOpsSpecs.VtFileName) as string == ini) { vt = v; break; }
                if (vt == null || !Interesting(vt, wish)) return null;
                var d = new AswMath.DeckEntry { ShipUid = shipUid, ShipName = shipName, Type = ini, Label = Get(vt, AirOpsSpecs.VtDisplayName) as string, Squadron = Get(t, AswSpecs.TaskSquadron) as string, Roles = RolesOf(vt), State = "cooling" };
                d.Helicopter = Get(vt, AirOpsSpecs.VtType) != null && Get(vt, AirOpsSpecs.VtType).ToString() == "Helicopter";
                d.AswCapableType = AswCapable(vt, wish);
                FillDip(d, ini, null);
                try
                {
                    bool w; var lo = PickLoadout(vt, wish, out w);
                    d.NextPrepS = AirOpsMath.HangarPrepS(lo == null ? 0 : (D(Get(lo, AirOpsSpecs.LoReadyUp)) ?? 0), _a.AirOps.DeckTimingsMode());   // T-213: the next order of this machine prepares in the hangar
                }
                catch { }
                var dur = D(Get(t, AswSpecs.TaskDuration)); var st = D(Get(t, AswSpecs.TaskStartTime));
                double rem = dur.HasValue && dur.Value > 0 && st.HasValue && st.Value > 0 ? dur.Value - (Now - st.Value) : 0;
                d.ReadyInS = Math.Max(0, Math.Min(dur ?? 0, rem));
                var g = Get(t, AswSpecs.TaskUid);
                d.Key = "cool:" + g;
                d.Uid = AswMath.TaskUid(d.Key);
                var sn = Get(t, AswSpecs.CoolSerial);
                d.Serial = sn == null ? null : sn.ToString();
                return d;
            }

            /// <summary>Per mission: forget everything that is keyed by the units of the previous one.</summary>
            internal void ResetMission()
            {
                _subMem.Clear();
                _hist.Clear(); _histT.Clear(); _deckShip.Clear(); _lastTasks.Clear(); _skipLogged.Clear(); _losCache.Clear(); _buoySeen.Clear();
                _heights.Clear(); _sampler = null; _samplerTried = false; _adRange.Clear(); _deckCount.Clear(); _ltpKeys.Clear(); _typeLabels.Clear(); _aswType.Clear();
            }

            // ---- T-150: terrain, queue, hostile air defence ----------------------------------------------------------

            private readonly Dictionary<long, double> _heights = new Dictionary<long, double>();
            private Func<double, double, double> _sampler;
            private bool _samplerTried;

            public double HeightM(GeoPt g)
            {
                try
                {
                    if (!_samplerTried) { _samplerTried = true; _sampler = _a.TerrainSampler(false); }
                    if (_sampler == null) return -200;
                    long key = (((long)Math.Round(g.Lat * 60.0 / 0.05)) << 32) ^ (((long)Math.Round(g.Lon * 60.0 / 0.05)) & 0xffffffffL);
                    double h;
                    if (_heights.TryGetValue(key, out h)) return h;
                    if (_heights.Count > 20000) _heights.Clear();
                    h = _sampler(g.Lat, g.Lon);
                    if (double.IsNaN(h)) h = -200;
                    _heights[key] = h;
                    return h;
                }
                catch { return -200; }
            }

            public int QueuedTasks(int uid)
            {
                var u = Unit(uid);
                if (u == null) return -1;
                try
                {
                    var tv = Get(Get(u, AswSpecs.ObjUserRoot), AswSpecs.UserRootTasks) as ICollection;
                    return tv == null ? -1 : tv.Count;
                }
                catch { return -1; }
            }

            private readonly Dictionary<string, double> _adRange = new Dictionary<string, double>();

            /// <summary>Longest air-defence reach (nm) in the class data of an identified hostile class: anti-air ammunition of its loadouts (UnitMetadataProvider + AmmunitionParameters).
            /// 0 = unknown (the controller then uses its default radius). Best effort: any missing member gives 0.</summary>
            private double ClassAirDefenceNm(string cls)
            {
                double r;
                if (string.IsNullOrEmpty(cls)) return 0;
                if (_adRange.TryGetValue(cls, out r)) return r;
                r = 0;
                try
                {
                    var prov = _a.SingletonInstance(AswSpecs.MetaType);
                    var data = prov == null ? null : Get(prov, AswSpecs.MetaUnitData) as IEnumerable;
                    var ammoData = prov == null ? null : Get(prov, AswSpecs.MetaAmmoData);
                    object sop = null;
                    if (data != null)
                        foreach (var kv in data)
                        {
                            var key = Get(kv, CoreSpecs.PairKey);
                            if (!string.Equals(Get(key, AswSpecs.TupleItem2) as string, cls, StringComparison.OrdinalIgnoreCase)) continue;
                            sop = Get(Get(kv, CoreSpecs.PairValue), AswSpecs.TupleItem2);
                            break;
                        }
                    var los = sop == null ? null : Get(sop, AswSpecs.SopLoadouts) as IDictionary;
                    if (los != null)
                        foreach (DictionaryEntry le in los)
                        {
                            var am = Get(le.Value, AswSpecs.LoAccountable) as IDictionary;
                            if (am == null) continue;
                            foreach (DictionaryEntry ae in am)
                            {
                                string name = ae.Key as string;
                                if (name == null) continue;
                                var simple = ammoData == null ? null : (ammoData as IDictionary) != null ? ((IDictionary)ammoData)[name] : null;
                                var tt = simple == null ? null : Get(simple, AswSpecs.SimpleTargetType);
                                if (tt == null || tt.ToString() != "AAW") continue;
                                var ap = _a.CallStatic(AswSpecs.ApGetShared, name);
                                var rng = ap == null ? null : D(Get(ap, AswSpecs.ApMaxLaunchRange));
                                if (rng.HasValue && rng.Value > r) r = rng.Value;
                            }
                        }
                }
                catch { r = 0; }
                if (r > 0) r = Math.Min(40, Math.Max(2, r));
                _adRange[cls] = r;
                return r;
            }

            /// <summary>Hostile surface / ground contacts the player knows (tracks only, as the SAR threats) with the air defence reach of their class when it is identified.</summary>
            public IList<AswHazard> Hazards()
            {
                var res = new List<AswHazard>();
                try
                {
                    var snap = _a.Snapshot(false);
                    foreach (var t in snap.Tracks)
                    {
                        if (t.Side != "hostile" || !t.Lat.HasValue || !t.Lon.HasValue || t.Gone) continue;
                        string k = t.Kind ?? "";
                        bool ident = t.Identified && !string.IsNullOrEmpty(t.Class);
                        double ad = ident ? ClassAirDefenceNm(t.Class) : 0, range;
                        if (!AswRouteGuard.HazardOf(k, ident, ad, out range)) continue;   // T-180: no Unknown / empty kind, no coast without air defence
                        res.Add(new AswHazard(new GeoPt(t.Lat.Value, t.Lon.Value), range) { TrackId = t.Id, Kind = k });
                    }
                }
                catch { }
                return res;
            }

            // T-343: an ASW-capable type = one of its loadouts is ASW or its dipping sonar is loaded; roles / _aswCapability alone never (Ka-25Ts: MPA, AEW only)
            private readonly Dictionary<string, bool> _aswType = new Dictionary<string, bool>();

            private bool AswCapable(object vt, string[] wish)
            {
                string file = Get(vt, AirOpsSpecs.VtFileName) as string ?? "";
                bool res;
                if (file.Length > 0 && _aswType.TryGetValue(file, out res)) return res;
                var los = new List<LoadoutOption>();
                var en = Get(vt, AirOpsSpecs.VtLoadouts) as IEnumerable ?? Get(vt, AirOpsSpecs.VtLoadoutsField) as IEnumerable;
                if (en != null)
                    foreach (var l in en)
                    {
                        string n = Get(l, AirOpsSpecs.LoName) as string;
                        los.Add(new LoadoutOption { Name = n, Display = Get(l, AirOpsSpecs.LoDisplayName) as string ?? n, Weapons = AmmoOfLoadout(l) });
                    }
                bool? dip = null;
                try { var info = file.Length > 0 ? _a.TypeDipSonar(file, null) : null; if (info != null) dip = info.Loaded; } catch { }
                res = AswMath.TypeAswCapable(los, string.Join(",", wish ?? new string[0]), dip);
                if (file.Length > 0) _aswType[file] = res;
                return res;
            }

            /// <summary>Deck vehicle types worth listing: helicopters, anything with an ASW capability or an ASW/MPA/AEW role, or a configured loadout.</summary>
            private bool Interesting(object vt, string[] wish)
            {
                var roles = RolesOf(vt);
                if (AswCapable(vt, wish) || Array.IndexOf(roles, "AEW") >= 0) return true;
                var ty = Get(vt, AirOpsSpecs.VtType);
                if (ty != null && ty.ToString() == "Helicopter") return true;
                var lo = Get(vt, AirOpsSpecs.VtLoadouts) as IEnumerable ?? Get(vt, AirOpsSpecs.VtLoadoutsField) as IEnumerable;
                if (lo != null) foreach (var l in lo) if (Array.IndexOf(wish, Get(l, AirOpsSpecs.LoName) as string ?? "") >= 0) return true;
                return false;
            }

            // ---- targets and contacts -------------------------------------------------------------------------------

            private AswTarget TargetOf(object u)
            {
                if (u == null || !GameApi.IsAlive(u)) return null;
                double lat, lon, h;
                if (!_a.OwnPos(u, out lat, out lon, out h)) return null;
                var t = new AswTarget { Uid = _a.UidOf(u), Name = _a.NameOf(u), Lat = lat, Lon = lon, WidthNm = 4 };
                try { t.SpeedKn = D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0; } catch { }
                try { t.ImmobileWhy = _a.ShipImmobileWhy(u, out t.WayPending, out t.Telegraph); } catch { }   // T-266 / ADR-0106
                try { var hd = D(RxGet(u, AirOpsSpecs.ObjHeading)); t.CourseDeg = hd.HasValue ? ((hd.Value % 360) + 360) % 360 : 0; } catch { }
                try
                {
                    var wps = _a.PlayerWaypoints(u);   // T-278: not the detour point of the mod after an evasion
                    if (wps.Count > 0) t.NextWpBrg = WorldBearing(lat, lon, wps[0][0], wps[0][1]);
                }
                catch { }
                try
                {
                    // T-238 / ADR-0098: the player's route (the saved one while the mod steers the ship) gives the barrier axis
                    var e = _a._ctlMem.Peek(u); bool steered;
                    var route = AswRouteGuard.PlayerRoute(_a.PlayerWaypoints(u), e != null && e.HasRoute && e.PlacedByMod != null, e == null ? null : e.SavedRoute, e == null ? null : e.PlacedByMod, out steered);
                    t.RouteBrg = AswRouteGuard.NextRouteBrg(lat, lon, route); t.ModSteered = steered || _a.EvasionDetourActive(u); t.SavedCourseDeg = steered ? e.SavedCourseDeg : null;
                }
                catch { }
                // track: our own 3 min history, 1 sample per call
                List<double[]> hist;
                if (!_hist.TryGetValue(t.Uid, out hist)) { hist = new List<double[]>(); _hist[t.Uid] = hist; }
                double now = Now;
                double lastT;
                if (!_histT.TryGetValue(t.Uid, out lastT)) lastT = -1e9;
                if (hist.Count == 0 || now - lastT >= 1.0) { hist.Add(new[] { lat, lon }); _histT[t.Uid] = now; }
                while (hist.Count > 180) hist.RemoveAt(0);
                if (_hist.Count > 48) PruneHist(now);
                t.Track = new List<double[]>(hist);
                return t;
            }

            /// <summary>T-150 (memory audit item 5): tracks of units not asked for 10 minutes go (destroyed ships, old targets).</summary>
            private void PruneHist(double now)
            {
                foreach (int k in new List<int>(_hist.Keys))
                {
                    double t;
                    if (!_histT.TryGetValue(k, out t) || now - t > 600 || now < t) { _hist.Remove(k); _histT.Remove(k); }
                }
            }

            private readonly Dictionary<int, List<double[]>> _hist = new Dictionary<int, List<double[]>>();
            private readonly Dictionary<int, double> _histT = new Dictionary<int, double>();

            public AswTarget Target(int uid) { return TargetOf(_a.FindOwnUnit(uid)); }

            public AswTarget TargetByName(string name)
            {
                if (name == null) return null;
                foreach (var u in _a.OwnUnits()) if (GameApi.IsAlive(u) && _a.NameOf(u) == name) return TargetOf(u);
                return null;
            }

            public AswTarget SelectedShip()
            {
                string src;
                var u = _a.SelectedUnit(out src);
                if (u != null && !_a.IsAirUnit(u) && _a.FindOwnUnit(_a.UidOf(u)) != null) return TargetOf(u);
                foreach (var s in _a.DeckShips()) return TargetOf(s);   // fallback: the first own ship with a deck
                return null;
            }

            /// <summary>T-142 K: our surface ships and the formations (leader's course, width across it from the stations + 2 nm). Unknown formation members: ships only.</summary>
            public IList<AswTargetOption> CourseTargets()
            {
                var res = new List<AswTargetOption>();
                int sel = 0;
                try { var st = SelectedShip(); if (st != null) sel = st.Uid; } catch { }
                try
                {
                    foreach (var u in _a.OwnUnits())
                    {
                        if (!GameApi.IsAlive(u) || _a.IsAirUnit(u)) continue;
                        int uid = _a.UidOf(u);
                        var opt = new AswTargetOption { Uid = uid, Name = _a.NameOf(u), Kind = "ship", Selected = uid == sel };
                        double plat, plon, phdg;
                        if (_a.OwnPos(u, out plat, out plon, out phdg)) { opt.HasPos = true; opt.Lat = plat; opt.Lon = plon; }   // T-178 A2: the distance in the ship pick
                        res.Add(opt);
                    }
                }
                catch (Exception e) { Log("asw course targets (ships): " + e.Message); }
                try
                {
                    foreach (var f in _a.PlayerFormations())
                    {
                        var members = _a.MembersOf(f);
                        var leader = _a.LeaderOf(f);
                        if (leader == null || members.Count < 2) continue;
                        var lt = TargetOf(leader);
                        if (lt == null) continue;
                        double lo = 0, hi = 0;
                        foreach (var m in members)
                        {
                            double lat, lon, h;
                            if (!_a.OwnPos(m, out lat, out lon, out h)) continue;
                            double y = AswMath.ToLocal(new GeoPt(lt.Lat, lt.Lon), lt.CourseDeg, new GeoPt(lat, lon)).Y;
                            lo = Math.Min(lo, y); hi = Math.Max(hi, y);
                        }
                        string name = Get(f, AswSpecs.FormationName) as string ?? lt.Name;
                        res.Add(new AswTargetOption { Uid = lt.Uid, Name = name, Kind = "formation", Selected = lt.Uid == sel, WidthNm = Math.Max(4, hi - lo + 2), HasPos = true, Lat = lt.Lat, Lon = lt.Lon });
                    }
                }
                catch (Exception e) { Log("asw course targets (formations): " + e.Message); }
                return res;
            }

            private sealed class SubMem { public object Obj; public double Lat, Lon; public bool Reported; }
            private readonly Dictionary<int, SubMem> _subMem = new Dictionary<int, SubMem>();

            /// <summary>The remembered game object of a submarine track is destroyed (IsDestroyed, or the Unity object is gone).</summary>
            private bool SubDestroyed(object o)
            {
                try { return o != null && (!GameApi.IsAlive(o) || BoolOf(Get(o, AirOpsSpecs.ObjIsDestroyed))); }   // nothing remembered = not known to be destroyed
                catch { return false; }
            }

            public IList<AswContact> SubContacts()
            {
                var res = new List<AswContact>();
                var snap = _a.Snapshot(true);   // true: Gone/Live/ObjType are filled only for the overview pass
                var seen = new HashSet<int>();
                foreach (var t in snap.Tracks)
                {
                    if (t.Kind != "Submarine" && t.ObjType != "Submarine") continue;
                    seen.Add(t.Id);
                    object vv = null;
                    try { snap.VehicleById.TryGetValue(t.Id, out vv); } catch { }
                    SubMem mem;
                    if (t.Gone || !t.Lat.HasValue || !t.Lon.HasValue)
                    {
                        // T-172: a track whose object is gone: destroyed (the object we remembered is dead) -> one Killed contact; else just lost
                        if (_subMem.TryGetValue(t.Id, out mem) && !mem.Reported && SubDestroyed(mem.Obj))
                        {
                            mem.Reported = true;
                            res.Add(new AswContact { Id = t.Id, Name = "Sub #" + t.Id, Lat = mem.Lat, Lon = mem.Lon, Source = "track", Hostile = true, AttackOk = false, Side = t.Side, Killed = true });
                        }
                        continue;
                    }
                    // 0.27.8: the main map paints a track red by MapContactVisuals (posture "Enemy") even when the relationship reads "unknown";
                    // the attack follows what the player sees (overview red = hostile); 0.27.10: one rule with the attack execution (AttackHostility)
                    bool enemy = AttackHostility.AttackOk(t.Side, t.MapPosture, t.VmPosture);
                    // 0.27.10 (live): a noisemaker next to the boat came in as "Sub #7047" (map type Submarine) and was proposed again and again
                    string objCls = null;
                    try { if (vv != null) { var o = _a.Get(vv, CoreSpecs.VehObject); if (o != null) objCls = o.GetType().Name; } } catch { }
                    string decoy = AswMath.DecoyName(t.Class, t.ClassName, t.ObjType, objCls);
                    if (!_subMem.TryGetValue(t.Id, out mem)) _subMem[t.Id] = mem = new SubMem();
                    mem.Lat = t.Lat.Value; mem.Lon = t.Lon.Value;
                    try { if (vv != null) { var ob = _a.Get(vv, CoreSpecs.VehObject); if (ob != null) mem.Obj = ob; } } catch { }
                    // T-172: Live is passed on (a stale track is no new contact); the controller filters
                    var sc = new AswContact { Id = t.Id, Name = "Sub #" + t.Id, Lat = t.Lat.Value, Lon = t.Lon.Value, Source = t.Live ? "live" : "track", Live = t.Live, Hostile = enemy || t.Side == "unknown", AttackOk = enemy && decoy == null, Side = t.Side, Decoy = decoy };
                    StrikeFill(sc, t, vv, objCls);
                    res.Add(sc);
                }
                // a remembered submarine that vanished from the table altogether and whose object is dead
                foreach (var kv in new List<KeyValuePair<int, SubMem>>(_subMem))
                {
                    if (seen.Contains(kv.Key) || kv.Value.Reported || !SubDestroyed(kv.Value.Obj)) continue;
                    kv.Value.Reported = true;
                    res.Add(new AswContact { Id = kv.Key, Name = "Sub #" + kv.Key, Lat = kv.Value.Lat, Lon = kv.Value.Lon, Source = "track", Hostile = true, AttackOk = false, Killed = true });
                }
                return res;
            }

            // ---- orders ---------------------------------------------------------------------------------------------

            private object Unit(int uid) { return uid < 0 ? null : _a.FindOwnUnit(uid); }

            public List<BuoyType> BuoyTypes(int uid)
            {
                var u = Unit(uid);
                return u == null ? new List<BuoyType>() : _a.BuoyTypesOn(u);
            }

            public double DipRangeNm(int uid)
            {
                var u = Unit(uid);
                if (u == null) return 0;
                try
                {
                    var list = Get(Get(u, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpSensorSystems) as IEnumerable;
                    return AswDipTasks.DipRangeNm(list,
                        s => s.GetType().Name == "SensorSystemSonar" && BoolOf(Get(s, AswSpecs.SensorDipping)),
                        s => Get(s, AswSpecs.SensorSystemName) as string,
                        n => { string src; return _a.SonobuoyRangeNm(n, out src); });
                }
                catch { return 0; }
            }

            public string Drop(int uid, string ammo, IList<GeoPt> pts)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                int q;
                return _a.DropSonobuoys(u, ammo, pts, out q);
            }

            public string Dip(int uid, IList<GeoPt> pts)
            {
                var u = Unit(uid);
                return u == null ? "unit_not_found" : _a.DipSonar(u, pts);
            }

            private const double FeetPerUnitAlt = 220.47266;   // as Aviation.cs FeetPerUnit / CapGame

            /// <summary>Highest altitude (ft) at which the MAD of the aircraft works; 0 = unknown.</summary>
            private double MadMaxDeployFt(object u)
            {
                try
                {
                    var list = Get(Get(u, AirOpsSpecs.ObjObp), AirOpsSpecs.ObpSensorSystems) as IEnumerable;
                    if (list == null) return 0;
                    foreach (var s in list)
                    {
                        if (s == null || s.GetType().Name != "SensorSystemMAD") continue;
                        var v = D(Get(s, AswSpecs.MadMaxDeployAlt));
                        if (v.HasValue && v.Value > 0) return v.Value * FeetPerUnitAlt;
                    }
                }
                catch { }
                return 0;
            }

            public string Goto(int uid, IList<GeoPt> pts, int altFt)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                // T-172 B10: an aircraft the game does not let us steer is refused before its route is touched
                try { var ctl = Get(u, CoreSpecs.ObjIsControllable); if (ctl is bool && !(bool)ctl) return "not_controllable"; } catch { }
                _a.WpqNotePlaced(uid, "asw_goto");   // T-331
                try
                {
                    // T-172 B2: a MAD leg flies at its own height, at most 0.8 of the MAD deployment limit (AswMath.MadAltFt); other routes keep the current height
                    if (altFt > 0 && _a.Present(AirOpsSpecs.ObjSetWaypoint))
                    {
                        int ft = AswMath.MadAltFt(altFt, MadMaxDeployFt(u));
                        double h = ft / FeetPerUnitAlt;
                        _a.TacticsEnsureMission();
                        int? tgBefore = _a.Telegraph(u);
                        CallOpt(u, AirOpsSpecs.ObjRemoveWaypoints);
                        object firstGeo = null;
                        foreach (var p in pts)
                        {
                            var geo = _a.MakeGeo(p.Lat, p.Lon, h);
                            if (firstGeo == null) firstGeo = geo;
                            _a.CallOpt(u, AirOpsSpecs.ObjSetWaypoint, geo);
                        }
                        try { if (firstGeo != null) _a.Set(u, AirOpsSpecs.ObjCommandPosition, firstGeo); } catch { }
                        int? tgAfter = _a.Telegraph(u);
                        if (tgBefore.HasValue && tgAfter.HasValue && tgBefore.Value != tgAfter.Value) { try { _a.Call(u, AirOpsSpecs.ObjSetTelegraph, tgBefore.Value); } catch { } }
                        Log("asw goto uid=" + uid + " alt_ft=" + ft + " points=" + pts.Count);
                        return null;
                    }
                    // ordinary waypoints (existing add_waypoint path); the first call clears the old route
                    CallOpt(u, AirOpsSpecs.ObjRemoveWaypoints);
                    foreach (var p in pts)
                    {
                        var cmd = new Dictionary<string, object> { { "uid", (double)uid }, { "lat", p.Lat }, { "lon", p.Lon }, };
                        string ack = _a.GoTo("null", cmd, true);
                        if (ack != null && ack.Contains("\"ok\":false")) return "waypoint_failed";
                    }
                    return null;
                }
                catch { return "waypoint_failed"; }
            }

            public string ClearRoute(int uid)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                try { CallOpt(u, AirOpsSpecs.ObjRemoveWaypoints); return null; }
                catch { return "clear_failed"; }
            }

            public string SetDepth(int uid, string mode, out string previous)
            {
                previous = null;
                var u = Unit(uid);
                return u == null ? "unit_not_found" : _a.SetSonoDepth(u, mode, out previous);
            }

            public string HoldRoe(int uid, out string previous)
            {
                previous = null;
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                previous = _a.WeaponStatusName(u);
                _a._ctlMem.RememberRoe(u, previous);
                if (previous == "hold") return null;
                string ack = _a.RoeCmd("null", new Dictionary<string, object> { { "uid", (double)uid }, { "status", "hold" } });
                if (ack == null || !ack.Contains("\"ok\":true")) return "roe_failed";
                _a._ctlMem.NoteRoeSet(u, "hold");
                return null;
            }

            public string RestoreRoe(int uid, string roe)
            {
                var u = Unit(uid);
                if (u == null || roe == null) return "unit_not_found";
                string ack = _a.RoeCmd("null", new Dictionary<string, object> { { "uid", (double)uid }, { "status", roe }, { "confirm", true } });
                return ack != null && ack.Contains("\"ok\":true") ? null : "roe_failed";
            }

            /// <summary>T-119: the loadout the voice launch path (Aviation.cs) would take for this deck (Aviation.PickLoadout rules, year of service, CanLaunchVehicle,
            /// sea state for helicopters). The first name of the CSV that works wins.</summary>
            public string ResolveLoadout(int deckUid, string type, string loadoutsCsv, out string err)
            {
                err = "no_deck";
                try
                {
                    var ship = _a.FindOwnUnit(deckUid);
                    var deck = ship == null ? null : _a.DeckOf(ship);
                    if (deck == null) return null;
                    object vehicle = null;
                    var vs = Get(deck, AswSpecs.DeckVehicles) as IEnumerable;
                    if (vs != null) foreach (var v in vs) if (Get(v, AirOpsSpecs.VtFileName) as string == type) { vehicle = v; break; }
                    if (vehicle == null) { err = "unknown_loadout"; return null; }
                    string vtype = Get(vehicle, AirOpsSpecs.VtType) == null ? "None" : Get(vehicle, AirOpsSpecs.VtType).ToString();
                    if (vtype == "Helicopter" && _a.Present(AirOpsSpecs.DeckCanOperateHelo))
                    {
                        bool okSea = true;
                        try { okSea = (bool)_a.Call(deck, AirOpsSpecs.DeckCanOperateHelo, vehicle); } catch { }
                        if (!okSea) { err = "sea_state"; return null; }
                    }
                    var infos = new List<LoadoutInfo>();
                    var names = new List<string>();
                    var lo = Get(vehicle, AirOpsSpecs.VtLoadouts) as IEnumerable;
                    if (lo != null)
                        foreach (var l in lo)
                        {
                            bool can = false;
                            try { can = (bool)_a.CallOpt(deck, AirOpsSpecs.DeckCanLaunch, l, 1); } catch { }
                            string nm = Get(l, AirOpsSpecs.LoName) as string;
                            infos.Add(new LoadoutInfo { Name = nm, Display = Get(l, AirOpsSpecs.LoDisplayName) as string, Year = (int)(D(Get(l, AirOpsSpecs.LoServiceYear)) ?? 0), CanLaunch = can });
                            names.Add(nm);
                        }
                    int year = _a.MissionYear();
                    err = "unknown_loadout";
                    foreach (string wish in (loadoutsCsv ?? "").Split(','))
                    {
                        string w = wish.Trim();
                        if (w.Length == 0) continue;
                        string e;
                        int i = GameApi.PickLoadout(infos, w, year, out e);
                        if (i >= 0) { err = null; return names[i]; }
                        if (e == "no_ammo") err = "no_ammo";
                    }
                    return null;
                }
                catch (Exception e) { err = "launch_failed:" + (e.InnerException ?? e).GetType().Name; return null; }
            }

            public bool TasksChangedByOther(int uid)
            {
                var u = Unit(uid);
                if (u == null) return false;
                try
                {
                    var tasks = Get(Get(u, AswSpecs.ObjUserRoot), AswSpecs.UserRootTasks) as IEnumerable;
                    if (tasks == null) return false;
                    var kinds = new List<string>();
                    foreach (var tv in tasks)
                    {
                        var t = tv == null ? null : Get(tv, AswSpecs.TaskVmTask);
                        if (t != null) kinds.Add(t.GetType().Name);
                    }
                    return AswDipTasks.ChangedByOther(_lastTasks, uid, kinds);
                }
                catch { }
                return false;
            }

            public string AttackWeapon(int uid)
            {
                var u = Unit(uid);
                if (u == null) return null;
                try
                {
                    var rows = _a.WeaponRows(u);
                    int k = AswMath.AswWeaponIndex(rows);   // T-172: a torpedo / ASROC before a depth charge / RBU
                    if (k >= 0) return rows[k][1];
                }
                catch { }
                return null;
            }

            public string Attack(int uid, int contactId) { return AttackTrack(uid, contactId, false); }

            /// <summary>T-173: unknownOk = an unidentified classified track (not painted red) goes through the mod's own engage path (NeutralGuard stays).</summary>
            public string AttackTrack(int uid, int contactId, bool unknownOk)
            {
                string weapon = "asw";
                try
                {
                    // T-172: "asw" takes the first weapon of the category; when a better one (torpedo before a depth charge) is not the first, it goes by its file
                    var u = Unit(uid);
                    if (u != null)
                    {
                        var rows = _a.WeaponRows(u);
                        int k = AswMath.AswWeaponIndex(rows), first = -1;
                        for (int i = 0; i < rows.Count && first < 0; i++) if (rows[i][4] == "asw" && int.Parse(rows[i][5]) > 0) first = i;
                        if (k >= 0 && k != first) weapon = rows[k][0];
                    }
                }
                catch { }
                var cmd = new Dictionary<string, object>
                {
                    { "uid", (double)uid }, { "target_id", (double)contactId }, { "weapon", weapon }, { "confirm", true }
                };
                // 0.27.10: the shared Attack (Common) accepts only relationship "hostile"; a track the main map paints red while the relationship
                // still reads "unknown" was proposed (AttackHostility) but then refused with not_hostile. Such a track goes through the
                // UI mod's own engage path (same weapon / range / mount / neutral-corridor steps, see MapHostileAttack.cs).
                string route = _a.AswAttackRoute(contactId, unknownOk);
                if (route == "map") return _a.AttackMapHostile(cmd, contactId, "asw", unknownOk);
                if (route != "relation") return route;   // not_hostile / unknown_target
                string ack = _a.Attack("null", cmd, false);
                if (ack != null && ack.Contains("\"ok\":true")) return null;
                try
                {
                    var d = Json.Parse(ack) as Dictionary<string, object>;
                    return d != null && d.ContainsKey("error") ? d["error"] as string : "attack_failed";
                }
                catch { return "attack_failed"; }
            }

            public void Event(string name, string dataJson)
            {
                try { _a.EvLine(name, dataJson); } catch { }
            }

            private readonly Dictionary<int, double> _buoySeen = new Dictionary<int, double>();

            /// <summary>
            /// Own sonobuoys in the water: Bomb objects of the own side whose launch platform is an own aircraft. Best effort and unverified
            /// offline (the buoy objects are only seen live); returns nothing when the members are missing.
            /// </summary>
            private bool OrphanLaunched(object u)
            {
                try { return Convert.ToBoolean(_a.Call(u, BuoyReviveSpecs.IsLaunched)); }   // isLaunched() of WeaponBase
                catch { return false; }
            }

            public IList<AswBuoyObs> OwnBuoys()
            {
                var res = new List<AswBuoyObs>();
                try
                {
                    foreach (var u in _a.OwnUnits())
                    {
                        if (u == null || u.GetType().Name != "Bomb" || !GameApi.IsAlive(u)) continue;
                        var plat = Get(u, AirOpsSpecs.BombLaunchPlatform);
                        if (plat == null && !OrphanLaunched(u)) continue;   // T-094: a revived orphan (no launch platform, launched again after the load) is ours too
                        double lat, lon, h;
                        if (!_a.OwnPos(u, out lat, out lon, out h)) continue;
                        int id = _a.UidOf(u);
                        double seen;
                        if (!_buoySeen.TryGetValue(id, out seen)) { seen = Now; _buoySeen[id] = seen; }
                        double rl = -1;
                        try { var rv = D(_a.RxGet(u, AswSpecs.BombRemainingLife)); if (rv.HasValue && !double.IsNaN(rv.Value) && rv.Value >= 0) rl = rv.Value; } catch { }
                        res.Add(new AswBuoyObs { Id = id, OwnerUid = plat == null ? 0 : _a.UidOf(plat), Lat = lat, Lon = lon, DroppedS = seen, Ammo = _a.NameOf(u), RemainingS = rl });
                    }
                }
                catch { }
                return res;
            }

            public void Log(string line)
            {
                if (line.StartsWith("[Sar]", StringComparison.Ordinal) || line.StartsWith("[AirGuard]", StringComparison.Ordinal))   // the SAR / air threat log lines of the controller come with their own tag
                {
                    try { var sk = _a.AswLogSink; if (sk != null) sk(line); else UnityEngine.Debug.Log(line); } catch { }
                    return;
                }
                // T-150 P8: the lines that explain what happened to a machine (adopt, handover, member) are always written: their absence in a log proved nothing.
                // Noisy ones (route, standby, frame, assign) stay behind [Asw] Debug.
                if (!_cfg.Debug && !AswMath.LogAlways(line)) return;   // ADR-0063: every buoy package ("asw drop") with its spacing, always
                try
                {
                    var sink = _a.AswLogSink;
                    if (sink != null) sink("[Asw] " + line); else UnityEngine.Debug.Log("[asw] " + line);
                }
                catch { }
            }
        }
    }
}
