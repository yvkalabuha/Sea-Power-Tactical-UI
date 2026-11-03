using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed partial class AswController
    {
        /// <summary>0.27.10: the counts of the last raft read of the game (SarGame), set by GameApi.CreateAswController; null = none (stands).</summary>
        internal Func<SarRaftScan> RaftScanOf;

        /// <summary>
        /// Live finding 38: hands a ready deck task the player put into a SAR group over to the mod, so the deck broker may abort it for the re-arm
        /// to the SAR loadout (the game layer aborts only tasks the mod ordered). (deckUid, key) -> true = handed over. Null on the stands: the
        /// abort is then answered by the fake deck as it is.
        /// </summary>
        internal Func<int, string, bool> DelegateDeckTask;

        /// <summary>0.28.1: the back-off of a refused SAR deck order (stands).</summary>
        internal static double SarOrderBackoffS(string err, int fails) { return SarAirOrder.OrderBackoffS(err, fails); }

        /// <summary>0.30.1: the SAR group allowed this deck task and waits for its take-off (stands).</summary>
        internal bool SarAllowWaiting(string group, string deckKey) { return _sarAir.AllowWaiting(group, deckKey); }
        internal static double SarAllowBackoffS(int tries) { return SarAirOrder.AllowBackoffS(tries); }

        /// <summary>
        /// T-121 (T-109, ADR-0039): the order "sar" of the ASW/SAR groups - commands, the tick of a SAR group, the execution of the plan
        /// <c>SarMath.PlanSarRelief</c> (deck requests through <see cref="AirRelief"/>), returns of landed helicopters, the rafts of the view and
        /// the delayed load of the SAR part of the save. Nested in the controller: it shares the groups, the live rows and the helpers.
        /// </summary>
        private sealed class SarAirOrder
        {
            private readonly AswController _c;

            public SarAirOrder(AswController c) { _c = c; }

            /// <summary>The [Sar] settings (standby, tie-break of loadouts, mission type of the deck orders, ...).</summary>
            public SarParams Options = new SarParams();

            /// <summary>T-130: the SAR part of the save is loaded at once. The mission lifecycle clears SAR before ASW (subscriber order),
            /// so a mission-change reset cannot undo it any more.</summary>
            public void LoadNow(SarSaveData d) { if (d != null && _c.Sar != null) _c.Sar.Load(d, _c._now); }

            /// <summary>Mission change: nothing is kept here any more.</summary>
            public void Reset() { _idleRafts = null; ClearOrderBlocks(null); _noRearm.Clear(); _loadoutWhy.Clear(); _allowed.Clear(); }

            /// <summary>Rafts and rescue runs for the window and the overlay; only while some SAR work is on (the game is asked for rafts then).</summary>
            public void View(AswView v)
            {
                if (_c.Sar == null) return;
                bool any = ShipsActive();
                foreach (var g in _c._groups) if (g.Order == "sar") any = true;
                if (!any)
                {
                    // 0.27.10: no SAR work on - the rafts are not drawn, but the game is still asked every RaftIdleScanS so the window and the
                    // dev state ("raft_scan") show what is in the water before an order (the list used to be read only while a SAR order ran)
                    // T-192: the result of that read stays in the view (window, ui_state "rafts"); SarIdle keeps the overlay from drawing it
                    if (_c._now - _idleScanAt >= RaftIdleScanS || _c._now < _idleScanAt || _idleRafts == null) { _idleScanAt = _c._now; _idleRafts = RaftViews(_c._game.SarRafts()); }
                    ScanToView(v);
                    v.Rafts.AddRange(_idleRafts);
                    v.SarIdle = true;
                    return;
                }
                _idleScanAt = -1e9; _idleRafts = null;
                v.Rafts.AddRange(RaftViews(_c._game.SarRafts()));
                ScanToView(v);
                v.ShipsSarJson = _c.Sar.ShipsJson();
            }

            private List<AswRaftView> _idleRafts;

            /// <summary>The candidate rafts of a read as view rows (claims and the state of each).</summary>
            private List<AswRaftView> RaftViews(IList<SarRaft> rafts)
            {
                var res = new List<AswRaftView>();
                if (rafts == null) return res;
                var threats = _c._game.SarThreats();
                var p = Options;
                foreach (var r in rafts)
                {
                    string w;
                    if (!SarMath.Candidate(r, out w)) continue;
                    var rv = new AswRaftView { Id = r.Id, Type = r.Type, Lat = r.Lat, Lon = r.Lon, People = r.People };
                    foreach (var cl in _c.Sar.Registry.Claims) if (cl.RaftId == r.Id) rv.ClaimedBy.Add(cl.Rescuer);
                    rv.State = SarMath.UnderThreat(r, threats, p.ThreatStandoffNm) ? "threat"
                        : _c.Sar.Registry.Unreachable(r.Id, "air", _c._now) && _c.Sar.Registry.Unreachable(r.Id, "water", _c._now) ? "unreachable"
                        : _c.Sar.Registry.SeatsOn(r.Id) > 0 ? (_c.Sar.Registry.Covered(r) ? "covered" : "claimed") : "free";
                    res.Add(rv);
                }
                return res;
            }

            /// <summary>Mission seconds between two raft reads while no SAR order or ship run is on.</summary>
            public const double RaftIdleScanS = 10;
            private double _idleScanAt = -1e9;

            private void ScanToView(AswView v)
            {
                var f = _c.RaftScanOf;
                SarRaftScan s = null;
                try { s = f == null ? null : f(); } catch { }
                if (s == null) return;
                v.RaftScanJson = s.ToJson();
                v.RaftsInWater = s.List < 0 ? -1 : s.Candidates;
                v.PeopleInWater = s.People;
            }

            public static SarArea AreaOf(SarGroupOrderDto d)
            {
                if (d.AreaKind == "rect" && d.Rect != null && d.Rect.Length == 4) return new SarArea { Kind = "rect", Rect = d.Rect };
                if (d.AreaKind == "circle" && d.RadiusNm > 0) return new SarArea { Kind = "circle", Center = new GeoPt(d.Lat, d.Lon), RadiusNm = d.RadiusNm };
                return new SarArea();
            }

            public static SarGroupOrderDto OrderDto(Grp g)
            {
                var a = g.SarArea ?? new SarArea();
                var d = new SarGroupOrderDto { Group = g.Name, AreaKind = a.Kind, OnComplete = g.SarOnComplete };
                if (a.Kind == "rect") d.Rect = a.Rect;
                else if (a.Kind == "circle") { d.Lat = a.Center.Lat; d.Lon = a.Center.Lon; d.RadiusNm = a.RadiusNm; }
                return d;
            }

            private Dictionary<string, object> WithOrigin(Dictionary<string, object> c)
            {
                object ao;
                if (c == null || !c.TryGetValue("area", out ao)) return c;
                var am = ao as Dictionary<string, object>;
                if (am == null || !am.ContainsKey("bearing") || am.ContainsKey("origin") || c.ContainsKey("origin")) return c;
                int tu = 0; object tv;
                if (c.TryGetValue("target_uid", out tv) && tv is double) tu = (int)(double)tv;
                AswTarget t = tu != 0 ? _c._game.Target(tu) : _c._game.SelectedShip();
                if (t == null) return c;
                var copy = new Dictionary<string, object>(c);
                copy["origin"] = new Dictionary<string, object> { { "lat", t.Lat }, { "lon", t.Lon } };
                return copy;
            }

            /// <summary>sar_order: {group, area?, on_complete?}. The group takes the order "sar"; machines that cannot serve go back to "available" with a reason.</summary>
            public string Order(Dictionary<string, object> c)
            {
                string err;
                var cmd = SarCommands.ParseOrder(WithOrigin(c), out err);
                if (cmd == null) return Fail(err);
                if (_c.Sar == null) return Fail("unsupported", "sar controller is off");
                var g = _c.Find(cmd.Group);
                if (g == null) return Fail("no_group");
                // 0.30.1 (live 0.30.0: "sar_order {group=STRIKE-1} -> ok" turned a strike group into a rescue group): a strike group is stopped
                // or disbanded first; the rescue order takes new, ASW and SAR groups
                if (g.Order == "strike") return Fail("wrong_group_kind", "group " + g.Name + " has the order strike");
                if (g.Members.Count == 0) return Fail("no_aircraft");
                _c.RefreshLive();
                var fit = new List<int>();
                var unfit = new List<KeyValuePair<int, string>>();
                foreach (int u in g.Members)
                {
                    AswMember m;
                    if (!_c._live.TryGetValue(u, out m)) continue;
                    string why = AswMath.SarExclusionReason(m);
                    if (why == null) fit.Add(u); else unfit.Add(new KeyValuePair<int, string>(u, why));
                }
                if (fit.Count == 0) return Fail(unfit.Count > 0 ? unfit[0].Value : "no_aircraft");
                _c.ClearPreview();
                foreach (var kv in unfit)
                {
                    string name = g.Refs.ContainsKey(kv.Key) ? g.Refs[kv.Key].Name : I(kv.Key);
                    _c.RemoveMember(g, kv.Key, true);
                    _c.Warn(g, "removed_unfit", name + ": " + kv.Value, 0);
                }
                if (g.Order != "sar") foreach (int u in fit) _c.RestoreMemory(u);   // ROE and buoy depth set by a former ASW order
                g.Order = "sar"; g.SarArea = cmd.Area; g.SarOnComplete = cmd.OnComplete;
                g.TargetName = null; g.TargetUid = 0; g.TargetKind = "ship"; g.FormWidthNm = 0; g.Rect = null; g.Tactics.Clear();
                _c.ResetPlan(g);
                g.Prepared.Clear(); g.Contacts.Clear(); g.Leaving.Clear(); g.Hovering.Clear(); g.Status = "";
                _c.ReassignNow(g);
                _c.AutoRename(g);
                _c.RebuildView();
                _c._game.Log("[Sar] order " + g.Name + " area=" + cmd.Area.Kind + " on_complete=" + cmd.OnComplete + " members=" + I(g.Members.Count) + " dropped=" + I(unfit.Count));
                return Obj("group", g.Name, "area", cmd.Area.Kind, "on_complete", cmd.OnComplete, "members", g.Members.Count);
            }

            /// <summary>sar_ship: {uid, op: start|stop, radius_nm?} for a ship or submarine (the "Старпом" menu); the run itself is the SAR controller's.</summary>
            public string Ship(Dictionary<string, object> c)
            {
                string err;
                var cmd = SarCommands.ParseShip(c, out err);
                if (cmd == null) return Fail(err);
                if (_c.Sar == null) return Fail("unsupported", "sar controller is off");
                var norm = new Dictionary<string, object> { { "uid", (double)cmd.Uid }, { "op", cmd.Op } };
                if (cmd.Op == "start") norm["radius_nm"] = cmd.RadiusNm;
                string r = _c.Sar.ShipCmd(norm);
                _c.RebuildView();
                return r;
            }

            /// <summary>The order "sar" ends: claims are released, own deck orders are taken back; stop = also send the airborne home.</summary>
            public void Leave(Grp g, bool stop)
            {
                foreach (int u in new List<int>(g.Members))
                {
                    AswMember m;
                    if (!_c._live.TryGetValue(u, out m)) continue;
                    if (_c.Sar != null) _c.Sar.ReleaseUnit(u, "stopped");
                    if (stop && m.Where == "air" && !g.Leaving.ContainsKey(u)) _c._game.SarRtb(u);
                    if (IsTask(m) && g.OwnKeys.ContainsKey(m.DeckKey))
                    {
                        string dkey = m.DeckKey;
                        _c.SubmitDeck(g, "abort" + dkey, AirRelief.Abort(m.DeckUid, dkey, "sar " + (stop ? "stop" : "leave") + " " + g.Name),
                            res => _c._game.Log("[Sar] abort " + dkey + " -> " + (res.Err ?? "ok")));
                        _c.RemoveMember(g, u, false);
                    }
                }
                if (!stop)   // the group goes on as an ASW group: what cannot hunt submarines leaves it
                    foreach (int u in new List<int>(g.Members))
                    {
                        AswMember m;
                        if (!_c._live.TryGetValue(u, out m)) continue;
                        string why = AswMath.ExclusionReason(m);
                        if (why == null) continue;
                        string name = g.Refs.ContainsKey(u) ? g.Refs[u].Name : I(u);
                        _c.RemoveMember(g, u, true);
                        _c.Warn(g, "removed_unfit", name + ": " + why, 0);
                    }
                foreach (var k in new List<string>(_allowed.Keys)) if (k.StartsWith(g.Name + "|", StringComparison.Ordinal)) _allowed.Remove(k);
                g.OwnKeys.Clear(); g.OwnSeen.Clear(); g.Leaving.Clear(); g.Hovering.Clear(); g.Returns.Clear(); g.Targets.Clear(); g.LastOnboard.Clear();
                g.SarStatus = ""; g.NextLaunchS = -1;
                g.Dirty = true;
            }

            /// <summary>A member of a SAR group is gone from the lists: a helicopter that landed is expected back as a deck row; deck rows simply change.</summary>
            public void Vanished(Grp g, int uid)
            {
                AswMember was;
                _c._lastKnown.TryGetValue(uid, out was);
                string name = g.Refs.ContainsKey(uid) ? g.Refs[uid].Name : I(uid);
                if (_c.Sar != null) _c.Sar.ReleaseUnit(uid, "landed");
                if (was != null && was.Where == "air")
                {
                    g.Returns.Add(new SarReturn { Type = was.Type, Base = was.Base, Name = name, Expires = _c._now + 3600 });
                    _c._game.Log("[Sar] " + g.Name + " " + name + " left the air (landed), waiting for the deck row");
                }
                else if (was != null)   // live 0.28.7: a deck row leaves the group without a word otherwise (a hangar row whose last airframe an order took, a task the game dropped)
                    _c._game.Log("[Sar] " + g.Name + " " + name + " uid=" + I(uid) + " deck row gone: " + (was.DeckKey ?? "-") + " " + (was.Where ?? "-")
                        + (was.DeckKey != null && was.DeckKey.StartsWith("hangar:", StringComparison.Ordinal) ? " (hangar empty: its airframes were ordered or launched)" : ""));
                _c.RemoveMember(g, uid, false); _c._mem.Remove(uid);
                g.Leaving.Remove(uid); g.Hovering.Remove(uid); g.Targets.Remove(uid); g.LastOnboard.Remove(uid);
            }

            /// <summary>Landed helicopters come back as hangar rows of their type.</summary>
            private void ProcessReturns(Grp g)
            {
                foreach (var r in new List<SarReturn>(g.Returns))
                {
                    if (_c._now > r.Expires)
                    {
                        g.Returns.Remove(r);
                        _c.Warn(g, "member_lost", r.Name + ": left_air_landed_or_lost", 30);
                        continue;
                    }
                    // T-181 (ADR-0080 s.3): a landed helicopter takes a free hangar slot of its type (no "row already in the group" check any more)
                    var hang = _c.HangarRow(r.Type, 0, r.Base);
                    if (hang == null) continue;
                    _c.AddMember(g, hang);
                    if (!g.Members.Contains(hang.Uid)) continue;
                    g.Assign[hang.Uid] = "sar";
                    g.Returns.Remove(r);
                }
            }

            private static int CapOf(IList<LoadoutOption> los, string loadout, int fallback)
            {
                if (los != null && loadout != null) foreach (var l in los) if (l.Name == loadout) return l.Capacity;
                return fallback;
            }

            private SarHelo ToSarHelo(Grp g, AswMember m, Func<int, string, IList<LoadoutOption>> losOf, Func<int, string, ISet<string>> noAmmoOf, int deckUid, string mode)
            {
                // T-343: the loadouts are those of the member's OWN type on its deck, never the anchor's
                var los = IsDeckRow(m) && m.DeckUid != 0 ? losOf(m.DeckUid, m.Type) : null;
                string pw;
                int bi = los == null || los.Count == 0 ? -1 : SarMath.PickOwnSarLoadout(los, Options, noAmmoOf(m.DeckUid, m.Type), out pw);
                int bestCap = bi >= 0 ? los[bi].Capacity : 0;
                double orderPrepS = bi >= 0 ? AirOpsMath.ReadyS(los[bi], mode, 0) : -1;
                int capFb = los == null ? m.Capacity : bestCap;   // no loadout list (air / launching): the game's own seats
                bool task = IsTask(m);
                var h = new SarHelo
                {
                    Uid = m.Uid, DeckUid = IsDeckRow(m) ? m.DeckUid : 0, Onboard = m.Onboard, Type = m.Type, Loadout = m.Loadout, FuelFrac = m.FuelFrac,
                    EnduranceS = m.EnduranceS, TransitS = m.TransitS, ReadyInS = m.ReadyInS, BestCapacity = bestCap, Capacity = m.Capacity,
                    Ours = task && g.OwnKeys.ContainsKey(m.DeckKey)
                };
                switch (m.Where)
                {
                    case "air":
                        h.Where = "air"; h.HasTarget = _c.Sar.Registry.ClaimOf(m.Uid) != null;
                        break;
                    case "deck_launching":
                        h.Where = "air"; h.Capacity = CapOf(los, m.Loadout, capFb);
                        break;
                    case "deck_recover":
                        h.Where = "deck_recover"; h.Capacity = CapOf(los, m.Loadout, capFb);
                        break;
                    default:   // deck_ready | deck_prep | deck_service
                        h.Capacity = CapOf(los, m.Loadout, capFb);
                        if (!task)
                        {
                            h.Where = "landed";   // a vehicle in the hangar: nothing is being prepared yet
                            // T-226: a hangar row waits only for the preparation of the SAR order; a cool: row for its cool-down left plus that preparation
                            if (AswMath.IsHangarRow(m.DeckKey)) h.PrepS = orderPrepS >= 0 ? orderPrepS : m.HangarPrepS;
                            else if (m.DeckKey != null && m.DeckKey.StartsWith("cool:", StringComparison.Ordinal)) { h.PrepS = orderPrepS >= 0 ? orderPrepS : AswMath.NextPrepS(m); h.CoolLeftS = Math.Max(0, m.ReadyInS); }
                        }
                        else if (m.Where == "deck_ready")   // a ready task: no pending key, so the plan may launch it
                        {
                            h.Where = "deck_ready"; h.TaskKey = m.DeckKey;
                            // live finding 38 / T-343: a ready task of a deck row that has loadouts of its own type; a task the deck refused to abort stays as it is
                            h.CanRearm = IsDeckRow(m) && m.DeckUid != 0 && los != null && los.Count > 0 && !NoRearm(g, m.DeckKey);
                        }
                        else { h.Where = "deck_prep"; h.PendingKey = m.DeckKey; }
                        break;
                }
                return h;
            }

            /// <summary>T-175 (ADR-0075 item 2.4): a helicopter with people aboard lands on a deck in the SAM ring: the game flies it home like the "full" leave.</summary>
            public void AirLand(Grp g, AswMember m) { SarLeave(g, m, "air_threat"); }

            private void SarLeave(Grp g, AswMember m, string reason)
            {
                if (_c.Sar != null) _c.Sar.ReleaseUnit(m.Uid, reason);
                string err = _c._game.SarRtb(m.Uid);
                g.Leaving[m.Uid] = reason; g.Hovering.Remove(m.Uid); g.Targets.Remove(m.Uid);
                _c._game.Event("sar_leave", Obj("group", g.Name, "unit", m.Uid, "aircraft", m.Name, "reason", reason, "onboard", m.Onboard));
                _c._game.Log("[Sar] leave " + g.Name + " " + m.Name + " reason=" + reason + " onboard=" + I(m.Onboard) + " rtb -> " + (err ?? "ok"));
            }

            /// <summary>T-147 H: an empty helicopter of a "loiter" group waits over its own ship (it moves: checked every 5 s, repeated when farther than 0.5 nm);
            /// without a known ship it just stops where it is.</summary>
            private void SarHoverShip(Grp g, AswMember m)
            {
                int u = m.Uid;
                bool first = g.Hovering.Add(u);
                double last;
                if (!first && g.HoverCheckAt.TryGetValue(u, out last) && _c._now - last < 5) return;
                g.HoverCheckAt[u] = _c._now;
                var ship = string.IsNullOrEmpty(m.Base) ? null : _c._game.TargetByName(m.Base);
                if (ship == null)
                {
                    if (first) { string e = _c._game.SarHover(u); _c._game.Log("[Sar] hover " + g.Name + " " + m.Name + " -> " + (e ?? "ok")); }
                    return;
                }
                double d = AswMath.DistNm(new GeoPt(m.Lat, m.Lon), new GeoPt(ship.Lat, ship.Lon));
                if (!first && d <= 0.5) return;
                string err = _c._game.SarHoverOver(u, ship.Uid);
                _c._game.Log("[Sar] hover over " + ship.Name + " " + g.Name + " " + m.Name + " dist=" + N(Math.Round(d, 1)) + " -> " + (err ?? "ok"));
            }

            private void SarAir(Grp g, AswMember m)
            {
                int u = m.Uid;
                int last;
                if (g.LastOnboard.TryGetValue(u, out last) && m.Onboard > last) g.Rescued += m.Onboard - last;
                g.LastOnboard[u] = m.Onboard;
                if (g.Leaving.ContainsKey(u)) return;   // on the way home
                if (_c.AirRetreat(g, m)) return;        // T-175: stage 2 of the air threat: cover point (or the deck with people aboard); the rafts wait
                if (m.Evading) return;                  // T-318 (as ASW T-100): the evasion owns the route; the raft run and the takeover check wait for its end
                var sh = new SarHelo { Uid = u, Onboard = m.Onboard, Capacity = m.Capacity, FuelFrac = m.FuelFrac, EnduranceS = m.EnduranceS, TransitS = m.TransitS, Where = "air" };
                string why = SarMath.LeaveReason(sh, new SarDemand { NoRaftsSince = -1 }, _c._now, Options);   // full | fuel
                if (why != "fuel") { string tick = _c.Sar.HeloTick(u, g.SarArea, _c._now); why = tick ?? why; }   // HeloTick counts the people picked up, then "full" is reported
                if (why == "full" && m.Capacity > 0 && m.Onboard < m.Capacity) why = null;
                if (why == null) { g.Hovering.Remove(u); return; }
                if (why == "player_took")
                {
                    _c.RemoveMember(g, u, true); _c.Warn(g, "player_took", m.Name, 30);
                    return;
                }
                if (why == "no_rafts" && g.SarOnComplete == "loiter" && m.Onboard == 0)
                {
                    SarHoverShip(g, m);
                    return;
                }
                SarLeave(g, m, why);
            }

            // T-343: the loadouts of ONE type on ONE deck, cached 5 s per (deck, type) in the group
            private List<LoadoutOption> SarLoadoutsOf(Grp g, int deckUid, string type)
            {
                if (deckUid == 0 || type == null) return new List<LoadoutOption>();
                string k = SarMath.LosKey(deckUid, type);
                double at; List<LoadoutOption> los;
                if (!g.LosAtBy.TryGetValue(k, out at) || _c._now - at >= 5 || !g.LosBy.TryGetValue(k, out los) || los == null)
                {
                    los = _c._game.SarLoadouts(deckUid, type) ?? new List<LoadoutOption>();
                    g.LosBy[k] = los; g.LosAtBy[k] = _c._now;
                }
                return los;
            }

            public void TickGroup(Grp g)
            {
                if (_c.Sar == null) { g.SarStatus = "sar_off"; _c.Warn(g, "sar_off", null, 600); return; }
                var p = Options;
                ProcessReturns(g);
                ForgetAllowed(g);
                foreach (int u in new List<int>(g.Members))
                {
                    AswMember m;
                    if (_c._live.TryGetValue(u, out m) && m.Where == "air") SarAir(g, m);
                }
                var rafts = _c._game.SarRafts();
                var threats = _c._game.SarThreats();
                string mode = _c._game.DeckTimingsMode();
                int deckUid = 0; string deckType = null;
                foreach (int u in g.Members)
                {
                    AswMember m; if (!_c._live.TryGetValue(u, out m)) continue;   // waiting to be adopted
                    if (!IsDeckRow(m)) continue;
                    deckUid = m.DeckUid; deckType = m.Type; g.AnchorDeck = deckUid; g.AnchorType = deckType; g.AnchorLoadout = m.Loadout;
                    break;
                }
                bool anchored = deckUid != 0;
                if (!anchored) { deckUid = g.AnchorDeck; deckType = g.AnchorType; }
                foreach (var lo in new List<string>(g.NoAmmo.Keys)) if (_c._now - g.NoAmmo[lo] > 600) g.NoAmmo.Remove(lo);
                var noAmmoBy = new Dictionary<string, ISet<string>>();   // T-344: refusals per (deck, type), one set per tick
                Func<int, string, ISet<string>> noAmmoOf = (dk, ty) =>
                {
                    string nk = SarMath.LosKey(dk, ty);
                    ISet<string> ns;
                    if (!noAmmoBy.TryGetValue(nk, out ns)) noAmmoBy[nk] = ns = SarMath.NoAmmoOf(g.NoAmmo.Keys, dk, ty);
                    return ns;
                };
                // loadouts the game refused (no ammunition) are not taken for a while; one copy per (deck, type) and tick
                var planLos = new Dictionary<string, IList<LoadoutOption>>();
                Func<int, string, IList<LoadoutOption>> losOf = (dk, ty) =>
                {
                    string lk = SarMath.LosKey(dk, ty);
                    IList<LoadoutOption> pl;
                    if (planLos.TryGetValue(lk, out pl)) return pl;
                    var copy = new List<LoadoutOption>();
                    foreach (var lo in SarLoadoutsOf(g, dk, ty))
                        copy.Add(new LoadoutOption { Name = lo.Name, Display = lo.Display, Capacity = lo.Capacity, ReadyUpMin = lo.ReadyUpMin, CoolDownMin = lo.CoolDownMin, Roles = lo.Roles, Weapons = lo.Weapons, Available = lo.Available && !g.NoAmmo.ContainsKey(SarMath.NoAmmoKey(dk, ty, lo.Name)) });
                    planLos[lk] = copy;
                    return copy;
                };
                int anchorCap = 0;
                if (deckUid != 0 && deckType != null)
                {
                    string aw; var alos = losOf(deckUid, deckType);
                    int ai = SarMath.PickOwnSarLoadout(alos, p, noAmmoOf(deckUid, deckType), out aw);
                    anchorCap = ai >= 0 ? alos[ai].Capacity : 0;
                }

                var helos = new List<SarHelo>();
                int seatsAir = 0, seatsReserve = 0, active = 0;
                foreach (int u in g.Members)
                {
                    AswMember m; if (!_c._live.TryGetValue(u, out m)) continue;   // waiting to be adopted
                    var h = ToSarHelo(g, m, losOf, noAmmoOf, deckUid, mode);
                    NoOwnLoadoutLog(g, h, losOf, noAmmoOf);
                    helos.Add(h);
                    if (h.Where == "air" && !g.Leaving.ContainsKey(u) && !_c._airRet.ContainsKey(u))
                    {
                        if (h.HasTarget) active++; else seatsAir += Math.Max(0, h.Capacity - h.Onboard);
                    }
                    else if ((h.Where == "deck_ready" || h.Where == "deck_prep") && !_c.DeckOut(g, m.DeckUid)) seatsReserve += h.Capacity;   // T-231: a knocked-out deck is no reserve
                }
                foreach (var h in new List<SarHelo>(helos)) if (g.Leaving.ContainsKey(h.Uid) || _c._airRet.ContainsKey(h.Uid)) helos.Remove(h);   // heading home: not a rescuer any more
                if (!anchored && deckUid != 0)   // all machines are in the air: the remembered deck still takes orders
                    helos.Add(new SarHelo { Uid = int.MinValue, DeckUid = deckUid, Type = deckType, Loadout = g.AnchorLoadout, Where = "landed", BestCapacity = anchorCap });
                // uncovered rafts of the area
                int unc = 0, people = 0;
                g.AreaRafts = 0; g.AreaPeople = 0;
                foreach (var r in rafts)
                {
                    string w;
                    if (!SarMath.Candidate(r, out w)) continue;
                    if (!g.SarArea.Contains(new GeoPt(r.Lat, r.Lon))) continue;
                    g.AreaRafts++; g.AreaPeople += r.People;
                    if (_c.Sar.Registry.Covered(r) || _c.Sar.Registry.Unreachable(r.Id, "air", _c._now)) continue;
                    if (SarMath.UnderThreat(r, threats, p.ThreatStandoffNm)) continue;
                    unc++; people += Math.Max(0, r.People - _c.Sar.Registry.SeatsOn(r.Id));
                }
                g.UncoveredRafts = unc; g.UncoveredPeople = people; g.Active = active;
                // where each helicopter is going (window columns)
                g.Targets.Clear();
                foreach (var cl in _c.Sar.Registry.Claims)
                {
                    if (!g.Members.Contains(cl.Rescuer)) continue;
                    AswMember m = _c._live[cl.Rescuer];
                    foreach (var r in rafts) if (r.Id == cl.RaftId) { g.Targets[cl.Rescuer] = new[] { (double)r.Id, AswMath.DistNm(new GeoPt(m.Lat, m.Lon), new GeoPt(r.Lat, r.Lon)) }; break; }
                }
                var demand = new SarDemand { UncoveredRafts = unc, People = people, SeatsAir = seatsAir, SeatsReserve = seatsReserve, Loiter = g.SarOnComplete == "loiter" };
                var plan = SarMath.PlanSarRelief(helos, demand, losOf, noAmmoOf, mode, _c._now, p);
                g.NextLaunchS = SarMath.NextLaunchS(helos, losOf, mode);
                g.SarStatus = active > 0 || unc > 0 ? "searching" : "idle_no_rafts";
                foreach (var a in plan) SarExecute(g, a, p);
            }

            private void SarExecute(Grp g, SarAction a, SarParams p)
            {
                switch (a.Kind)
                {
                    case "launch":
                    {
                        AswMember m;
                        if (!_c._live.TryGetValue(a.Uid, out m)) break;
                        if (_c.DeckOut(g, m.DeckUid)) break;   // T-231: a knocked-out deck takes no orders
                        string key = "sarlaunch" + a.Uid;
                        if (_c._relief.Paused(key, _c._now, 120)) break;
                        if (_c._relief.InFlight(key)) break;
                        string route = AswMath.LaunchRoute(m) ?? "create";
                        bool blocked = _c.AirLaunchBlocked(g);   // T-175: stage 2 of the air threat: prepare, but no take-off
                        if (blocked && route == "allow") break;  // a prepared task stays prepared; "allow" goes out right after the threat
                        string akey = g.Name + "|" + m.DeckKey;
                        if (route == "allow" && !AllowDue(akey)) break;   // 0.30.1: allowed already, waiting for its turn on the deck
                        _c._relief.Mark(key, _c._now);
                        LoadoutLog(g, a, "launch");
                        var req = route == "allow" ? AirRelief.Allow(m.DeckUid, m.DeckKey, "sar launch " + g.Name) : AirRelief.Order(m.DeckUid, m.Type, a.Loadout ?? m.Loadout, p.LaunchMissionType, !blocked);
                        int auid = a.Uid;
                        _c.SubmitDeck(g, key, req, res =>
                        {
                            string err = res.Err;
                            AllowRec prevA;
                            _allowed.TryGetValue(akey, out prevA);
                            _c._game.Log("[Sar] launch " + g.Name + " uid=" + I(m.Uid) + " key=" + (m.DeckKey ?? res.Key ?? "-") + " route=" + route + " -> " + (err ?? "ok") + AirRelief.HeldTag(res)
                                + (route == "allow" && err == null && prevA != null ? " (again after " + N(Math.Round(_c._now - prevA.At)) + " s on deck)" : ""));
                            if (err == "deck_inoperable") { _c.DeckOut(g, m.DeckUid); return; }   // T-231: knocked out between the check and the order
                            if (err != null) { _c.Warn(g, "launch_failed", err, 120); return; }
                            _c.ClearWarn(g, "launch_failed");
                            if (route == "allow") NoteAllowed(akey, prevA);
                            if (route == "create" && res.Key != null && res.Key != m.DeckKey && g.Slots.ContainsKey(auid))
                            {
                                _c.HandOver(g, auid, res.Key, "sar", !blocked);   // T-181: the slot member becomes the order (ADR-0080 s.3)
                            }
                            else if (route == "create" && res.Key != null && res.Key != m.DeckKey)
                            {
                                g.OwnKeys[res.Key] = _c._now;
                                g.OwnInfo[res.Key] = new OwnRec { Task = "sar", Type = m.Type, DeckUid = m.DeckUid };
                            }
                            else
                            {
                                g.Assign[auid] = "sar";
                                var a0 = _c.StartAdopt(g, auid, m, "sar", "launch");
                                a0.Known = new HashSet<int>(); foreach (var l in _c._live.Values) if (l.Where == "air") a0.Known.Add(l.Uid);
                            }
                            _c._game.Event("asw_relief", Obj("group", g.Name, "kind", "launch", "uid", auid, "aircraft", m.Name, "task", "sar"));
                        });
                        break;
                    }
                    case "order":
                    {
                        string key = "sarorder" + a.DeckUid;
                        if (_c.DeckOut(g, a.DeckUid)) break;   // T-231
                        if (_c._relief.Paused(key, _c._now, 30)) break;   // a failed order is not repeated every second
                        // 0.28.1: a deck that refused the order is not asked again until its situation changes (a machine of the group left the deck,
                        // landed, finished preparing ...) or the back-off ran out; the refusal is logged once per reason
                        string sig = DeckSig(g, a.DeckUid);
                        string bkey = g.Name + "|" + a.DeckUid;
                        OrderBlock ob;
                        if (_orderBlock.TryGetValue(bkey, out ob))
                        {
                            if (ob.Sig == sig && _c._now - ob.At < ob.WaitS && _c._now >= ob.At) { ob.Skipped++; break; }
                            _c._game.Log("[Sar] order " + g.Name + " deck=" + I(a.DeckUid) + " retry after " + ob.Reason + " (skipped " + ob.Skipped + ", "
                                + (ob.Sig != sig ? "deck changed" : "waited " + N(Math.Round(ob.WaitS)) + " s") + ")");
                            ob.Skipped = 0; ob.Sig = sig; ob.At = _c._now;   // one retry per change: the answer comes later through the deck broker
                        }
                        var oreq = AirRelief.Order(a.DeckUid, a.Type, a.Loadout, p.LaunchMissionType, false);
                        var oa = a;
                        _c.SubmitDeck(g, key, oreq, res =>
                        {
                            string err = res.Err, nk = res.Key;
                            OrderBlock prev;
                            _orderBlock.TryGetValue(bkey, out prev);
                            if (err == null)
                            {
                                _orderBlock.Remove(bkey);
                                _c.ClearWarn(g, "no_reserve_deck"); _c.ClearWarn(g, "launch_failed");   // T-231: the order went out, the old refusals are over
                                _c._game.Log("[Sar] order " + g.Name + " deck=" + I(oa.DeckUid) + " " + oa.Type + "/" + oa.Loadout + " mission=" + p.LaunchMissionType + " -> ok key=" + nk + " ready_s=" + N(Math.Round(res.ReadyS)) + (oa.Why == "own_empty" || oa.Why == "own_any" ? " why=" + oa.Why : ""));
                            }
                            else if (err != "no_ammo")   // no_ammo: the next best loadout is taken at once (g.NoAmmo), no deck back-off
                            {
                                var nb = new OrderBlock { Reason = err, At = _c._now, Sig = DeckSig(g, oa.DeckUid), Fails = prev == null ? 1 : prev.Fails + 1 };
                                nb.WaitS = OrderBackoffS(err, nb.Fails);
                                _orderBlock[bkey] = nb;
                                if (prev == null || prev.Reason != err)
                                    _c._game.Log("[Sar] order " + g.Name + " deck=" + I(oa.DeckUid) + " " + oa.Type + "/" + oa.Loadout + " mission=" + p.LaunchMissionType + " -> " + err
                                        + " (" + OrderErrText(err) + "; next try when the deck changes or in " + N(Math.Round(nb.WaitS)) + " s)");
                            }
                            if (err == "no_ammo") { g.NoAmmo[SarMath.NoAmmoKey(oa.DeckUid, oa.Type, oa.Loadout)] = _c._now; _c.Warn(g, "no_ammo", oa.Loadout, 300); return; }
                            if (err == "no_airframes") { _c._relief.Mark(key, _c._now); _c.Warn(g, "no_reserve_deck", "deck " + I(oa.DeckUid) + ": no " + oa.Type + " left in the hangar", 600); return; }
                            if (err == "deck_inoperable") { _c._relief.Mark(key, _c._now); _c.DeckOut(g, oa.DeckUid); return; }   // T-231
                            if (err != null) { _c._relief.Mark(key, _c._now); _c.Warn(g, "launch_failed", err, 120); return; }
                            int slotUid = _c.GroupSlotOf(g, oa.DeckUid, oa.Type);   // T-181: the order takes a machine out of the hangar: one of the group's slots of that type becomes the order
                            if (slotUid != 0) _c.HandOver(g, slotUid, nk, "sar", false);
                            else
                            {
                                g.OwnKeys[nk] = _c._now;
                                g.OwnInfo[nk] = new OwnRec { Task = "sar", Type = oa.Type, DeckUid = oa.DeckUid };
                            }
                            _c._game.Event("asw_relief", Obj("group", g.Name, "kind", "prepare", "uid", 0, "aircraft", oa.Type, "task", "sar"));
                        });
                        break;
                    }
                    case "abort":
                    {
                        var aa = a;
                        _c.SubmitDeck(g, "abort" + a.Key, AirRelief.Abort(a.DeckUid, a.Key, "sar plan " + g.Name), res =>
                        {
                            string err = res.Err;
                            _c._game.Log("[Sar] abort " + g.Name + " " + aa.Key + " -> " + (err ?? "ok"));
                            if (err == null || err == "deck_task_not_found") { g.OwnKeys.Remove(aa.Key); g.OwnSeen.Remove(aa.Key); _c.RemoveMember(g, aa.Uid, false); }
                        });
                        break;
                    }
                    case "rearm":
                        Rearm(g, a, p);
                        break;
                    case "hold":
                        LoadoutLog(g, a, "hold");
                        break;
                    case "warn":
                        if (a.Code == "no_capacity") g.SarStatus = "no_capacity";
                        _c.Warn(g, a.Code, null, 600);
                        break;
                }
            }

            // ---- live finding 38: the SAR loadout of a ready helicopter (re-arm on deck) ----

            private readonly Dictionary<string, double> _noRearm = new Dictionary<string, double>();     // group|task key -> time the deck refused the abort
            private readonly Dictionary<string, string> _loadoutWhy = new Dictionary<string, string>();  // group|uid -> the last logged decision
            public const double NoRearmS = 600;

            private bool NoRearm(Grp g, string key)
            {
                double at;
                if (key == null || !_noRearm.TryGetValue(g.Name + "|" + key, out at)) return false;
                if (_c._now - at <= NoRearmS && _c._now >= at) return true;
                _noRearm.Remove(g.Name + "|" + key);
                return false;
            }

            /// <summary>T-343: a deck type with no loadout with seats of its own gets no order; one line per (group, deck, type).</summary>
            private void NoOwnLoadoutLog(Grp g, SarHelo h, Func<int, string, IList<LoadoutOption>> losOf, Func<int, string, ISet<string>> noAmmoOf)
            {
                if (h.DeckUid == 0 || h.Type == null) return;
                string w;
                if (SarMath.PickOwnSarLoadout(losOf(h.DeckUid, h.Type), Options, noAmmoOf(h.DeckUid, h.Type), out w) >= 0) return;
                string k = g.Name + "|" + h.DeckUid + "|" + h.Type;
                if (_loadoutWhy.ContainsKey(k)) return;
                _loadoutWhy[k] = "no_own_loadout";
                _c._game.Log("[Sar] loadout " + g.Name + " deck=" + I(h.DeckUid) + " " + h.Type + " -> no_own_loadout");
            }

            /// <summary>One [Sar] loadout line per helicopter and decision (a repeated hold or launch of the same machine is not logged again).</summary>
            private void LoadoutLog(Grp g, SarAction a, string act)
            {
                if (a.Why == null) return;
                if (act == "launch" && a.Why == "best_loadout" && !Options.Debug) return;   // the usual case: no line
                string k = g.Name + "|" + a.Uid, v = act + ":" + a.Why + ":" + a.Loadout;
                string last;
                if (_loadoutWhy.TryGetValue(k, out last) && last == v) return;
                _loadoutWhy[k] = v;
                string best = act == "rearm" ? a.Loadout : a.BestLoadout;
                string cur = act == "rearm" ? a.FromLoadout : a.Loadout;
                _c._game.Log("[Sar] loadout " + g.Name + " uid=" + I(a.Uid) + " " + (a.Type ?? "?") + " loadout=" + (cur ?? "?") + " seats=" + I(a.Seats)
                    + " best=" + (best ?? "-") + "/" + I(a.BestSeats) + " people=" + I(a.People) + " prep_s=" + N(Math.Round(a.PrepS)) + " -> " + act + " why=" + a.Why);
            }

            /// <summary>Abort the ready task (the airframe goes back to the hangar, its ammunition too) and order the same type with the SAR loadout.
            /// A deck that refuses the abort (a task of the player or the game AI that was not handed over) keeps it: the plan then takes it as is.</summary>
            private void Rearm(Grp g, SarAction a, SarParams p)
            {
                AswMember m;
                if (a.Key == null || !_c._live.TryGetValue(a.Uid, out m) || m.DeckKey != a.Key) return;
                string key = "sarrearm" + a.Uid;
                if (_c._relief.Paused(key, _c._now, 60) || _c._relief.InFlight(key)) return;
                _c._relief.Mark(key, _c._now);
                LoadoutLog(g, a, "rearm");
                bool handed = false;
                var dl = _c.DelegateDeckTask;
                if (dl != null) { try { handed = dl(a.DeckUid, a.Key); } catch { handed = false; } }
                var aa = a;
                string nk = g.Name + "|" + a.Key;
                bool sent = _c.SubmitDeck(g, key, AirRelief.Abort(a.DeckUid, a.Key, "sar rearm " + g.Name + " " + a.FromLoadout + "->" + a.Loadout), res =>
                {
                    string err = res.Err;
                    _c._game.Log("[Sar] rearm " + g.Name + " uid=" + I(aa.Uid) + " key=" + aa.Key + " " + aa.Type + " " + aa.FromLoadout + "->" + aa.Loadout
                        + (handed ? " handed_over" : "") + " abort -> " + (err ?? "ok") + (err != null && err != "deck_task_not_found" ? " (goes as is)" : ""));
                    if (err == "deck_task_not_found") return;   // launched or gone meanwhile
                    if (err != null) { _noRearm[nk] = _c._now; return; }
                    g.OwnKeys.Remove(aa.Key); g.OwnSeen.Remove(aa.Key); g.OwnInfo.Remove(aa.Key);
                    _c.RemoveMember(g, aa.Uid, false);
                    SarExecute(g, new SarAction { Kind = "order", DeckUid = aa.DeckUid, Type = aa.Type, Loadout = aa.Loadout, EtaS = aa.EtaS }, p);
                });
                if (!sent) _c._relief.Mark(key, _c._now - 50);   // no broker: try again in 10 s
            }

            // ---- 0.30.1: one "allow" per prepared deck task ----

            // Live 0.30.0: "launch SAR-2 uid=-1830039994 key=task:52562023... route=allow -> ok" four times in two minutes. An allowed task stays a
            // deck_ready row until the deck has a free spot for it (one helicopter after the other; the broker may also hold it for a CAP); the plan
            // still sees a ready helicopter and the 120 s pause of the launch key let the same allow out again (a held allow sent again is released
            // at once, against the CAP yield). Now an allowed task is not asked again before AllowWaitS (doubling up to AllowWaitMaxS).

            private sealed class AllowRec { public double At; public int Tries; }
            private readonly Dictionary<string, AllowRec> _allowed = new Dictionary<string, AllowRec>();   // group|task key -> the last allow that was taken
            public const double AllowWaitS = 300, AllowWaitMaxS = 1200;

            public static double AllowBackoffS(int tries)
            {
                double w = AllowWaitS;
                for (int i = 1; i < tries && w < AllowWaitMaxS; i++) w *= 2;
                return Math.Min(AllowWaitMaxS, w);
            }

            private bool AllowDue(string akey)
            {
                AllowRec a;
                if (!_allowed.TryGetValue(akey, out a)) return true;
                return _c._now < a.At || _c._now - a.At >= AllowBackoffS(a.Tries);
            }

            private void NoteAllowed(string akey, AllowRec prev)
            {
                _allowed[akey] = new AllowRec { At = _c._now, Tries = prev == null ? 1 : prev.Tries + 1 };
            }

            /// <summary>Tasks that left the group's deck rows (launched, aborted, gone) are forgotten.</summary>
            private void ForgetAllowed(Grp g)
            {
                if (_allowed.Count == 0) return;
                var keys = new HashSet<string>();
                foreach (int u in g.Members)
                {
                    AswMember m;
                    if (_c._live.TryGetValue(u, out m) && m.DeckKey != null) keys.Add(g.Name + "|" + m.DeckKey);
                }
                foreach (var k in new List<string>(_allowed.Keys))
                    if (k.StartsWith(g.Name + "|", StringComparison.Ordinal) && !keys.Contains(k)) _allowed.Remove(k);
            }

            /// <summary>Test access: the task was allowed and waits for its turn (no new allow now).</summary>
            internal bool AllowWaiting(string group, string deckKey) { return !AllowDue(group + "|" + deckKey); }

            // ---- 0.28.1: back-off of refused deck orders ----

            private sealed class OrderBlock { public string Reason, Sig; public double At, WaitS; public int Fails, Skipped; }
            private readonly Dictionary<string, OrderBlock> _orderBlock = new Dictionary<string, OrderBlock>();

            /// <summary>Seconds before a refused order is tried again on an unchanged deck: an empty hangar waits long (a machine has to come back),
            /// any other refusal doubles from 60 s up to 600 s.</summary>
            public static double OrderBackoffS(string err, int fails) { return AirRelief.OrderBackoffS(err, fails); }

            public static string OrderErrText(string err) { return AirRelief.OrderErrText(err); }

            /// <summary>What the group has on that deck (uid, place, task): a change lets a refused order be tried again at once.</summary>
            private string DeckSig(Grp g, int deckUid)
            {
                var sb = new StringBuilder();
                foreach (int u in g.Members)
                {
                    AswMember m;
                    if (!_c._live.TryGetValue(u, out m)) continue;
                    if (m.DeckUid != deckUid && m.Where != "air") continue;
                    sb.Append(u).Append(':').Append(m.Where).Append(':').Append(m.DeckKey).Append(';');
                }
                // T-231: the free hangar rows of the deck: a machine that comes back changes the signature (the refused order is tried at once)
                var hang = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var c in _c._live.Values)
                    if (KeyIs(c, "hangar:") && c.DeckUid == deckUid) hang[c.Type ?? ""] = c.DeckCount;
                foreach (var kv in hang) sb.Append("h:").Append(kv.Key).Append('=').Append(kv.Value).Append(';');
                return sb.ToString();
            }

            /// <summary>Mission change / group gone: the back-off is forgotten.</summary>
            public void ClearOrderBlocks(string group)
            {
                foreach (var k in new List<string>(_orderBlock.Keys)) if (group == null || k.StartsWith(group + "|", StringComparison.Ordinal)) _orderBlock.Remove(k);
            }

            public bool ShipsActive()
            {
                if (_c.Sar == null) return false;
                return _c.Sar.ActiveShips > 0;
            }

            /// <summary>T-126: a ship on a rescue run keeps the controller out of idle.</summary>
            public bool HasWork() { return ShipsActive(); }

        }
    }
}
