using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    // T-342 phase B (ADR-0133 section 3, 7, 8): the window orders the strike loadouts from the decks itself. After a plan is counted on command (never in the tick) the controller asks
    // StrikeDeckPlanner what is missing up to the threshold of the goal, orders it through the deck broker (window "strike", one aircraft per order, AllowLaunch) and keeps the key in
    // DeckKeys: from there the adoption of phase B of v1 (TickDeck / PickAdoptee) takes the aircraft into the group. A cancel of the player blocks the group's orders until the target
    // changes or the player orders again. Surplus orders are aborted before take-off, only the group's own keys. No order is a launch: the button stays (the auto mode is phase C).
    internal sealed partial class StrikeController
    {
        private readonly Dictionary<string, double> _deckBackoff = new Dictionary<string, double>();   // deck|type|loadout (loadout "*" = the whole type) -> until (mission time)
        private readonly Dictionary<string, int> _deckFails = new Dictionary<string, int>();
        private List<StrikeDeckOffer> _deckLastOffers = new List<StrikeDeckOffer>();                    // the offers of the last decision (Substitute); dropped on target change / reset
        private readonly Dictionary<string, StrikeDeckOffer> _deckOfferOf = new Dictionary<string, StrikeDeckOffer>();   // key -> the offer it was ordered from (the missiles of the live order)
        private readonly Dictionary<string, double> _deckFateAt = new Dictionary<string, double>();
        private readonly Dictionary<string, bool> _deckSeenIn = new Dictionary<string, bool>();         // groups that had an adopted order since the last deck_ready

        private static string Dk(int deck, string type, string loadout) { return I(deck) + "|" + type + "|" + loadout; }

        private bool DeckBackedOff(int deck, string type, string loadout)
        {
            double until;
            double now = _g.Now;
            if (_deckBackoff.TryGetValue(Dk(deck, type, loadout), out until) && now < until) return true;
            if (_deckBackoff.TryGetValue(Dk(deck, type, "*"), out until) && now < until) return true;
            return false;
        }

        /// <summary>T-342 C: the orders the auto waits for (sent, preparing, await_lift, launch). failed / no_reply / cancelled / aborted are not counted.</summary>
        internal int DeckLiveCount(StrikeGroup g) { return g.DeckOrders.Count(o => o.Live); }

        private static string Missiles(StrikeDeckOffer o) { return I(o.PerAircraft) + " x " + (o.Ammo ?? "?"); }

        // ---------------------------------------------------------------- the decision after a plan

        /// <summary>The ammunition the plan is built on: the most numerous strike missile of the aircraft in the air (null = none).</summary>
        private static string PlanAmmoOf(IList<StrikeAircraft> air)
        {
            var by = new Dictionary<string, int>();
            foreach (var a in air) foreach (var m in a.Missiles) if (StrikeAirMath.IsStrikeMissile(m) && m.Operable && m.Count > 0) by[m.Ammo] = (by.ContainsKey(m.Ammo) ? by[m.Ammo] : 0) + m.Count;
            return by.Count == 0 ? null : by.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        }

        private int DeckClaimed(int deck, string type, int numbers)
        {
            if (Ownership == null) return 0;
            int n = 0;
            for (int k = 1; k <= numbers; k++)
            {
                string w, gr;
                if (Ownership.Slots.Held(deck, type, k, out w, out gr) && w != "strike") n++;
            }
            return n;
        }

        /// <summary>The time of flight without the simulator: the cache, else the rough formula times the measured scale (a probe of the deck planner must not make a cold EstimateShot).</summary>
        private double CheapTof(StrikeAircraft a, StrikeMissile m, StrikeShip s, double r)
        {
            double pt; bool pex;
            if (StrikeTofCache.TryProfile(m.Ammo, r, out pt, out pex)) return pt;   // T-350: the flight profile first
            double roughS = StrikeTotMath.RoughTofS(r, m.SpeedKn > 0 ? m.SpeedKn : 540);
            double alt = m.MinLaunchAltFt > 0 ? m.MinLaunchAltFt : a.CruiseAltFt;
            double sup = m.SpeedKn >= StrikeCalib.SupersonicKn ? StrikeCalib.TofScaleSup : 1.0;
            double cached;
            if (_tofs.TryGet(m.Ammo, r, s.SpeedKn, alt, out cached)) return cached * sup;
            return roughS * _tofs.Scale(m.Ammo, r) * sup;
        }

        /// <summary>Whether any deck can offer a strike loadout for this target (for a group without an aircraft in the air).</summary>
        private bool DeckCanOffer(StrikeGroup g, StrikeShip main, IList<StrikeAircraft> air)
        {
            if (Deck == null || main == null) return false;
            try
            {
                var decks = _g.Decks();
                if (decks == null || decks.Count == 0) return false;
                var inp = DeckInput(g, main, decks, air, null, 0);
                List<StrikeDeckOffer> rej;
                return StrikeDeckPlanner.Offers(inp, out rej).Count > 0;
            }
            catch (Exception) { return false; }
        }

        private StrikeDeckInput DeckInput(StrikeGroup g, StrikeShip main, IList<StrikeDeckInfo> decks, IList<StrikeAircraft> air, GeoPt? from, double axisDeg)
        {
            var tp = new GeoPt(main.Lat, main.Lon);
            GeoPt f = from ?? new GeoPt(main.Lat, main.Lon);
            if (!from.HasValue)
            {
                var nearest = decks.Where(d => d != null).OrderBy(d => AswMath.DistNm(new GeoPt(d.Lat, d.Lon), tp)).FirstOrDefault();
                if (nearest != null) f = new GeoPt(nearest.Lat, nearest.Lon);
                axisDeg = StrikeGeoMath.AxisDeg(f, tp);
            }
            var numbers = new Dictionary<string, int>();
            foreach (var d in decks) if (d != null) foreach (var r in d.Rows) numbers[I(d.DeckUid) + "|" + r.Type] = r.Numbers;
            return new StrikeDeckInput
            {
                Decks = decks, Target = main, AxisFrom = f, AxisDeg = axisDeg, PlanAmmo = PlanAmmoOf(air), P = _p,
                Claimed = (deck, type) => { int n; return DeckClaimed(deck, type, numbers.TryGetValue(I(deck) + "|" + type, out n) ? n : 0); },
                Backoff = DeckBackedOff,
            };
        }

        /// <summary>Plan(): after the log of the plan. Orders what is missing up to the threshold, aborts what is not needed. Not for "choose". Every probe is a StrikePlanMath.Probe with the cached time of flight.</summary>
        internal void DeckAfterPlan(StrikeGroup g, StrikePlanInput inp, IList<StrikeVariant> variants)
        {
            if (Deck == null || g == null || inp == null) return;
            long t0 = TimerStart();
            var main = inp.Ships.FirstOrDefault(s => s != null && s.TrackId == g.Target.MainId);
            if (main == null) return;
            IList<StrikeDeckInfo> decks;
            try { decks = _g.Decks() ?? new List<StrikeDeckInfo>(); } catch (Exception) { decks = new List<StrikeDeckInfo>(); }
            double now = _g.Now;
            bool spread = g.Target.Mode == "spread" && g.Target.Ships.Count >= 2;
            string kind = variants != null && variants.Count > 0 && variants[0].Kind == "clear_pause" ? "clear_pause" : (spread ? "max_targets" : "min_missiles");   // T-342 C: the deck is counted for the sum of both stages of a clear_pause
            double axis = variants != null && variants.Count > 0 ? variants[0].AxisDeg : StrikeGeoMath.AxisDeg(inp.From, new GeoPt(main.Lat, main.Lon));
            var dinp = DeckInput(g, main, decks, inp.Air, inp.Air.Count == 0 ? (GeoPt?)null : inp.From, axis);
            List<StrikeDeckOffer> rejected;
            var offers = StrikeDeckPlanner.Offers(dinp, out rejected);
            _deckLastOffers = new List<StrikeDeckOffer>(offers);
            bool sink = string.Equals(inp.Goal, "sink", StringComparison.OrdinalIgnoreCase);
            double th = sink ? inp.KillThreshold : _p.PkThreshold;
            var live = g.DeckOrders.Where(o => o.Live).ToList();
            int hint = -1;
            if (variants != null && variants.Count > 0 && variants[0].PerTarget.Count > 0 && variants[0].PerTarget[0].Short) hint = variants[0].PerTarget[0].ShortBy;
            var need = new StrikeDeckNeed
            {
                Kind = kind, Offers = offers, Live = live, ShortHint = hint, Blocked = g.DeckBlock != null, EvalMax = StrikeCalib.DeckEvalMax,
                LiveAir = o => { StrikeDeckOffer of; return o.Key != null && _deckOfferOf.TryGetValue(o.Key, out of) ? StrikeDeckPlanner.Virtual(of, 1) : null; },
                Eval = extra =>
                {
                    var all = new List<StrikeAircraft>(inp.Air); all.AddRange(extra);
                    var ci = new StrikePlanInput { Air = all, Ships = inp.Ships, Target = inp.Target, From = inp.From, Now = inp.Now, P = inp.P, TofS = CheapTof, TMin = inp.TMin, Difficulty = inp.Difficulty, Goal = inp.Goal, DamageBonus = inp.DamageBonus, BonusKnown = inp.BonusKnown, KillThreshold = inp.KillThreshold, IsLand = inp.IsLand };
                    var v = StrikePlanMath.Probe(ci, kind);
                    if (v == null || v.PerTarget.Count == 0) return new StrikeDeckEval();
                    var mp = v.PerTarget.FirstOrDefault(t => t.TrackId == g.Target.MainId) ?? v.PerTarget[0];
                    bool ok = !v.Red && (kind == "max_targets" ? v.PerTarget.Count >= g.Target.Ships.Count && v.PerTarget.All(t => t.P >= th - 1e-9) : kind == "clear_pause" ? v.PerTarget.All(t => t.P >= th - 1e-9) : mp.P >= th - 1e-9);
                    return new StrikeDeckEval { Ok = ok, P = mp.P, N = v.MissilesUsed };
                },
            };
            var dec = StrikeDeckPlanner.Decide(need);
            double ms = TimerMs(t0);
            var sum = g.Deck ?? new StrikeDeckSummary();
            g.Deck = sum;
            sum.Kind = kind; sum.Now = dec.Now; sum.With = dec.With; sum.All = dec.All.N > 0 || dec.ShortAfterAll ? dec.All : dec.With; sum.ShortAfterAll = dec.ShortAfterAll;
            sum.Rejected.Clear();
            foreach (var r in rejected) sum.Rejected.Add(r.DeckName + "|" + r.Type + "|" + r.Why);
            sum.StockRows.Clear();
            foreach (var d in decks)
                if (d != null && d.Stock != null)
                {
                    var ammos = new HashSet<string>();
                    foreach (var o in offers.Where(x => x.DeckUid == d.DeckUid)) foreach (var k in o.Needs.Keys) ammos.Add(k);
                    foreach (var o in rejected.Where(x => x.DeckUid == d.DeckUid && x.Needs != null)) foreach (var k in o.Needs.Keys) ammos.Add(k);
                    foreach (string am in ammos.OrderBy(x => x, StringComparer.Ordinal)) { int left, max = -1; if (!d.Stock.TryGetValue(am, out left)) continue; if (d.StockMax != null) d.StockMax.TryGetValue(am, out max); sum.StockRows.Add(d.Name + "|" + am + "|" + I(left) + "|" + I(max)); }
                }
            foreach (string key in dec.Abort)
            {
                var o = g.DeckOrders.FirstOrDefault(x => x.Key == key);
                if (o != null) DeckAbort(g, o, "surplus", "auto");
            }
            if (dec.Why == "blocked" && dec.Now.N >= 0 && !dec.With.Ok) DeckNote(g, "deck_blocked:" + (g.DeckBlock ?? ""));
            var groupedBatches = new List<KeyValuePair<string, int>>();
            var sent = new List<StrikeDeckOffer>();
            foreach (var o in dec.Order)
            {
                if (DeckBackedOff(o.DeckUid, o.Type, o.Loadout)) continue;   // refused by an earlier order of this very decision
                sent.Add(o);
                DeckSubmitOrder(g, o, dec.Why);
                string bk = o.DeckName + "|" + o.Type + "|" + o.Loadout;
                int ix = groupedBatches.FindIndex(x => x.Key == bk);
                if (ix < 0) groupedBatches.Add(new KeyValuePair<string, int>(bk, 1)); else groupedBatches[ix] = new KeyValuePair<string, int>(bk, groupedBatches[ix].Value + 1);
            }
            foreach (var kv in groupedBatches)
            {
                var o = sent.First(x => x.DeckName + "|" + x.Type + "|" + x.Loadout == kv.Key);
                DeckNote(g, "deck_ordered:" + I(kv.Value) + ":" + o.Type + ":" + o.Display + ":" + I(o.PerAircraft * kv.Value) + ":" + o.Ammo + ":" + o.DeckName + ":" + I((int)Math.Round(o.ReadyS)));
            }
            DeckRefreshSummary(g, now);
            if (variants != null)
            {
                double wait = sum.LastReadyS;
                if (g.DeckOrders.Any(o => o.Live) && wait >= 0) foreach (var v in variants) v.Warnings.Add("deck_wait:" + I((int)Math.Round(wait)));
            }
            _log("[Strike] strike deck plan grp=" + g.Name + " kind=" + kind + " now_n=" + I(dec.Now.N) + " now_p=" + F3(dec.Now.P) + " with_n=" + I(dec.With.N) + " with_p=" + F3(dec.With.P) + " all_p=" + F3(sum.All.P)
                  + " order=" + I(dec.Order.Count) + " abort=" + I(dec.Abort.Count) + " evals=" + I(dec.Evals) + " why=" + dec.Why + " ms=" + F1(ms));
        }

        private void DeckNote(StrikeGroup g, string note)
        {
            if (g.Deck == null) g.Deck = new StrikeDeckSummary();
            g.Deck.Notes.Add(note);
            while (g.Deck.Notes.Count > 6) g.Deck.Notes.RemoveAt(0);
        }

        private void DeckRefreshSummary(StrikeGroup g, double now)
        {
            if (g.Deck == null) return;
            var live = g.DeckOrders.Where(o => o.Live).ToList();
            g.Deck.Aircraft = live.Count; g.Deck.Missiles = live.Sum(o => o.Missiles);
            g.Deck.LastReadyS = live.Count == 0 ? -1 : Math.Max(0, live.Max(o => o.OrderedT + o.ReadyS - now));
        }

        // ---------------------------------------------------------------- order / abort

        private StrikeDeckItem ItemOf(string key, StrikeDeckOffer o)
        {
            var it = new StrikeDeckItem { DeckUid = o.DeckUid, Key = key, Type = o.Type, Loadout = o.Loadout, State = "ready_up", DeckName = o.DeckName, RemainS = o.ReadyS, Lat = o.DeckLat, Lon = o.DeckLon };
            if (o.Lo != null) foreach (var m in o.Lo.Missiles) it.Missiles.Add(m);
            return it;
        }

        /// <summary>One DeckRequest Kind = Order (window strike, AllowLaunch, MissionType none); the answer adds the key to DeckKeys as an already allowed key (the adoption of v1 takes the aircraft).</summary>
        private void DeckSubmitOrder(StrikeGroup g, StrikeDeckOffer o, string why)
        {
            double now = _g.Now;
            var ord = new StrikeDeckOrder { DeckUid = o.DeckUid, DeckName = o.DeckName, Type = o.Type, Loadout = o.Loadout, Display = o.Display, Ammo = o.Ammo, Missiles = o.PerAircraft, OrderedT = now, ReadyS = o.ReadyS };
            g.DeckOrders.Add(ord);
            while (g.DeckOrders.Count > 40) { int ix = g.DeckOrders.FindIndex(x => !x.Live); if (ix < 0) break; g.DeckOrders.RemoveAt(ix); }
            string gn = g.Name;
            Deck.Submit(new DeckRequest { DeckUid = o.DeckUid, Window = "strike", Group = g.Name, Kind = DeckKind.Order, Type = o.Type, Loadout = o.Loadout, MissionType = "none", AllowLaunch = true, Reason = "strike", Prio = Prio(g) },
                r =>
                {
                    if (r.Err == null && !ord.Live && r.Key != null)
                    {
                        // T-342 C, review of B: the answer came after the order was dropped (cancelled by the player, taken back by a change of the target, failed no_reply): the new task is aborted at once
                        _log("[Strike] strike deck order grp=" + gn + " deck=" + o.DeckName + " type=" + o.Type + " lo=" + o.Loadout + " key=" + r.Key + " -> late_reply (" + ord.State + ")");
                        Deck.Submit(new DeckRequest { DeckUid = o.DeckUid, Window = "strike", Group = gn, Kind = DeckKind.Abort, Key = r.Key, Reason = "strike_late_reply", Prio = Prio(g) },
                            r2 => _log("[Strike] strike deck cancel grp=" + gn + " key=" + r.Key + " by=auto why=late_reply -> " + (r2.Err ?? "ok")));
                        return;
                    }
                    if (r.Err == null)
                    {
                        ord.Key = r.Key;
                        if (r.ReadyS > 0) ord.ReadyS = r.ReadyS;
                        if (r.Key != null)
                        {
                            if (!g.DeckKeys.Contains(r.Key)) g.DeckKeys.Add(r.Key);
                            _deckAllowed.Add(r.Key);
                            _deckInfo[r.Key] = ItemOf(r.Key, o);
                            _deckOfferOf[r.Key] = o;
                            var known = new HashSet<int>();
                            foreach (var a in _g.Aircraft() ?? new List<StrikeAircraft>()) if (a != null) known.Add(a.Uid);
                            _deckKnown[r.Key] = known;
                        }
                        _deckFails.Remove(Dk(o.DeckUid, o.Type, o.Loadout));
                        _deckFails.Remove(Dk(o.DeckUid, o.Type, "*"));
                        _log("[Strike] strike deck order grp=" + gn + " deck=" + o.DeckName + " type=" + o.Type + " lo=" + o.Loadout + " msl=" + Missiles(o) + " key=" + (r.Key ?? "-") + " ready_s=" + F1(ord.ReadyS) + " -> ok");
                        return;
                    }
                    DeckOrderFailed(g, ord, o, r.Err);
                });
        }

        private void DeckOrderFailed(StrikeGroup g, StrikeDeckOrder ord, StrikeDeckOffer o, string err)
        {
            double now = _g.Now;
            string shown = err;
            if (err == "no_ammo")
            {
                try
                {
                    var d = (_g.Decks() ?? new List<StrikeDeckInfo>()).FirstOrDefault(x => x != null && x.DeckUid == o.DeckUid);
                    if (d != null && d.LaunchesDisabled) shown = "launches_disabled";
                }
                catch (Exception) { }
            }
            ord.State = "failed"; ord.Err = shown;
            string bk = Dk(o.DeckUid, o.Type, o.Loadout);
            int fails; _deckFails.TryGetValue(bk, out fails); fails++; _deckFails[bk] = fails;
            if (shown == "launches_disabled") _deckBackoff[Dk(o.DeckUid, o.Type, "*")] = now + 3600;
            else if (err == "no_ammo") _deckBackoff[bk] = now + StrikeCalib.DeckNoAmmoS;
            else if (err == "no_airframes" || err == "deck_inoperable") _deckBackoff[Dk(o.DeckUid, o.Type, "*")] = now + AirRelief.OrderBackoffS(err, fails);
            else _deckBackoff[bk] = now + AirRelief.OrderBackoffS(err, fails);
            if (err == "unknown_loadout") _log("[Strike] strike deck defect unknown_loadout deck=" + I(o.DeckUid) + " type=" + o.Type + " lo=" + o.Loadout);
            var pool = _deckLastOffers.Where(x => !DeckBackedOff(x.DeckUid, x.Type, x.Loadout)).ToList();
            var next = StrikeDeckPlanner.Substitute(ord, pool);
            _log("[Strike] strike deck err grp=" + g.Name + " key=" + (ord.Key ?? "-") + " err=" + shown + " deck=" + o.DeckName + " type=" + o.Type + " lo=" + o.Loadout + " next=" + (next == null ? "none" : next.DeckName + "/" + next.Loadout));
            DeckNote(g, "deck_err:" + shown + ":" + o.DeckName + ":" + o.Type + ":" + o.Loadout);
            if (next != null && g.DeckBlock == null)
            {
                next.Units = Math.Max(0, next.Units - 1);
                DeckSubmitOrder(g, next, "substitute");
            }
        }

        /// <summary>T-342 D (the debt of the review of C): the broker is silent, not the deck, so there is no substitute (it would be silent too). The whole type of that deck rests with the growing pause
        /// 60 / 120 / 240 / 480 / 600 s (AirRelief.OrderBackoffS), so a replan every 30 s does not order again at every pass. The pause ends by time; an answered order resets the count.</summary>
        private void DeckNoReply(StrikeGroup g, StrikeDeckOrder ord, double now)
        {
            string bk = Dk(ord.DeckUid, ord.Type, "*");
            int fails; _deckFails.TryGetValue(bk, out fails); fails++; _deckFails[bk] = fails;
            double wait = AirRelief.OrderBackoffS("no_reply", fails);
            _deckBackoff[bk] = now + wait;
            ord.Err = "no_reply";
            _log("[Strike] strike deck err grp=" + g.Name + " key=- err=no_reply deck=" + ord.DeckName + " type=" + ord.Type + " lo=" + ord.Loadout + " next=none backoff_s=" + F1(wait).Replace(".0", ""));
        }

        /// <summary>Kind = Abort of an own key before take-off. deck_task_not_found = the task has launched meanwhile: the key stays for the adoption.</summary>
        private void DeckAbort(StrikeGroup g, StrikeDeckOrder o, string why, string by)
        {
            if (o == null || o.Key == null || !o.Live || o.State == "launch") return;
            string key = o.Key; string prev = o.State;
            o.State = by == "player" ? "cancelled" : "aborted";
            var st = o.State;
            Deck.Submit(new DeckRequest { DeckUid = o.DeckUid, Window = "strike", Group = g.Name, Kind = DeckKind.Abort, Key = key, Reason = "strike_" + why, Prio = Prio(g) },
                r =>
                {
                    if (r.Err == null || r.Err == "not_ours")
                    {
                        g.DeckKeys.Remove(key); ForgetDeckKey(key); _deckOfferOf.Remove(key);
                        _log("[Strike] strike deck cancel grp=" + g.Name + " key=" + key + " by=" + by + " why=" + why + " -> " + (r.Err ?? "ok"));
                    }
                    else if (r.Err == "deck_task_not_found")
                    {
                        if (o.State == st) o.State = "await_lift";   // the task has launched: the adoption takes the aircraft, Track corrects the state
                        _log("[Strike] strike deck cancel grp=" + g.Name + " key=" + key + " by=" + by + " why=" + why + " -> deck_task_not_found (stays for the adoption)");
                    }
                    else
                    {
                        if (o.State == st) o.State = prev;
                        _log("[Strike] strike deck cancel grp=" + g.Name + " key=" + key + " by=" + by + " why=" + why + " -> " + r.Err);
                    }
                });
        }

        /// <summary>target_changed | target_sunk | cancel | disband | release | surplus: every own order that has not launched yet.</summary>
        internal void DeckAbortAll(StrikeGroup g, string why)
        {
            if (Deck == null) return;
            foreach (var o in g.DeckOrders.Where(x => x.Live && x.State != "launch").ToList())
            {
                if (o.Key == null) { o.State = "aborted"; _log("[Strike] strike deck state grp=" + g.Name + " key=- sent->aborted why=" + why); continue; }   // no answer yet: its late answer is aborted by the reply callback
                DeckAbort(g, o, why, "auto");
            }
            DeckRefreshSummary(g, _g.Now);
        }

        // ---------------------------------------------------------------- the tick

        private void DeckOnAdopt(StrikeGroup g, string key, int uid)
        {
            var o = g.DeckOrders.FirstOrDefault(x => x.Key == key);
            _deckOfferOf.Remove(key);
            if (o == null) return;
            string from = o.State;
            o.State = "in_group"; o.Uid = uid;
            _deckSeenIn[g.Name] = true;
            _log("[Strike] strike deck state grp=" + g.Name + " key=" + key + " " + from + "->in_group uid=" + I(uid));
        }

        private void DeckOnLost(StrikeGroup g, string key, string why)
        {
            var o = g.DeckOrders.FirstOrDefault(x => x.Key == key);
            _deckOfferOf.Remove(key);
            if (o == null || !o.Live) return;
            o.State = "failed"; o.Err = why;
            _log("[Strike] strike deck state grp=" + g.Name + " key=" + key + " ->failed " + why);
        }

        /// <summary>Every tick of a group: the states of its orders (Track), the late ones, "deck_ready" once all are in the group, the target sunk. No plan, no probe, no EstimateShot.</summary>
        private void TickDeckOrders(StrikeGroup g, double t)
        {
            if (g.DeckOrders.Count == 0) { return; }
            IList<StrikeDeckItem> items = null;
            foreach (var o in g.DeckOrders.Where(x => x.Live && x.Key == null).ToList())   // T-342 C, review of B (1): no answer of the broker for DeckReplyS = failed no_reply
            {
                string ks = StrikeDeckPlanner.TrackNoKey(o, t, StrikeCalib.DeckReplyS);
                if (ks == "failed" && o.State != "failed")
                {
                    o.State = "failed";
                    _log("[Strike] strike deck state grp=" + g.Name + " key=- sent->failed err=no_reply");
                    DeckNoReply(g, o, t);
                    DeckNote(g, "deck_err:no_reply:" + o.DeckName + ":" + o.Type + ":" + o.Loadout);
                }
            }
            if (g.DeckOrders.Any(o => o.Live)) items = _g.DeckItems() ?? new List<StrikeDeckItem>();
            if (items != null)
                foreach (var o in g.DeckOrders.Where(x => x.Live && x.Key != null).ToList())
                {
                    var it = items.FirstOrDefault(x => x != null && x.Key == o.Key);
                    if (it != null) o.RemainS = it.RemainS;
                    string from = o.State;
                    string ns = StrikeDeckPlanner.Track(o, it, false, t, _p.DeckLateS);
                    if (ns != null && ns != from)
                    {
                        o.State = ns;
                        _log("[Strike] strike deck state grp=" + g.Name + " key=" + o.Key + " " + from + "->" + ns + (o.Err == null ? "" : " err=" + o.Err));
                        if (ns == "failed") DeckNote(g, "deck_err:late:" + o.DeckName + ":" + o.Type + ":" + o.Loadout);
                    }
                }
            // the target of the strike is sunk: the orders that have not launched yet are not needed
            if (g.DeckOrders.Any(o => o.Live && o.State != "launch") && g.Target.MainId != 0)
            {
                double last;
                if (!_deckFateAt.TryGetValue(g.Name, out last) || t < last || t - last >= _p.StaleCheckS)
                {
                    _deckFateAt[g.Name] = t;
                    int hits; string fate = null;
                    try { fate = _g.Fate(g.Target.MainId, out hits); } catch (Exception) { }
                    if (fate == "sunk") { DeckAbortAll(g, "target_sunk"); _log("[Strike] strike deck cancel grp=" + g.Name + " all why=target_sunk"); }
                }
            }
            DeckRefreshSummary(g, t);
            bool anyLive = g.DeckOrders.Any(o => o.Live);
            bool seen; _deckSeenIn.TryGetValue(g.Name, out seen);
            if (!anyLive && seen && g.Gather == "waiting")
            {
                _deckSeenIn.Remove(g.Name);
                DeckNote(g, "deck_ready");
                if (g.Timeline != null && (g.Timeline.Status == "draft" || g.Timeline.Status == "armed")) MarkStale(g, "deck_ready");
                else if (g.Timeline != null && g.Timeline.Status == "stale" && g.Timeline.StaleWhy == "aircraft_changed") g.Timeline.StaleWhy = "deck_ready";   // the adoptions marked it; the cause the player cares about is the deck
                _log("[Strike] strike deck ready grp=" + g.Name);
            }
        }

        // ---------------------------------------------------------------- commands

        /// <summary>strike_deck_cancel {group, key?}: no key = every order that has not launched; the group's orders are blocked until the target changes or strike_deck_reorder.</summary>
        public string DeckCancel(Dictionary<string, object> cmd)
        {
            string group = Str(cmd, "group");
            if (string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            var g = Find(group);
            if (g == null) return Fail("no_group");
            string key = Str(cmd, "key");
            var cancelled = new List<string>();
            if (!string.IsNullOrEmpty(key))
            {
                var o = g.DeckOrders.FirstOrDefault(x => x.Key == key);
                if (o == null) return Fail("no_order", key);
                if (!o.Live || o.State == "launch") return Fail("launched", key);
                DeckAbort(g, o, "player", "player");
                cancelled.Add(Q(key));
            }
            else
            {
                foreach (var o in g.DeckOrders.Where(x => x.Live && x.State != "launch" && x.Key != null).ToList()) { DeckAbort(g, o, "player", "player"); cancelled.Add(Q(o.Key)); }
                foreach (var o in g.DeckOrders.Where(x => x.Live && x.Key == null).ToList()) o.State = "cancelled";   // no key yet: its late answer is aborted
            }
            g.DeckBlock = "player";
            _log("[Strike] strike deck block grp=" + g.Name + " why=player");
            DeckRefreshSummary(g, _g.Now);
            return Ack(g.Name, "\"cancelled\":[" + string.Join(",", cancelled) + "],\"block\":\"player\"");
        }

        /// <summary>strike_deck_reorder {group}: the block is lifted; not executing = the plan is counted again (and orders what is missing).</summary>
        public string DeckReorder(Dictionary<string, object> cmd)
        {
            string group = Str(cmd, "group");
            if (string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            var g = Find(group);
            if (g == null) return Fail("no_group");
            if (g.DeckBlock != null) _log("[Strike] strike deck block grp=" + g.Name + " why=clear");
            g.DeckBlock = null;
            if (ExecRunning(g) || (g.Timeline != null && g.Timeline.Status == "executing")) return Ack(g.Name, "\"block\":null,\"plan_status\":\"executing\"");
            string r = Plan(new Dictionary<string, object> { { "group", g.Name } });
            if (IsErr(r)) return r;
            return Ack(g.Name, "\"block\":null,\"plan_status\":" + Q(g.Timeline == null ? "none" : g.Timeline.Status));
        }

        /// <summary>The group's target changed: the block is lifted and the orders of the old target that have not launched are aborted.</summary>
        private void DeckTargetChanged(StrikeGroup g)
        {
            if (g.DeckBlock != null) { _log("[Strike] strike deck block grp=" + g.Name + " why=clear"); g.DeckBlock = null; }
            DeckAbortAll(g, "target_changed");
            _deckLastOffers = new List<StrikeDeckOffer>();
        }

        private void DeckReset()
        {
            _deckBackoff.Clear(); _deckFails.Clear(); _deckLastOffers = new List<StrikeDeckOffer>(); _deckOfferOf.Clear(); _deckFateAt.Clear(); _deckSeenIn.Clear();
            foreach (var g in _groups) { g.DeckOrders.Clear(); g.DeckBlock = null; g.Deck = null; }
        }

        // ---------------------------------------------------------------- ui_state

        /// <summary>deck_orders + deck of a group (ADR-0133 section 7). No hidden state: the stock of the own deck is what the player sees in the game.</summary>
        internal string DeckJson(StrikeGroup g)
        {
            var sb = new StringBuilder("\"deck_orders\":[");
            int n = 0;
            foreach (var o in g.DeckOrders)
            {
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"key\":").Append(o.Key == null ? "null" : Q(o.Key)).Append(",\"deck\":").Append(Q(o.DeckName)).Append(",\"deck_uid\":").Append(I(o.DeckUid)).Append(",\"type\":").Append(Q(o.Type))
                  .Append(",\"loadout\":").Append(Q(o.Loadout)).Append(",\"display\":").Append(Q(o.Display)).Append(",\"n\":1,\"missiles\":").Append(I(o.Missiles)).Append(",\"ammo\":").Append(Q(o.Ammo))
                  .Append(",\"ready_s\":").Append(N(Math.Max(0, o.OrderedT + o.ReadyS - _g.Now))).Append(",\"state\":").Append(Q(o.State)).Append(",\"err\":").Append(o.Err == null ? "null" : Q(o.Err)).Append('}');
            }
            sb.Append("],\"deck_plan\":");
            var d = g.Deck;
            if (d == null && g.DeckBlock == null) { sb.Append("null"); return sb.ToString(); }
            if (d == null) d = new StrikeDeckSummary();
            sb.Append("{\"block\":").Append(g.DeckBlock == null ? "null" : Q(g.DeckBlock)).Append(",\"kind\":").Append(d.Kind == null ? "null" : Q(d.Kind)).Append(",\"aircraft\":").Append(I(d.Aircraft)).Append(",\"missiles\":").Append(I(d.Missiles))
              .Append(",\"last_ready_s\":").Append(d.LastReadyS < 0 ? "null" : N(d.LastReadyS))
              .Append(",\"now\":").Append(EvalJson(d.Now)).Append(",\"with\":").Append(EvalJson(d.With)).Append(",\"all\":").Append(EvalJson(d.All)).Append(",\"short_after_all\":").Append(Json.Bool(d.ShortAfterAll)).Append(",\"stock\":[");
            n = 0;
            foreach (string row in d.StockRows)
            {
                var p = row.Split('|');
                if (p.Length < 4) continue;
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"deck\":").Append(Q(p[0])).Append(",\"ammo\":").Append(Q(p[1])).Append(",\"left\":").Append(p[2]).Append(",\"max\":").Append(p[3] == "-1" ? "null" : p[3]).Append('}');
            }
            sb.Append("],\"rejected\":[");
            n = 0;
            foreach (string row in d.Rejected)
            {
                var p = row.Split('|');
                if (p.Length < 3) continue;
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"deck\":").Append(Q(p[0])).Append(",\"type\":").Append(Q(p[1])).Append(",\"why\":").Append(Q(p[2])).Append('}');
            }
            sb.Append("],\"notes\":[").Append(string.Join(",", d.Notes.Select(Q))).Append("]}");
            return sb.ToString();
        }

        private static string EvalJson(StrikeDeckEval e) { return "{\"n\":" + I(e.N) + ",\"p\":" + N(e.P) + ",\"ok\":" + Json.Bool(e.Ok) + "}"; }

    }
}
