using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        private AirOpsGameImpl _airOps;

        // T-126: inside one air tick ASW and CAP share one scan of the air units and of the deck ships; outside a tick every call scans the game afresh
        private readonly TickMemo<List<object>> _airUnitsMemo = new TickMemo<List<object>>();
        private readonly TickMemo<List<object>> _deckShipsMemo = new TickMemo<List<object>>();

        // T-186 (ADR-0082 s.2): Numbers of the hangar per deck and type, read once per air tick for the shared slot book; plain ints, no game objects
        private Dictionary<int, Dictionary<string, int>> _hangarMemo;

        internal void AirScanBegin() { _airUnitsMemo.Begin(); _deckShipsMemo.Begin(); _hangarMemo = new Dictionary<int, Dictionary<string, int>>(); }

        /// <summary>Machines of a type in the hangar of a deck (sum of Numbers over the records of the type); 0 = none, -1 = the deck cannot be read (the book then moves nothing).</summary>
        internal int AirHangarNumbers(int deckUid, string type)
        {
            try
            {
                Dictionary<string, int> d;
                if (_hangarMemo == null || !_hangarMemo.TryGetValue(deckUid, out d))
                {
                    var ship = FindOwnUnit(deckUid);
                    var deck = ship == null ? null : DeckOf(ship);
                    var vs = deck == null ? null : Get(deck, AirOpsSpecs.DeckVehicles) as IEnumerable;
                    if (vs == null) return -1;
                    d = new Dictionary<string, int>();
                    foreach (var v in vs)
                    {
                        string t = Get(v, AirOpsSpecs.VtFileName) as string;
                        double n = D(Get(v, AirOpsSpecs.VtNumbers)) ?? 0;
                        if (t == null || n < 1) continue;
                        int acc; d.TryGetValue(t, out acc); d[t] = acc + (int)n;
                    }
                    if (_hangarMemo != null) _hangarMemo[deckUid] = d;
                }
                int c;
                return d.TryGetValue(type ?? "", out c) ? c : 0;
            }
            catch { return -1; }
        }

        /// <summary>Drops the cached lists (no game objects are kept between ticks). Call from finally.</summary>
        internal void AirScanEnd() { _airUnitsMemo.End(); _deckShipsMemo.End(); _hangarMemo = null; }

        internal List<object> AirUnitsTick() { return _airUnitsMemo.Get(AirUnits); }

        internal List<object> DeckShipsTick() { return _deckShipsMemo.Get(DeckShips); }

        /// <summary>
        /// 0.27.8 diag (dev section "deck", key "game"): every task of every own flight deck as the game lists it (pending, launch/taxi, recovery,
        /// cool-down, ...) and the hangar numbers. Read only; called on the main thread by the dev state request.
        /// </summary>
        internal string AirDeckGameJson(int limit)
        {
            var impl = AirOps as AirOpsGameImpl;
            return impl == null ? "[]" : impl.GameDecksJson(DeckShips(), limit);
        }

        /// <summary>The deck layer (created on first use). Safe outside a mission: every method answers empty or with a code.</summary>
        internal IAirOps AirOps
        {
            get { if (_airOps == null) _airOps = new AirOpsGameImpl(this); return _airOps; }
        }

        private sealed class AirOpsGameImpl : IAirOps
        {
            private readonly GameApi _a;
            private readonly HashSet<string> _ours = new HashSet<string>();
            private readonly HashSet<string> _capLogged = new HashSet<string>();
            private readonly Dictionary<string, double> _noAirLogAt = new Dictionary<string, double>();   // T-231: the no_airframes diagnostics, once per 600 s of the mission per (deck, type)

            public AirOpsGameImpl(GameApi a) { _a = a; }

            private object Get(object o, MemberSpec s) { return _a.Get(o, s); }

            private object Deck(int deckUid)
            {
                try
                {
                    var ship = _a.FindOwnUnit(deckUid);
                    return ship == null ? null : _a.DeckOf(ship);
                }
                catch { return null; }
            }

            private void Log(string line)
            {
                try { var s = _a.AswLogSink; if (s != null) s(line); else UnityEngine.Debug.Log(line); } catch { }
            }

            public string DeckTimingsMode()
            {
                try
                {
                    var om = _a.SingletonInstance(AirOpsSpecs.OptionsType);
                    var m = Get(om, AirOpsSpecs.DeckTimingsMode);
                    return m == null ? "Realistic" : m.ToString();
                }
                catch { return "Realistic"; }
            }

            private object VehicleOf(object deck, string vehicleType)
            {
                var vs = Get(deck, AirOpsSpecs.DeckVehicles) as IEnumerable;
                if (vs == null) return null;
                foreach (var v in vs) if (Get(v, AirOpsSpecs.VtFileName) as string == vehicleType) return v;
                return null;
            }

            private static string[] RolesOf(GameApi a, object vt)
            {
                var res = new List<string>();
                var roles = a.Get(vt, AirOpsSpecs.VtUnitRoles) as IEnumerable;
                if (roles != null) foreach (var r in roles) if (r != null) res.Add(r.ToString());
                return res.ToArray();
            }

            public List<LoadoutOption> Loadouts(int deckUid, string vehicleType)
            {
                var res = new List<LoadoutOption>();
                var deck = Deck(deckUid);
                if (deck == null) return res;
                try
                {
                    var vt = VehicleOf(deck, vehicleType);
                    if (vt == null) return res;
                    var roles = RolesOf(_a, vt);
                    var los = Get(vt, AirOpsSpecs.VtLoadouts) as IEnumerable ?? Get(vt, AirOpsSpecs.VtLoadoutsField) as IEnumerable;
                    if (los == null) return res;
                    foreach (var lo in los)
                    {
                        if (lo == null) continue;
                        var o = new LoadoutOption { Name = Get(lo, AirOpsSpecs.LoName) as string, Display = Get(lo, AirOpsSpecs.LoDisplayName) as string, Roles = roles };
                        // PatrolZone.GetSARTransportCapacity(loadout) = [Physics] TransportCapacity overridden by the loadout section (the game's own SAR rule)
                        string src = "game";
                        try { o.Capacity = Convert.ToInt32(_a.CallStatic(AirOpsSpecs.SarCapacity, lo)); }
                        catch { o.Capacity = 0; src = "none"; }
                        var rd = GameApi.D(Get(lo, AirOpsSpecs.LoReadyUp)); var cd = GameApi.D(Get(lo, AirOpsSpecs.LoCoolDown));
                        if (rd.HasValue && rd.Value >= 0) o.ReadyUpMin = rd.Value / 60.0;
                        if (cd.HasValue && cd.Value >= 0) o.CoolDownMin = cd.Value / 60.0;
                        try { o.Available = (bool)_a.CallOpt(deck, AirOpsSpecs.DeckCanLaunch, lo, 1); } catch { o.Available = false; }
                        var am = Get(lo, AirOpsSpecs.LoAmmoAmounts) as IEnumerable;
                        if (am != null)
                        {
                            o.Weapons = new Dictionary<string, int>();
                            foreach (var kv in am)
                            {
                                string k = Get(kv, CoreSpecs.PairKey) as string; var n = GameApi.D(Get(kv, CoreSpecs.PairValue));
                                if (k != null && n.HasValue) o.Weapons[k] = (int)n.Value;
                            }
                        }
                        res.Add(o);
                        string tag = vehicleType + "/" + o.Name;
                        if (_capLogged.Add(tag)) Log("[AirOps] loadout " + tag + " capacity=" + o.Capacity + " source=" + src + " ready_min=" + o.ReadyUpMin + " cool_min=" + o.CoolDownMin);
                    }
                }
                catch { }
                return res;
            }

            private string StateOf(object task)
            {
                string st = null;
                try { st = Get(Get(task, AirOpsSpecs.TaskStateMachine), AirOpsSpecs.MachineState).GetType().Name; } catch { }
                switch (st)
                {
                    case "HandleAwaitDeckSpaceTask": return "await_deck";
                    case "HandleAwaitGroundCrewTask": return "await_crew";
                    case "HandleReadyUpTask": return "ready_up";
                    case "HandleAwaitSpawnTask": return "await_spawn";
                    case "HandleCoolDownTask": return "cooldown";
                    default: return st == null ? "ready_up" : "launching";
                }
            }

            internal string GameDecksJson(List<object> ships, int limit)
            {
                var sb = new System.Text.StringBuilder("[");
                int nShip = 0;
                foreach (var ship in ships)
                {
                    try
                    {
                        var deck = _a.DeckOf(ship);
                        if (deck == null) continue;
                        if (nShip++ > 0) sb.Append(',');
                        sb.Append("{\"ship_uid\":").Append(_a.UidOf(ship)).Append(",\"ship\":").Append(Json.Str(_a.NameOf(ship))).Append(",\"tasks\":[");
                        int n = 0;
                        var tasks = Get(deck, AirOpsSpecs.DeckTasks) as IEnumerable;
                        if (tasks != null)
                            foreach (var t in tasks)
                            {
                                if (t == null) continue;
                                if (limit > 0 && n >= limit) break;
                                string tn = t.GetType().Name;
                                var vt = Get(t, AirOpsSpecs.TaskVehicleType);
                                if (n++ > 0) sb.Append(',');
                                sb.Append("{\"kind\":").Append(Json.Str(tn)).Append(",\"type\":").Append(Json.Str(vt == null ? null : Get(vt, AirOpsSpecs.VtFileName) as string));
                                string key = "task:" + Get(t, AirOpsSpecs.TaskUid);
                                sb.Append(",\"key\":").Append(Json.Str(key)).Append(",\"ours\":").Append(Json.Bool(_ours.Contains(key)));
                                string st = null;
                                try { st = Get(Get(t, AirOpsSpecs.TaskStateMachine), AirOpsSpecs.MachineState).GetType().Name; } catch { }
                                sb.Append(",\"state\":").Append(Json.Str(st));
                                var dur = GameApi.D(Get(t, AirOpsSpecs.TaskDuration));
                                if (dur.HasValue) sb.Append(",\"duration\":").Append(Json.Num(Math.Round(dur.Value, 1)));
                                if (tn == "PendingLaunchTask")
                                {
                                    var al = Get(t, AirOpsSpecs.PendingLaunchAllowed);
                                    sb.Append(",\"allowed\":").Append(al is bool ? Json.Bool((bool)al) : "null");
                                }
                                var veh = Get(t, AirOpsSpecs.TaskVehicle);
                                if (veh != null)
                                {
                                    bool alive = GameApi.IsAlive(veh);
                                    sb.Append(",\"unit_uid\":").Append(alive ? _a.UidOf(veh).ToString(System.Globalization.CultureInfo.InvariantCulture) : "null").Append(",\"unit_alive\":").Append(Json.Bool(alive));
                                }
                                sb.Append('}');
                            }
                        sb.Append("],\"hangar\":{");
                        int h = 0;
                        var vs = Get(deck, AirOpsSpecs.DeckVehicles) as IEnumerable;
                        if (vs != null)
                            foreach (var v in vs)
                            {
                                var num = GameApi.D(Get(v, AirOpsSpecs.VtNumbers));
                                if (h++ > 0) sb.Append(',');
                                sb.Append(Json.Str(Get(v, AirOpsSpecs.VtFileName) as string ?? "?")).Append(':').Append(Json.Num(num ?? -1));
                            }
                        sb.Append("}}");
                    }
                    catch (Exception e) { sb.Append("{\"error\":").Append(Json.Str(e.GetType().Name)).Append('}'); }
                }
                return sb.Append(']').ToString();
            }

            public List<AirDeckTask> DeckTasks(int deckUid)
            {
                var res = new List<AirDeckTask>();
                var deck = Deck(deckUid);
                if (deck == null) return res;
                try
                {
                    var tasks = Get(deck, AirOpsSpecs.DeckTasks) as IEnumerable;
                    if (tasks == null) return res;
                    foreach (var t in tasks)
                    {
                        if (t == null) continue;
                        string tn = t.GetType().Name;
                        if (tn != "PendingLaunchTask" && tn != "CoolDownTask") continue;
                        var vt = Get(t, AirOpsSpecs.TaskVehicleType);
                        var d = new AirDeckTask { ShipUid = deckUid, Type = AirOpsMath.DeckTaskType(vt == null ? null : Get(vt, AirOpsSpecs.VtFileName) as string, tn == "CoolDownTask" ? Get(t, AirOpsSpecs.CoolIniRef) as string : null) };
                        var dur = GameApi.D(Get(t, AirOpsSpecs.TaskDuration));
                        if (tn == "PendingLaunchTask")
                        {
                            d.Key = "task:" + Get(t, AirOpsSpecs.TaskUid);
                            var idx = GameApi.D(Get(t, AirOpsSpecs.PendingLoadoutIndex));
                            var los = vt == null ? null : (Get(vt, AirOpsSpecs.VtLoadoutsField) as IList ?? Get(vt, AirOpsSpecs.VtLoadouts) as IList);
                            if (los != null && idx.HasValue && idx.Value >= 0 && idx.Value < los.Count) d.Loadout = Get(los[(int)idx.Value], AirOpsSpecs.LoName) as string;
                            d.State = StateOf(t);
                            var crew = GameApi.D(Get(t, AirOpsSpecs.PendingCrew));
                            d.RemainS = AirOpsMath.PendingRemainS(dur ?? 0, DeckTimingsMode(), crew.HasValue ? (int)crew.Value : 0);
                            d.Ours = _ours.Contains(d.Key);
                        }
                        else
                        {
                            d.Key = "cool:" + Get(t, AirOpsSpecs.TaskUid);
                            d.State = "cooldown";
                            // T-204 (Q2): the rest as AswGame.CoolRow reads it: _duration - (Now - _startTime) once the task has started
                            var st = GameApi.D(Get(t, AirOpsSpecs.TaskStartTime));
                            double rem = dur.HasValue && dur.Value > 0 && st.HasValue && st.Value > 0 ? dur.Value - ((_a.Frame.MissionTimeS ?? 0) - st.Value) : dur ?? 0;
                            d.RemainS = Math.Max(0, Math.Min(dur ?? 0, rem));
                        }
                        res.Add(d);
                    }
                }
                catch { }
                return res;
            }

            public string OrderLaunch(int deckUid, string vehicleType, string loadoutName, string missionType, bool allowLaunch, out string key, out double readyS)
            {
                key = null; readyS = 0;
                var deck = Deck(deckUid);
                if (deck == null) return "unit_not_found";
                try
                {
                    // T-231: a type can have several records on the deck (the hangar rows add Numbers of all of them): the order takes the first record that has
                    // the loadout and a squadron with a machine left
                    var recs = new List<object>(); var los = new List<object>(); var sqLists = new List<List<object>>(); var facts = new List<VtRecFacts>();
                    double vtSum = 0, sqSum = 0;
                    var vs = Get(deck, AirOpsSpecs.DeckVehicles) as IEnumerable;
                    if (vs != null)
                        foreach (var v in vs)
                        {
                            if (Get(v, AirOpsSpecs.VtFileName) as string != vehicleType) continue;
                            object loR = null;
                            var lis = Get(v, AirOpsSpecs.VtLoadouts) as IEnumerable ?? Get(v, AirOpsSpecs.VtLoadoutsField) as IEnumerable;
                            if (lis != null) foreach (var l in lis) if (Get(l, AirOpsSpecs.LoName) as string == loadoutName) { loR = l; break; }
                            var sl = new List<object>(); var nums = new List<int>();
                            var sqs0 = Get(v, AirOpsSpecs.VtSquadrons) as IEnumerable;
                            if (sqs0 != null) foreach (var s0 in sqs0) { sl.Add(s0); double nn = GameApi.D(Get(s0, AirOpsSpecs.SquadronNumbers)) ?? 0; nums.Add((int)nn); sqSum += nn; }
                            vtSum += GameApi.D(Get(v, AirOpsSpecs.VtNumbers)) ?? 0;
                            recs.Add(v); los.Add(loR); sqLists.Add(sl);
                            facts.Add(new VtRecFacts { HasLoadout = loR != null, SquadronNumbers = nums.ToArray() });
                        }
                    if (recs.Count == 0) return "unknown_loadout";
                    bool anyLoadout = false; foreach (var f in facts) if (f.HasLoadout) anyLoadout = true;
                    if (!anyLoadout) return "unknown_loadout";
                    int sqIdx;
                    int ri = AirOpsMath.PickLaunchRecord(facts, out sqIdx);
                    // 0.28.1: createLaunchTask takes the machine out of Squadron.Numbers; none left = the hangar of this type is empty
                    // (live: a second SH-2F order on Yorktown with both SH-2F already out failed as a bare "launch_failed" every 30 s)
                    if (ri < 0)
                    {
                        if (vtSum >= 1)
                        {
                            // T-231: the deck lists machines (VehicleTypeOnBoard.Numbers) but no squadron has one: the diagnostics of the "row is there, order says no_airframes" case
                            string dk = deckUid + "|" + vehicleType;
                            double nowS = _a.Frame.MissionTimeS ?? 0, lastS;
                            if (!_noAirLogAt.TryGetValue(dk, out lastS) || nowS - lastS >= 600 || nowS < lastS)
                            {
                                _noAirLogAt[dk] = nowS;
                                var parts = new List<string>(); foreach (var f in facts) parts.Add(string.Join("+", Array.ConvertAll(f.SquadronNumbers, n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                                Log("[Air] deck " + deckUid + " order " + vehicleType + " -> no_airframes records=" + recs.Count + " vt_numbers=" + vtSum.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " squadron_numbers=" + string.Join(",", parts.ToArray()));
                            }
                        }
                        return "no_airframes";
                    }
                    object vt = recs[ri], lo = los[ri], squadron = sqLists[ri][sqIdx];
                    string callsign = null;
                    var cs = Get(squadron, AirOpsSpecs.SquadronCallsigns) as IEnumerable;
                    if (cs != null) foreach (var c in cs) { callsign = c as string; break; }
                    if (string.IsNullOrEmpty(callsign)) return "no_callsign";
                    var ltpT = _a.TypeOf(AirOpsSpecs.LtpType);
                    object ltp = Activator.CreateInstance(ltpT);
                    _a.Set(ltp, AirOpsSpecs.LtpLaunchCount, 1);
                    var mt = _a.TypeOf(AirOpsSpecs.MissionTypeEnum);
                    string want = (missionType ?? "none").Trim();
                    object mv = Enum.ToObject(mt, 0);
                    foreach (var nm in Enum.GetNames(mt)) if (string.Equals(nm, want, StringComparison.OrdinalIgnoreCase)) mv = Enum.Parse(mt, nm);
                    _a.Set(ltp, AirOpsSpecs.LtpMissionType, mv);
                    object task = _a.CallOpt(deck, AirOpsSpecs.DeckCreateLaunch, vt, lo, squadron, callsign, ltp, allowLaunch);
                    if (task == null) return "no_ammo";
                    key = "task:" + Get(task, AirOpsSpecs.TaskUid);
                    _ours.Add(key);
                    var dur = GameApi.D(Get(task, AirOpsSpecs.TaskDuration));
                    readyS = dur.HasValue && dur.Value > 0 ? dur.Value : 0;
                    return null;
                }
                catch (Exception e) { return "launch_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            private object FindPending(object deck, string key)
            {
                var tasks = Get(deck, AirOpsSpecs.DeckTasks) as IEnumerable;
                if (tasks == null || key == null) return null;
                foreach (var t in tasks)
                    if (t != null && t.GetType().Name == "PendingLaunchTask" && ("task:" + Get(t, AirOpsSpecs.TaskUid)) == key) return t;
                return null;
            }

            public string AllowLaunch(int deckUid, string key)
            {
                var deck = Deck(deckUid);
                if (deck == null) return "deck_task_not_found";
                var task = FindPending(deck, key);
                if (task == null) return "deck_task_not_found";
                if (Get(task, AirOpsSpecs.PendingLaunchAllowed) is bool && (bool)Get(task, AirOpsSpecs.PendingLaunchAllowed)) return null;
                string st = null;
                try { st = Get(Get(task, AirOpsSpecs.TaskStateMachine), AirOpsSpecs.MachineState).GetType().Name; } catch { }
                try
                {
                    if (st == "HandleAwaitSpawnTask") _a.Call(task, AirOpsSpecs.PendingAllowLaunch, new object[] { null });
                    else _a.Call(task, AirOpsSpecs.PendingToggleLaunch, new object[] { null });
                }
                catch (Exception e) { return "launch_failed:" + (e.InnerException ?? e).GetType().Name; }
                return Get(task, AirOpsSpecs.PendingLaunchAllowed) is bool && (bool)Get(task, AirOpsSpecs.PendingLaunchAllowed) ? null : "launch_blocked";
            }

            public string Prioritise(int deckUid, string key)
            {
                if (key == null || !_ours.Contains(key)) return "not_ours";
                var deck = Deck(deckUid);
                if (deck == null) return "deck_task_not_found";
                var task = FindPending(deck, key);
                if (task == null) return "deck_task_not_found";
                string st = StateOf(task);
                if (st != "await_deck" && st != "await_crew" && st != "ready_up") return "bad_state";
                try
                {
                    var q = Get(deck, AirOpsSpecs.DeckPrioritise) as IList;
                    if (q == null) return "unsupported";
                    foreach (var t in q) if (ReferenceEquals(t, task)) return null;
                    q.Add(task);
                    return null;
                }
                catch (Exception e) { return "launch_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            public bool DeckInoperable(int deckUid)
            {
                var deck = Deck(deckUid);
                if (deck == null) return false;
                try { var v = _a.RxGet(deck, AirOpsSpecs.DeckInoperable); return v is bool && (bool)v; }
                catch { return false; }
            }

            public string DeckDump(int deckUid)
            {
                var deck = Deck(deckUid);
                if (deck == null) return "-";
                var sb = new System.Text.StringBuilder();
                try
                {
                    var tasks = Get(deck, AirOpsSpecs.DeckTasks) as IEnumerable;
                    if (tasks != null)
                        foreach (var t in tasks)
                        {
                            if (t == null) continue;
                            var vt = Get(t, AirOpsSpecs.TaskVehicleType);
                            string st = null;
                            try { st = Get(Get(t, AirOpsSpecs.TaskStateMachine), AirOpsSpecs.MachineState).GetType().Name; } catch { }
                            string key = "task:" + Get(t, AirOpsSpecs.TaskUid);
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(t.GetType().Name).Append(':').Append(vt == null ? "?" : Get(vt, AirOpsSpecs.VtFileName) as string ?? "?").Append(':')
                              .Append(st ?? "-").Append(':').Append(key).Append(_ours.Contains(key) ? "*" : "");
                        }
                }
                catch (Exception e) { sb.Append(" error:").Append(e.GetType().Name); }
                return sb.Length == 0 ? "-" : sb.ToString();
            }

            /// <summary>
            /// Live finding 38: a ready task the player put into a SAR group is handed over to the mod (it may be aborted for the re-arm to the
            /// SAR loadout). Not on IAirOps: only the SAR order asks, through AswController.DelegateDeckTask. True = the task exists and is ours now.
            /// </summary>
            public bool Delegate(int deckUid, string key)
            {
                if (key == null) return false;
                if (_ours.Contains(key)) return true;
                var deck = Deck(deckUid);
                if (deck == null || FindPending(deck, key) == null) return false;
                _ours.Add(key);
                return true;
            }

            public string AbortLaunch(int deckUid, string key)
            {
                if (key == null || !_ours.Contains(key)) return "not_ours";
                var deck = Deck(deckUid);
                if (deck == null) return "deck_task_not_found";
                var task = FindPending(deck, key);
                if (task == null) { _ours.Remove(key); return "deck_task_not_found"; }
                try
                {
                    var uid = Get(task, AirOpsSpecs.TaskUid);
                    _a.CallOpt(deck, AirOpsSpecs.DeckAbortLaunch, uid);   // trailing delayed=false filled in by the game's own default (0.8.7); 0.8.6 has no such parameter
                    _ours.Remove(key);
                    return null;
                }
                catch (Exception e) { return "launch_failed:" + (e.InnerException ?? e).GetType().Name; }
            }
        }
    }
}
