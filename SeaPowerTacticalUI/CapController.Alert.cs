using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075 items 5 and 8 (air-only ROE, loadouts) and T-179 / ADR-0079: the "Alert" group kind of the CAP controller.
    internal sealed partial class CapController
    {
        private const double ReturnFarS = 120, GuideEveryS = 10, HandoverExtraNm = 5, DefaultAamNm = 30, BarrageNm = 10;

        /// <summary>The air threat service gets the reaction of the alert groups here (ADR-0079 section 7); set by AirGuardFeature.</summary>
        internal IAirAlertNotes AlertNotes;
        /// <summary>[AirGuard] UnknownMinKn, read live (set by AirGuardFeature); null = the CapParams copy.</summary>
        internal Func<double> UnknownMinKn;

        // ================================================= alert group (ADR-0079) =====================================================
        /// <summary>Stop / disband / switch away from the alert kind: the designation goes, airborne birds are sent home, the lent ones stay with their patrol until they land.</summary>
        private void ReleaseAlertGroup(Grp g, bool rtb)
        {
            foreach (var m in new List<Mem>(g.Members))
            {
                bool wasAlert = m.Alert;
                m.Alert = false; m.AlertState = null; m.AlertRaid = null; m.RaidIds = null; m.Handed = false; m.Barrage = false; m.FarSince = -1;
                m.AlertKey = null; m.Tgt = null; m.BindRestoredAt = -1;   // T-219
                if (m.AutoFill && !m.Airborne) { Log("alert unfill uid=" + m.Uid + " group=" + g.Name + " (release)"); DropMember(g, m, false, "unfill"); continue; }
                if (rtb && wasAlert && m.Airborne && m.Leaving == null)
                {
                    RestoreThrottle(m); m.AlertThrottle = -1;
                    try { _g.Rtb(m.Uid); } catch (Exception) { }
                    m.Leaving = "alert_off"; m.LastRtb = _now; m.RtbErr = null; m.Recovered = false;
                    if (g.P.RtbRoeHold) RoeHold(m, "rtb");
                }
            }
            OrphanLent(g);
            g.Served.Clear(); g.ServedRaids.Clear(); g.RaidSeen.Clear(); g.AlertWarned.Clear(); g.Memos.Clear();   // T-219: memos too
            SetAlertPrio(g, false);
            Evt("cap_alert", "{\"group\":" + Q(g.Name) + ",\"op\":\"release\",\"uids\":[],\"raid\":\"\"}");
        }

        private GeoPt? AlertRefPoint(Grp g)
        {
            if (g.A != null && g.A.RefUid != 0) { var p = _g.UnitPos(g.A.RefUid); if (p.HasValue) return p; }
            foreach (var m in g.Members) if (m.DeckUid > 0) { var p = _g.UnitPos(m.DeckUid); if (p.HasValue) return p; }
            return null;
        }

        private static bool Standing(Mem m) { return !m.Airborne && (m.Where == "hangar" || m.Where == "deck_prep" || m.Where == "deck_ready"); }

        /// <summary>Members that stand (hangar / deck) and are not busy with a raid.</summary>
        private static bool AlertFree(Mem m) { return m.Alert && m.LentFrom == null && Standing(m) && m.AlertState == null && !m.Pending; }

        /// <summary>Who can go now: the prepared ones (level deck) or any standing one up to "Ready" (level hangar), best first.</summary>
        private List<Mem> ScramblePool(Grp g)
        {
            var a = g.A; var pool = new List<Mem>(); var rl = new List<AlertReady>(); var byUid = new Dictionary<int, Mem>();
            foreach (var m in g.Members)
            {
                if (!AlertFree(m) || m.Where == "deck_down") continue;
                bool prepared = m.Key != null && !m.Allowed && (m.Where == "deck_ready" || m.Where == "deck_prep");
                if (a.Level == "deck" ? !prepared : (m.Key != null && !prepared)) continue;
                byUid[m.Uid] = m;
                rl.Add(new AlertReady { Uid = m.Uid, DeckReady = m.Where == "deck_ready" && m.Key != null, ReadyInS = m.Where == "hangar" || m.Key == null ? 1e6 + m.ReadyInS : m.ReadyInS });
            }
            var order = CapAlertMath.PickScramble(rl, 0);
            if (a.Level == "hangar" && order.Count > a.Ready) order.RemoveRange(a.Ready, order.Count - a.Ready);
            foreach (int u in order) pool.Add(byUid[u]);
            return pool;
        }

        private static bool AnyUnlaunched(Grp g)
        {
            foreach (var m in g.Members) if (m.AlertState == "scramble" && !m.Airborne) return true;
            return false;
        }

        /// <summary>Deck tier "air defence under a threat" (ADR-0041/0042): while a scrambled machine has not left the deck.</summary>
        private void SetAlertPrio(Grp g, bool on)
        {
            if (g.Prio == on) return;
            g.Prio = on;
            Log("deck priority " + (on ? "on" : "off") + " group=" + g.Name + (on ? " alert" : ""));
            Evt("cap_priority", "{\"group\":" + Q(g.Name) + ",\"on\":" + Json.Bool(on) + ",\"threat_uid\":0,\"threat_nm\":0}");
        }

        private void NoteAlert(Grp g, IList<int> ids, string action, int n, string label)
        {
            var notes = AlertNotes;
            if (notes == null || ids == null) return;
            try { notes.AlertLine(g.Name, ids, action, n, label); } catch (Exception) { }
        }

        /// <summary>Per group tick of an alert group: fill, the standing orders (level deck: Ready prepared, launch not allowed), scramble on a raid.</summary>
        private void AlertTick(Grp g, Dictionary<int, CapBird> birds, IList<CapContact> contacts, double now)
        {
            var a = g.A;
            if (!g.Ordered || a == null) { SetAlertPrio(g, false); return; }
            if (a.Fill) FillTick(g, birds); else Unfill(g, "fill_off");
            int have = g.Members.Count;
            if (have < a.Ready) { if (g.AlertWarned.Add("not_ready_members")) Warn(g, "alert_not_ready", null, "\"have\":" + have + ",\"want\":" + a.Ready); }
            else { g.AlertWarned.Remove("not_ready_members"); ClearWarn(g, "alert_not_ready"); }
            bool down = false; int prepared = 0;
            foreach (var m in g.Members)
            {
                if (m.Where == "deck_down") { down = true; continue; }
                if (!m.Airborne && m.LentFrom == null && m.AlertState == null && ((m.Key != null && !m.Allowed) || m.Pending) && Standing(m)) prepared++;
            }
            if (a.Level == "deck")
            {
                var cand = new List<Mem>();
                foreach (var m in g.Members)
                {
                    if (!AlertFree(m) || m.Key != null || m.Where == "deck_cool" || m.Where == "deck_down") continue;
                    cand.Add(m);
                }
                cand.Sort((x, y) => (x.Where == "hangar" ? 0 : x.Where == "deck_prep" ? 1 : 2).CompareTo(y.Where == "hangar" ? 0 : y.Where == "deck_prep" ? 1 : 2));
                for (int i = 0; i < cand.Count && prepared < a.Ready; i++) { OrderAlert(g, cand[i]); prepared++; }
            }
            else
            {
                foreach (var m in g.Members)
                    if (AlertFree(m) && m.Key != null && !m.Allowed && _ourKeys.Contains(m.Key)) AbortKey(m, "alert_hangar");   // T-187: a foreign ready task is kept   // level hangar: nothing stands prepared on the deck
            }
            if (down) Warn(g, "alert_unavailable"); else ClearWarn(g, "alert_unavailable");
            foreach (var sm in new List<Mem>(g.Members)) ResetStaleBind(g, sm, now);   // T-219: a restored binding that did not launch returns to the pool
            UpdateMemos(g, contacts, now);   // T-219
            // a raid within the radius of the protected ship sends the next ready machines
            var rp = AlertRefPoint(g);
            if (rp.HasValue)
                foreach (var raid in RaidsOf(g, contacts, now))
                {
                    foreach (int id in raid.Ids) if (!g.RaidSeen.ContainsKey(id)) g.RaidSeen[id] = now;
                    string key;
                    var notes = AlertNotes;
                    Func<int, string> keyOf = AlertKeyOf();   // T-201: AirGuard off = no keys, no 3 s wait
                    // T-197: one raid = one reaction; the raid key of the air threat service joins tracks that this group clusters apart
                    var gate = CapAlertMath.GateRaid(raid, g.Served, g.ServedRaids, keyOf, g.RaidSeen, now, CapAlertMath.RaidKeyWaitS, out key, g.Memos);   // T-219: key alias and place memory
                    if (gate == CapAlertMath.RaidGate.Served) AbsorbServed(g, raid, key, keyOf, now);
                    if (gate != CapAlertMath.RaidGate.Go || !CapAlertMath.InReach(raid, a.RadiusNm)) continue;
                    Scramble(g, raid, key);
                }
            SetAlertPrio(g, AnyUnlaunched(g));
        }

        private void Scramble(Grp g, AlertRaid raid, string key = null)
        {
            var a = g.A;
            var idle = new List<Mem>();
            foreach (var m in g.Members) if (m.Airborne && m.Leaving == null && m.Barrage && m.RaidIds == null) idle.Add(m);
            var pool = ScramblePool(g);
            int take = CapAlertMath.TakeCount(a.PerRaid, idle.Count + pool.Count);
            string rk = raid.Key.ToString(CultureInfo.InvariantCulture);
            if (take == 0)
            {
                if (g.AlertWarned.Add("raid" + raid.Key))
                {
                    Warn(g, "alert_not_ready", null, "\"raid\":" + raid.Key);
                    // T-204: machines that are preparing or cooling after a landing are not "nobody": the card says "preparing N, next ~T"
                    int prepN = 0; double prepIn = -1;
                    foreach (var pm in g.Members)
                        if (pm.Alert && pm.LentFrom == null && pm.AlertState == null && !pm.Airborne && !pm.Allowed && (pm.Where == "deck_cool" || pm.Where == "deck_prep"))
                        { prepN++; if (pm.ReadyInS >= 0) prepIn = prepIn < 0 ? pm.ReadyInS : Math.Min(prepIn, pm.ReadyInS); }
                    if (prepN > 0) NoteAlert(g, raid.Ids, "preparing", prepN, UiText.Duration(prepIn));
                    else NoteAlert(g, raid.Ids, "not_ready", 0, null);
                }
                return;
            }
            var uids = new List<int>(); string label = null;
            foreach (int id in raid.Ids) g.Served.Add(id);
            if (key != null) g.ServedRaids.Add(key);
            var ids = raid.Ids;
            for (int i = 0; i < take; i++)
            {
                var m = i < idle.Count ? idle[i] : pool[i - idle.Count];
                if (label == null) label = m.TypeLabel ?? m.Type;
                if (SendAlert(g, m, rk, ids, false, key)) uids.Add(m.Uid);
            }
            if (uids.Count == 0) { foreach (int id in raid.Ids) g.Served.Remove(id); if (key != null) g.ServedRaids.Remove(key); g.Memos.RemoveAll(mm => mm.Ids.Exists(raid.Ids.Contains)); return; }
            g.Memos.Add(CapAlertMath.MemoOf(raid, _now, key));   // T-219: the place of the raid is remembered
            Log("alert scramble group=" + g.Name + " raid=" + raid.Key + " nm=" + F(raid.Nm) + " n=" + raid.Ids.Count + " uids=" + string.Join(",", uids));
            Evt("cap_alert", "{\"group\":" + Q(g.Name) + ",\"op\":\"scramble\",\"uids\":[" + string.Join(",", uids) + "],\"raid\":" + Q(rk) + "}");
            NoteAlert(g, raid.Ids, "scramble", uids.Count, label);
        }

        /// <summary>Sends one machine: an airborne idle one gets the raid at once, a standing one is allowed (level deck) or ordered with the launch allowed (level hangar).</summary>
        private bool SendAlert(Grp g, Mem m, string rk, List<int> ids, bool barrage, string key = null)
        {
            var am = m;
            Action<bool> fail = refund =>
            {
                am.AlertState = null; am.RaidIds = null; am.Barrage = false; am.AlertKey = null; am.Tgt = null;
                if (ids != null) foreach (int id in ids) g.Served.Remove(id);
                if (key != null) g.ServedRaids.Remove(key);
                if (ids != null) g.Memos.RemoveAll(mm => mm.Ids.Exists(ids.Contains));   // T-219: no memory of a sortie that did not happen
            };
            if (m.Airborne)
            {
                m.AlertRaid = rk; m.AlertState = "scramble"; m.RaidIds = ids; m.Barrage = false; m.Handed = false; m.FarSince = -1; m.LeashStartNm = -1; m.LeashIn = false;
                m.AlertKey = key; m.Tgt = null; m.BindRestoredAt = -1;   // T-219
                if (ids != null) Log("alert raid uid=" + m.Uid + " group=" + g.Name + " raid=" + rk + " (from the barrage)");
                return true;
            }
            m.AlertRaid = rk; m.AlertState = "scramble"; m.RaidIds = ids; m.Barrage = barrage; m.FarSince = -1; m.Handed = false; m.LeashStartNm = -1; m.LeashIn = false;
            m.AlertKey = key; m.Tgt = null; m.BindRestoredAt = -1;   // T-219
            if (m.Key != null)
            {
                if (!SubmitDeck(g, AirRelief.Allow(m.DeckUid, m.Key, "cap alert scramble uid=" + m.Uid), res =>
                {
                    am.Pending = false;
                    Log("alert allow uid=" + am.Uid + " key=" + am.Key + " -> " + (res.Err ?? "ok"));
                    if (res.Err == null) am.Allowed = true; else fail(true);
                })) { fail(true); return false; }
                m.Pending = true;
                return true;
            }
            if (!OrderDue(g, m.DeckUid, m.Type, "uid=" + m.Uid)) { fail(true); return false; }   // T-187
            double readyS;
            string lo = AlertLoadout(g, m, out readyS);
            if (lo == null) { fail(true); if (g.AlertWarned.Add("no_loadout" + m.Type)) Warn(g, "alert_not_ready", m.Type, "\"why\":\"no_loadout\""); return false; }
            string loadout = lo;
            var req = AirRelief.Order(m.DeckUid, m.Type, lo, g.P.LaunchMissionType, true);
            if (!SubmitDeck(g, req, res =>
            {
                am.Pending = false;
                if (res.Err == null)
                {
                    OrderOk(g, am.DeckUid, am.Type);
                    am.Key = res.Key; am.Allowed = true; am.PlanSlot = -1; _ourKeys.Add(res.Key);
                    AfterOrder(g, am);
                    Log("alert order uid=" + am.Uid + " type=" + am.Type + " loadout=" + loadout + " allow=true ready_s=" + F(res.ReadyS) + " (level hangar)");
                }
                else
                {
                    if (res.Err == "no_ammo") { g.Banned[am.DeckUid + "|" + am.Type + "|" + loadout] = _now + BanS; Warn(g, "loadout_fallback", am.Type, "\"from\":" + Q(loadout)); }
                    else OrderFailed(g, am.DeckUid, am.Type, res.Err, "alert uid=" + am.Uid);
                    fail(true);
                }
            })) { fail(true); return false; }
            m.Pending = true;
            return true;
        }

        /// <summary>cap_scramble (ADR-0079 section 4): the ready machines take off without a raid and circle BarrageNm above the protected ship.</summary>
        public string ScrambleNow(Dictionary<string, object> c)
        {
            if (_deferred != null) return "!restore_waiting";   // T-211
            string err;
            string name = CapCommands.ParseScramble(c, out err);
            if (name == null) return "!" + err;
            var g = Find(name);
            if (g == null) return "!unknown_group";
            if (g.Kind != "alert" || !g.Ordered || g.A == null) return "!not_alert";
            var pool = ScramblePool(g);
            int take = CapAlertMath.TakeCount(g.A.PerRaid, pool.Count);
            if (take == 0) return "!no_ready";
            var uids = new List<int>();
            for (int i = 0; i < take; i++) if (SendAlert(g, pool[i], "", null, true)) uids.Add(pool[i].Uid);
            if (uids.Count == 0) return "!no_ready";
            SetAlertPrio(g, true);
            Log("alert scramble group=" + g.Name + " barrage n=" + uids.Count + " uids=" + string.Join(",", uids));
            Evt("cap_alert", "{\"group\":" + Q(g.Name) + ",\"op\":\"scramble\",\"uids\":[" + string.Join(",", uids) + "],\"raid\":\"\"}");
            return Ack(g.Name, "\"n\":" + uids.Count);
        }

        private string AlertLoadout(Grp g, Mem m, out double readyS)
        {
            readyS = 0;
            var los = DeckLos(g)(m.DeckUid, m.Type);
            if (los == null) return null;
            string want;
            if (g.Loadouts.TryGetValue(m.Type ?? "", out want) && want != "auto")
                foreach (var lo in los) if (lo.Available && string.Equals(lo.Name, want, StringComparison.OrdinalIgnoreCase)) { readyS = AirOpsMath.ReadyS(lo, _g.DeckTimingsMode(), 0); return lo.Name; }
            int i = CapMath.AlertFastestLoadout(los, _g.Ammo, _g.DeckTimingsMode());
            if (i < 0) return null;
            readyS = AirOpsMath.ReadyS(los[i], _g.DeckTimingsMode(), 0);
            return los[i].Name;
        }

        private void OrderAlert(Grp g, Mem m)
        {
            double readyS;
            string lo = AlertLoadout(g, m, out readyS);
            if (lo == null) { if (g.AlertWarned.Add("no_loadout" + m.Type)) Warn(g, "alert_not_ready", m.Type, "\"why\":\"no_loadout\""); return; }
            if (!OrderDue(g, m.DeckUid, m.Type, "alert uid=" + m.Uid)) return;   // T-187
            var om = m; string loadout = lo; double ready = readyS;
            var req = AirRelief.Order(m.DeckUid, m.Type, lo, g.P.LaunchMissionType, false);   // launchAllowed = false: prepared, never launched without a raid
            if (SubmitDeck(g, req, res =>
            {
                om.Pending = false;
                if (res.Err == null)
                {
                    OrderOk(g, om.DeckUid, om.Type);
                    om.Key = res.Key; om.Allowed = false; om.PlanSlot = -1; _ourKeys.Add(res.Key);
                    AfterOrder(g, om);
                    Log("alert order uid=" + om.Uid + " type=" + om.Type + " loadout=" + loadout + " ready_s=" + F(res.ReadyS));
                    Log("loadout pick type=" + om.Type + " loadout=" + loadout + " ready_s=" + F(ready) + " why=alert");
                    Evt("cap_alert", "{\"group\":" + Q(g.Name) + ",\"op\":\"ready\",\"uids\":[" + om.Uid + "],\"raid\":\"\"}");
                }
                else if (res.Err == "no_ammo")
                {
                    g.Banned[om.DeckUid + "|" + om.Type + "|" + loadout] = _now + BanS;
                    Warn(g, "loadout_fallback", om.Type, "\"from\":" + Q(loadout));
                }
                else OrderFailed(g, om.DeckUid, om.Type, res.Err, "alert uid=" + om.Uid);
            })) m.Pending = true;
        }

        // ================================================= the order of the kind (ADR-0079 section 1) =================================
        /// <summary>cap_order kind=alert: no zone. Fields not in the order come from the previous alert order of the group, else from the [Cap] defaults.</summary>
        private string OrderAlertGroup(Grp g, CapOrderCmd cmd)
        {
            bool was = g.Kind == "alert" && g.Ordered && g.A != null;
            var spec = was ? g.A.Clone() : CapAlertSpec.FromParams(g.P);
            var src = cmd.AlertSpec; var given = cmd.AlertGiven;
            if (given.Contains("ref_uid")) spec.RefUid = src.RefUid;
            if (given.Contains("ready")) spec.Ready = src.Ready;
            if (given.Contains("per_raid")) spec.PerRaid = src.PerRaid;
            if (given.Contains("level")) spec.Level = src.Level;
            if (given.Contains("radius_nm")) spec.RadiusNm = src.RadiusNm;
            if (given.Contains("targets")) spec.Targets = src.Targets;
            if (given.Contains("pursuit_nm")) spec.PursuitNm = src.PursuitNm;
            if (given.Contains("speed")) spec.Speed = src.Speed;
            if (given.Contains("roe")) spec.Roe = src.Roe;
            if (given.Contains("after")) spec.After = src.After;
            if (given.Contains("fill")) spec.Fill = src.Fill;
            if (!was) SwitchToAlert(g);
            g.Kind = "alert"; g.A = spec; g.Roe = spec.Roe; g.Ordered = true; g.OrderSeq++; g.Gap = false; g.PrevGap = false; g.GapS = -1;
            g.Zone = new CapZone(); g.AnchorWarned = false;
            g.Warned.Clear(); g.Warns.Clear(); g.Banned.Clear(); g.AlertWarned.Clear();
            foreach (var m in g.Members)
            {
                if (m.LentFrom == null) m.Alert = true;
                if (m.Airborne && m.Leaving == null) { string prev; if (_g.SetRoe(m.Uid, g.Roe, out prev) == null) { if (!m.RoeSet) m.PrevRoe = prev; m.RoeSet = true; } }
            }
            Log("order " + g.Name + " alert ready=" + spec.Ready + " per_raid=" + spec.PerRaid + " level=" + spec.Level + " radius_nm=" + F(spec.RadiusNm) + " targets=" + CapAlertMath.TargetsCsv(spec.Targets)
                + " pursuit_nm=" + F(spec.PursuitNm) + " speed=" + spec.Speed + " roe=" + spec.Roe + " after=" + spec.After + " fill=" + spec.Fill + " ref=" + spec.RefUid);
            return Ack(g.Name, "\"kind\":\"alert\",\"ready\":" + spec.Ready);
        }

        /// <summary>patrol -> alert: airborne birds fly home on hold (leave kind_change), the allowed relief launches are aborted; the standing ones become the alert machines.</summary>
        private void SwitchToAlert(Grp g)
        {
            ReleaseLent(g, "kind_change");
            foreach (var m in new List<Mem>(g.Members))
            {
                if (m.Airborne && m.Leaving == null)
                {
                    RestoreThrottle(m); m.AlertThrottle = -1;
                    try { _g.Rtb(m.Uid); } catch (Exception) { }
                    m.Leaving = "kind_change"; m.LastRtb = _now; m.RtbErr = null; m.Recovered = false;
                    if (g.P.RtbRoeHold) RoeHold(m, "rtb");
                    Log("leave uid=" + m.Uid + " reason=kind_change");
                    Evt("cap_rtb", "{\"group\":" + Q(g.Name) + ",\"unit\":" + m.Uid + ",\"reason\":\"kind_change\",\"speed\":\"cruise\"}");
                }
                else if (!m.Airborne && m.Key != null && m.Allowed) AbortKey(m, "kind_change");
                m.Slot = -1; m.OnStation = false; m.RouteSig = null; m.PlanSlot = -1;
            }
            Log("kind group=" + g.Name + " patrol->alert");
            g.Prio = false;
        }

        /// <summary>alert -> patrol: a bird in a raid guidance flies it to the end and then takes a slot; the other birds of the kind are ordinary members now.</summary>
        private void SwitchToPatrol(Grp g)
        {
            OrphanLent(g);
            foreach (var m in new List<Mem>(g.Members))
            {
                if (m.AutoFill && !m.Airborne) { Log("alert unfill uid=" + m.Uid + " group=" + g.Name + " (kind change)"); DropMember(g, m, false, "unfill"); continue; }
                if (m.Airborne && m.Leaving == null && !(m.AlertState == "scramble" && m.RaidIds != null))
                {
                    RestoreThrottle(m); m.AlertThrottle = -1;
                    try { _g.Rtb(m.Uid); } catch (Exception) { }
                    m.Leaving = "kind_change"; m.LastRtb = _now; m.RtbErr = null; m.Recovered = false;
                    if (g.P.RtbRoeHold) RoeHold(m, "rtb");
                    Log("leave uid=" + m.Uid + " reason=kind_change");
                }
                if (m.Airborne && m.Leaving == null) continue;   // finishes the guidance (Alert stays until AlertClear), then takes a slot
                m.Alert = false; m.AlertState = null; m.AlertRaid = null; m.RaidIds = null; m.Barrage = false;
                m.AlertKey = null; m.Tgt = null; m.BindRestoredAt = -1;   // T-219
                m.AutoFill = false;
            }
            g.Served.Clear(); g.ServedRaids.Clear(); g.RaidSeen.Clear(); g.Memos.Clear();
            SetAlertPrio(g, false);
            Log("kind group=" + g.Name + " alert->patrol");
            g.Kind = "patrol";
        }

        // ================================================= fill from the deck (ADR-0079 section 2) ====================================
        private void FillTick(Grp g, Dictionary<int, CapBird> birds)
        {
            var a = g.A; int standing = 0;
            var decks = new HashSet<int>();
            foreach (var m in g.Members) { if (Standing(m)) standing++; if (m.DeckUid != 0) decks.Add(m.DeckUid); }
            int need = a.Ready - standing;
            if (need <= 0 || decks.Count == 0) return;
            var cand = new List<FillCandidate>();
            foreach (var b in birds.Values)
            {
                if (!(b.Where == "hangar" || b.Where == "deck_prep" || b.Where == "deck_ready") || !decks.Contains(b.DeckUid) || MemOf(g, b.Uid) != null || (b.PendingKey != null && b.Ours)) continue;   // T-187: a ready task made by the game is a free machine; ours is someone's order
                string w;
                if (_own.Owner(b.Uid, out w) != null || (b.HangarK > 0 && !SlotFree(b)) || _g.InGameCapZone(b.Uid) || _g.TakenByPlayer(b.Uid) || _g.TypeAdmission(b.Type) != null || StrikeWhy(b) != null) continue;   // T-325
                cand.Add(new FillCandidate { Uid = b.Uid, DeckUid = b.DeckUid, Where = b.Where, ReadyInS = b.ReadyInS, Free = true });
            }
            int protect = a.RefUid != 0 ? a.RefUid : 0;
            if (protect == 0) foreach (int d in decks) { protect = d; break; }
            foreach (int uid in CapAlertMath.PickFill(cand, need, protect))
            {
                string reason; int took;
                if (TryAdd(g, uid, birds, out reason, out took) == null) continue;
                var m = MemOf(g, took);   // T-186: a taken slot is replaced by a free one of the pair
                if (m == null) continue;
                m.AutoFill = true; m.Alert = true;
                CapBird fb; if (birds.TryGetValue(took, out fb)) AdoptRowKey(g, m, fb, new Dictionary<int, List<AirDeckTask>>());   // T-187: a taken ready task is allowed, not ordered again in this tick
                Log("alert fill uid=" + took + " group=" + g.Name + " deck=" + m.DeckUid);
            }
        }

        /// <summary>Machines taken from the deck leave the group when they stand (fill switched off, stop, disband).</summary>
        private void Unfill(Grp g, string why)
        {
            foreach (var m in new List<Mem>(g.Members))
                if (m.AutoFill && Standing(m) && m.AlertState == null && !m.Pending)
                {
                    Log("alert unfill uid=" + m.Uid + " group=" + g.Name + " (" + why + ")");
                    DropMember(g, m, false, "unfill");
                }
        }

        // ================================================= in the air =================================================================
        /// <summary>A scrambled bird: guidance to the raid every GuideEveryS, hand-over to the game AI inside AAM range + 5 nm, return when the raid is gone.</summary>
        private void AlertAirborne(Grp g, Mem m, CapBird b, IList<CapContact> contacts, double now)
        {
            var a = g.A ?? new CapAlertSpec();
            var pos = new GeoPt(b.Lat, b.Lon);
            b.Where = "to_zone";
            double nh = NearestHostileTo(contacts, pos);
            string postponed;
            string reason = CapMath.LeaveReason(b, 0, nh, g.P, out postponed);
            if (reason != null) { LogPostponeOverride(m, b, reason, nh, g.P); StartLeave(g, m, b, reason, nh); return; }
            var rp = AlertRefPoint(g);
            if (rp.HasValue)
            {
                // T-197: a deck outside the circle must not send the bird home on take-off: the leash starts from the first distance until the bird was inside once
                double dRef = AswMath.DistNm(pos, rp.Value);
                if (m.LeashStartNm < 0) { m.LeashStartNm = dRef; m.LeashT0 = now; }
                if (dRef <= a.PursuitNm) m.LeashIn = true;
                if (CapAlertMath.LeashOut(dRef, a.PursuitNm, m.LeashStartNm, m.LeashIn) || (!m.LeashIn && now - m.LeashT0 > CapAlertMath.LeashNeverInS)) { StartLeave(g, m, b, "alert_leash", nh); return; }   // never-inside cap: no endless chase outside the circle
            }
            // the raid: the served contacts that are still tracked
            CapContact tgt = null; double td = double.MaxValue;
            for (int pass = 0; pass < 2; pass++)
            {
                if (contacts != null && m.RaidIds != null)
                    foreach (var c in contacts)
                    {
                        if (c == null || !m.RaidIds.Contains(c.Id)) continue;
                        double d = AswMath.DistNm(pos, new GeoPt(c.Lat, c.Lon));
                        if (d < td) { td = d; tgt = c; }
                    }
                if (tgt != null || pass == 1 || m.RaidIds == null) break;
                if (!TryRebind(g, m, contacts, now)) break;   // T-219: the tracks of the raid are gone: the same wave under other ids
            }
            if (tgt != null)
            {
                // T-219: the last target point (saved with the binding)
                if (m.Tgt == null) m.Tgt = new RaidMemo();
                m.Tgt.Key = m.AlertKey; m.Tgt.Lat = tgt.Lat; m.Tgt.Lon = tgt.Lon; m.Tgt.CourseDeg = tgt.CourseDeg; m.Tgt.Kn = tgt.SpeedKn; m.Tgt.At = now;
            }
            if (m.Barrage && m.RaidIds == null)
            {
                // no raid: circle over the protected ship, then the usual clear
                if (!rp.HasValue) { AlertClear(g, m, b, nh); return; }
                if (m.RouteSig == null || _g.WaypointsLeft(m.Uid) <= 1)
                {
                    var circle = new List<GeoPt>();
                    for (int k = 0; k < 4; k++) circle.Add(AswMath.Offset(rp.Value, k * 90.0, BarrageNm));
                    circle.Add(circle[0]);   // T-235: the re-issue at <= 1 point left must not cut the 4th leg
                    string e = _g.Patrol(m.Uid, circle, (int)g.P.PatrolAltFt);
                    if (e == null) m.RouteSig = "barrage";
                    Dbg("alert barrage uid=" + m.Uid + " -> " + (e ?? "ok"));
                }
                if (AswMath.DistNm(pos, rp.Value) <= BarrageNm + 3)
                {
                    if (m.FarSince < 0) m.FarSince = now;
                    if (now - m.FarSince >= ReturnFarS) AlertClear(g, m, b, nh);
                }
                return;
            }
            bool far = tgt == null || (rp.HasValue && AswMath.DistNm(rp.Value, new GeoPt(tgt.Lat, tgt.Lon)) > a.RadiusNm + 10);
            if (far)
            {
                if (m.FarSince < 0) m.FarSince = now;
                if (now - m.FarSince >= ReturnFarS) { AlertClear(g, m, b, nh); return; }
            }
            else m.FarSince = -1;
            if (tgt == null || m.Handed) return;
            double aam = b.AamMaxNm > 0 ? b.AamMaxNm : DefaultAamNm;
            if (td <= aam + HandoverExtraNm)
            {
                m.Handed = true;
                Log("alert handover uid=" + m.Uid + " target=" + tgt.Id + " nm=" + F(td) + " aam_nm=" + F(aam));
                return;
            }
            if (now - m.LastGuide < GuideEveryS) return;
            m.LastGuide = now;
            if (m.AlertThrottle < 0)
            {
                // the fastest step the fuel allows for the way to the raid (ADR-0040 D2); the old step comes back at landing. Dash speed "cruise": no rush
                int cur = -1; IList<CapStep> steps = null;
                try { cur = _g.Throttle(m.Uid); steps = _g.ThrottleSteps(m.Uid); } catch (Exception) { }
                m.AlertThrottle = cur < 0 ? 0 : cur;
                if (a.Speed == "max" && cur > 0 && steps != null && steps.Count > 0)
                {
                    string note;
                    int pick = CapMath.DashStep(steps, cur, td, b.ReserveNm, b.MaxRangeNm, double.MaxValue, g.P, out note);
                    if (pick != cur && _g.SetThrottle(m.Uid, pick) == null) { m.Dash = true; m.PrevThrottle = cur; m.DashStep = pick; Log("alert speed uid=" + m.Uid + " step " + cur + "->" + pick + " " + note); }
                }
            }
            double kn = b.PatrolKn > 100 ? b.PatrolKn : 420;
            double vx = tgt.SpeedKn * Math.Sin(tgt.CourseDeg * Math.PI / 180.0), vy = tgt.SpeedKn * Math.Cos(tgt.CourseDeg * Math.PI / 180.0);
            double ixn, iyn;
            bool lead = CapMath.LeadPoint(GeoLon.DLon(pos.Lon, tgt.Lon) * 60.0 * AswMath.LonK(pos.Lat, tgt.Lat), (tgt.Lat - pos.Lat) * 60.0, vx, vy, kn, out ixn, out iyn);
            double ipLat = pos.Lat + iyn / 60.0;   // T-362: K of the segment pos -> ip
            GeoPt ip = lead ? new GeoPt(ipLat, pos.Lon + ixn / (60.0 * AswMath.LonK(pos.Lat, ipLat))) : new GeoPt(tgt.Lat, tgt.Lon);
            string err = _g.Patrol(m.Uid, new List<GeoPt> { ip }, (int)g.P.PatrolAltFt);
            Dbg("alert guide uid=" + m.Uid + " target=" + tgt.Id + " nm=" + F(td) + " " + (lead ? "lead" : "pursuit") + " -> " + (err ?? "ok"));
        }

        /// <summary>ADR-0079 section 5: the raid is gone and the bird passed the leave thresholds. The group's "After the raid": back to stand alert (rtb_alert) or lend the
        /// bird to a patrol group that misses one (lend). A group switched to the patrol kind while the bird was guided takes it into its zone.</summary>
        private void AlertClear(Grp g, Mem m, CapBird b, double nh)
        {
            string raid = m.AlertRaid ?? "";
            if (g.Kind != "alert")
            {
                RestoreThrottle(m);
                m.Alert = false; m.AlertState = null; m.AlertRaid = null; m.RaidIds = null; m.Handed = false; m.FarSince = -1; m.AlertThrottle = -1; m.Barrage = false; m.AlertKey = null; m.Tgt = null; m.BindRestoredAt = -1;
                m.Slot = FreeSlot(g, m); m.RouteSig = null; m.OnStation = false; m.Reason = null;
                Log("alert clear uid=" + m.Uid + " group=" + g.Name + " -> patrol slot=" + m.Slot + " (kind change)");
                return;
            }
            AfterClearResult res;
            if (TryLend(g, m, b, nh, out res)) return;
            Log("alert clear uid=" + m.Uid + " group=" + g.Name + " -> rtb_alert why=" + (res == null ? "no_patrol" : res.Why) + " aam=" + b.AamNow + "/" + m.AamStart + " reserve_nm=" + F(b.ReserveNm)
                + " frac_reserve_nm=" + F(CapMath.FracReserveNm(b.FuelFrac, m.GameMaxNm)) + " low_by=" + (m.FuelLowBy ?? "-")
                + " thr_nm=" + F(ThrNm(b, g)) + (CapMath.FracFuelKnown(b) ? " floor_pct=" + F(g.P.FuelFloorPct * 100) : "") + " max_nm=" + F(b.MaxRangeNm) + " hostile_nm=" + (nh == double.MaxValue ? "-" : F(nh)));
            m.AlertState = "return"; m.Barrage = false;
            StartLeave(g, m, b, "alert_clear", nh);
            Evt("cap_alert", "{\"group\":" + Q(g.Name) + ",\"op\":\"return\",\"uids\":[" + m.Uid + "],\"raid\":" + Q(raid) + "}");
        }

        /// <summary>The lowest patrol slot nobody holds: no airborne non-leaving member flies it and no relief sent from the deck is assigned to it; -1 = all held.</summary>
        private static int FreeSlot(Grp g, Mem self)
        {
            if (!g.Ordered || g.N < 1 || g.Kind != "patrol") return -1;
            var used = new HashSet<int>();
            foreach (var o in g.Members)
            {
                if (o == self || o.Alert) continue;
                if (o.Airborne && o.Leaving == null && o.Slot >= 0) used.Add(o.Slot);
                else if (!o.Airborne && o.Allowed && o.PlanSlot >= 0) used.Add(o.PlanSlot);
            }
            for (int s = 0; s < g.N; s++) if (!used.Contains(s)) return s;
            return -1;
        }

        // ================================================= ROE rules (ADR-0075 item 5) ===============================================
        /// <summary>ROE hold: the previous one stays in the ControlMemory (SetRoe), the group's ROE comes back with the next sortie or with "Return control".</summary>
        private void RoeHold(Mem m, string reason)
        {
            string prev;
            try
            {
                if (_g.SetRoe(m.Uid, "hold", out prev) != null) return;
            }
            catch (Exception) { return; }
            if (!m.RoeSet) { m.PrevRoe = prev; m.RoeSet = true; }
            Log("roe uid=" + m.Uid + " hold reason=" + reason);
            Evt("cap_roe", "{\"uid\":" + m.Uid + ",\"roe\":\"hold\",\"reason\":" + Q(reason) + "}");
        }

        /// <summary>A target that is not in the air: the fire task is dropped; without that member of the game the ROE is hold while the target is a surface one and no air enemy is near.</summary>
        private void SurfaceFilter(Grp g, Mem m, CapBird b, double nearestHostileNm)
        {
            bool surface = CapMath.SurfaceTarget(b) && !b.Evading;
            if (surface && !m.SurfaceHold)
            {
                if (_now - m.LastClear < 5) return;
                m.LastClear = _now;
                string e = null;
                try { e = _g.ClearSurfaceTarget(m.Uid); } catch (Exception ex) { e = ex.GetType().Name; }
                Log("roe uid=" + m.Uid + " reason=surface_target clear -> " + (e ?? "ok"));
                if (e == null) { Evt("cap_roe", "{\"uid\":" + m.Uid + ",\"roe\":" + Q(g.Roe) + ",\"reason\":\"surface_target\"}"); m.RouteSig = null; return; }
                if (nearestHostileNm >= g.P.CombatHostileNm) { RoeHold(m, "surface_target"); m.SurfaceHold = true; }
                return;
            }
            if (m.SurfaceHold && (!surface || nearestHostileNm < g.P.CombatHostileNm))
            {
                string prev;
                if (_g.SetRoe(m.Uid, g.Roe, out prev) == null) { m.SurfaceHold = false; Log("roe uid=" + m.Uid + " " + g.Roe + " reason=surface_target_end"); }
            }
        }

        // ================================================= loadouts (ADR-0075 item 8) ================================================
        /// <summary>The loadouts the planner gets: the group's choice; with an active air threat the "auto" types take the fastest ready-up loadout.</summary>
        private Dictionary<string, string> FastLoadouts(Grp g, IList<CapBird> copies)
        {
            if (!g.Latch.On) return g.Loadouts;
            var map = new Dictionary<string, string>(g.Loadouts);
            var seen = new HashSet<string>();
            foreach (var b in copies)
            {
                if (b.Type == null || !Deckish(b.Where) || !seen.Add(b.Type)) continue;
                string want;
                if (map.TryGetValue(b.Type, out want) && want != "auto") continue;
                var los = DeckLos(g)(b.DeckUid, b.Type);
                int i = CapMath.AlertFastestLoadout(los, _g.Ammo, _g.DeckTimingsMode());
                if (i < 0) continue;
                map[b.Type] = los[i].Name;
                if (g.AlertWarned.Add("pick" + b.Type + los[i].Name)) Log("loadout pick type=" + b.Type + " loadout=" + los[i].Name + " ready_s=" + F(AirOpsMath.ReadyS(los[i], _g.DeckTimingsMode(), 0)) + " why=threat");
            }
            return map;
        }
    }
}
