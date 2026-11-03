using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase B (ADR-0133 section 3): the deck planner. Pure (no game, no Unity, no reflection): from the decks of the own side (hangar rows with their loadouts) and a function
    // that says "does the variant reach the threshold with these extra aircraft" it decides which strike loadouts to order, which live orders are not needed any more, and how an order
    // moves from a game state to a state of the window. Only strike loadouts are ever offered (a jammer loadout never); every offer takes its loadout from the list of its OWN type.

    internal sealed class StrikeDeckInput
    {
        public IList<StrikeDeckInfo> Decks;
        public StrikeShip Target;                       // the lead position of the main target
        public GeoPt AxisFrom; public double AxisDeg;   // the approach: the bearing from the group (or the nearest deck) to the target
        public string PlanAmmo;                          // the primary missile of the plan (null = none)
        public Func<int, string, int> Claimed;           // (deck, type) -> slots of other windows within Numbers
        public Func<int, string, string, bool> Backoff;  // (deck, type, loadout) -> refused recently
        public StrikeParams P;
    }

    internal sealed class StrikeDeckNeed
    {
        public string Kind;                              // min_missiles | max_targets
        public IList<StrikeDeckOffer> Offers;            // Why == null, ranked
        public IList<StrikeDeckOrder> Live;              // own live orders of the group
        public Func<IList<StrikeAircraft>, StrikeDeckEval> Eval;   // the variant with these extra (virtual) aircraft
        public Func<StrikeDeckOrder, StrikeAircraft> LiveAir;      // null = a plain virtual aircraft of the order (the missiles of the order)
        public int ShortHint = -1;                       // ShortBy of the current plan (missiles), the first guess of the search
        public bool Blocked;                             // DeckBlock != null: no new orders (surplus aborts still run)
        public int EvalMax = StrikeCalib.DeckEvalMax;
    }

    internal sealed class StrikeDeckDecision
    {
        public List<StrikeDeckOffer> Order = new List<StrikeDeckOffer>();   // one entry per aircraft, in rank order
        public List<string> Abort = new List<string>();                      // keys of live orders not needed any more (never State == launch)
        public StrikeDeckEval Now, With, All;
        public bool ShortAfterAll; public int Evals;
        public string Why;                               // enough | ordered | short_all | blocked | no_offers | no_eval
    }

    internal static class StrikeDeckPlanner
    {
        private static string I(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        private static int PerAircraft(StrikeDeckLoadout lo) { int n = 0; foreach (var m in lo.Missiles) if (StrikeAirMath.IsStrikeMissile(m)) n += Math.Max(0, m.Count); return n; }

        private static Dictionary<string, int> NeedsOf(StrikeDeckLoadout lo, StrikeMissile main, int per)
        {
            if (lo.Cats != null && lo.Cats.Count > 0) return new Dictionary<string, int>(lo.Cats);
            return new Dictionary<string, int> { { main.Ammo, per } };
        }

        private static StrikeMissile Main(StrikeDeckLoadout lo)
        {
            StrikeMissile best = null;
            foreach (var m in lo.Missiles) if (StrikeAirMath.IsStrikeMissile(m) && (best == null || m.Count > best.Count)) best = m;
            return best;
        }

        /// <summary>The cross-track distance of a point from the line through the target along the approach (the launch side of the target), nm.</summary>
        private static double AxisOff(GeoPt tgt, double axisDeg, GeoPt p)
        {
            double d = AswMath.DistNm(tgt, p);
            if (d < 1e-9) return 0;
            double rel = (AswMath.BearingDeg(tgt, p) - (axisDeg + 180)) * Math.PI / 180.0;
            return Math.Abs(d * Math.Sin(rel));
        }

        /// <summary>The filtered and ranked offers; rejected = the offers that turned down (Why set). Loadouts without a strike missile are not listed at all (jammers, patrol, weapons that are not anti-ship).</summary>
        public static List<StrikeDeckOffer> Offers(StrikeDeckInput inp, out List<StrikeDeckOffer> rejected)
        {
            var ok = new List<StrikeDeckOffer>();
            rejected = new List<StrikeDeckOffer>();
            if (inp == null || inp.Decks == null || inp.Target == null) return ok;
            var p = inp.P ?? new StrikeParams();
            var tp = new GeoPt(inp.Target.Lat, inp.Target.Lon);
            foreach (var deck in inp.Decks)
            {
                if (deck == null) continue;
                var dp = new GeoPt(deck.Lat, deck.Lon);
                foreach (var row in deck.Rows)
                {
                    if (row == null) continue;
                    int claimed = 0;
                    try { claimed = inp.Claimed == null ? 0 : Math.Max(0, inp.Claimed(deck.DeckUid, row.Type)); } catch (Exception) { }
                    int free = Math.Max(0, row.Numbers - claimed);
                    foreach (var lo in row.Loadouts)   // the loadouts of THIS row: an offer never carries the loadout of another type
                    {
                        if (lo == null) continue;
                        int per = PerAircraft(lo);
                        var main = Main(lo);
                        if (main == null || per <= 0) continue;
                        var o = new StrikeDeckOffer
                        {
                            DeckUid = deck.DeckUid, DeckName = deck.Name, Type = row.Type, Loadout = lo.Name, Display = lo.Display ?? lo.Name, Ammo = main.Ammo, PerAircraft = per, Free = free,
                            ReadyS = lo.ReadyS, DeckLat = deck.Lat, DeckLon = deck.Lon, Row = row, Lo = lo, Stock = deck.Stock, Needs = NeedsOf(lo, main, per), Illum = lo.Missiles.Any(m => StrikeAirMath.IsStrikeMissile(m) && m.NeedsIllum),
                            DistNm = AswMath.DistNm(dp, tp), AxisOffNm = AxisOff(tp, inp.AxisDeg, dp),
                        };
                        o.Why = Why(inp, p, deck, row, lo, o);
                        if (o.Why == null)
                        {
                            int units = free;
                            if (deck.Stock != null) foreach (var kv in o.Needs) { int left; units = Math.Min(units, kv.Value <= 0 ? units : (deck.Stock.TryGetValue(kv.Key, out left) ? left / kv.Value : 0)); }
                            o.Units = Math.Max(0, units);
                            ok.Add(o);
                        }
                        else rejected.Add(o);
                    }
                }
            }
            string planAmmo = inp.PlanAmmo;
            foreach (var o in ok) o.Rank = ClassRank(o, planAmmo, inp.Target);
            ok.Sort(CompareOffers);
            return ok;
        }

        private static string Why(StrikeDeckInput inp, StrikeParams p, StrikeDeckInfo deck, StrikeHangarRow row, StrikeDeckLoadout lo, StrikeDeckOffer o)
        {
            if (deck.Down) return "deck_down";
            if (deck.LaunchesDisabled) return "launches_disabled";
            if (string.Equals(row.ObjType, "Helicopter", StringComparison.OrdinalIgnoreCase)) return "not_striker";
            var v = Virtual(o, 1);
            if (StrikeAirMath.AircraftWhy(v) != null) return "not_striker";
            if (!lo.Available) return "no_ammo";
            bool back = false;
            try { back = inp.Backoff != null && inp.Backoff(deck.DeckUid, row.Type, lo.Name); } catch (Exception) { }
            if (back) return "backoff";
            if (o.Free <= 0) return "no_airframes";
            if (deck.Stock != null) foreach (var kv in o.Needs) { int left; if (kv.Value > 0 && (!deck.Stock.TryGetValue(kv.Key, out left) || left < kv.Value)) return "stock"; }
            if (row.MaxRangeNm < 0) return "range_unknown";
            double remain, need;
            double kn = row.CruiseKn > 0 ? row.CruiseKn : 450;
            if (!AswStrikeMath.StrikeFuelOk(1, v.EnduranceS, 0, Math.Max(0, o.DistNm + p.FuelMarginNm), o.DistNm, kn, out remain, out need)) return "range";
            return null;
        }

        private static int ClassRank(StrikeDeckOffer o, string planAmmo, StrikeShip target)
        {
            int cls;
            if (planAmmo != null && o.Ammo == planAmmo) cls = 0;
            else
            {
                var m = Main(o.Lo);
                cls = m != null && target != null && m.MaxNm >= target.SamMaxNm + 10 ? 1 : 2;
            }
            return cls * 2 + (o.Illum ? 1 : 0);
        }

        /// <summary>The rank rule: the missile class (the plan's own first, then a window that clears the SAM, an illuminated one after the others of its class); the distance of the deck from the axis in bands of 10 nm; more missiles per aircraft; the ready time; the names.</summary>
        public static int CompareOffers(StrikeDeckOffer a, StrikeDeckOffer b)
        {
            int c = a.Rank.CompareTo(b.Rank);
            if (c != 0) return c;
            c = ((int)Math.Floor(a.AxisOffNm / 10)).CompareTo((int)Math.Floor(b.AxisOffNm / 10));
            if (c != 0) return c;
            c = b.PerAircraft.CompareTo(a.PerAircraft);
            if (c != 0) return c;
            c = a.ReadyS.CompareTo(b.ReadyS);
            if (c != 0) return c;
            c = a.DeckUid.CompareTo(b.DeckUid);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Type, b.Type);
            return c != 0 ? c : string.CompareOrdinal(a.Loadout, b.Loadout);
        }

        /// <summary>A planning aircraft for an offer: airborne, a full tank of its type, at the deck, with its own copies of the missile rows (the plan sees it like an aircraft in the air).</summary>
        public static StrikeAircraft Virtual(StrikeDeckOffer o, int seq)
        {
            var row = o.Row;
            double range = row != null && row.MaxRangeNm > 0 ? row.MaxRangeNm : 0, kn = row != null && row.CruiseKn > 0 ? row.CruiseKn : 450;
            var a = new StrikeAircraft
            {
                Uid = -seq, DeckUid = o.DeckUid, Type = o.Type, Name = "deck:" + o.Type, ObjType = row == null ? "Aircraft" : row.ObjType ?? "Aircraft", Airborne = true, Lat = o.DeckLat, Lon = o.DeckLon,
                AltFt = row == null ? 25000 : row.CruiseAltFt, CruiseAltFt = row == null ? 25000 : row.CruiseAltFt, CruiseKn = kn, SpeedKn = kn, FuelFrac = 1, FuelFracLive = true,
                MaxRangeNm = range, ReserveNm = range, EnduranceS = range / kn * 3600.0, DeckKey = "virtual:" + I(o.DeckUid),
            };
            if (o.Lo != null) foreach (var m in o.Lo.Missiles) a.Missiles.Add(m.Copy());
            StrikeAirMath.Classify(new[] { a });
            return a;
        }

        private static StrikeAircraft LiveVirtual(StrikeDeckNeed need, StrikeDeckOrder o, int seq)
        {
            StrikeAircraft a = null;
            if (need.LiveAir != null) { try { a = need.LiveAir(o); } catch (Exception) { } }
            if (a != null) { a.Uid = -seq; return a; }
            a = new StrikeAircraft { Uid = -seq, DeckUid = o.DeckUid, Type = o.Type, Name = "deck:" + o.Type, ObjType = "Aircraft", Airborne = true, FuelFrac = 1, EnduranceS = 18000, CruiseKn = 450, SpeedKn = 450, CruiseAltFt = 25000, AltFt = 25000, DeckKey = "virtual:" + I(o.DeckUid) };
            a.Missiles.Add(new StrikeMissile { Ammo = o.Ammo ?? "ammo", Name = o.Ammo, Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", Count = Math.Max(1, o.Missiles), MinNm = 3.5, MaxNm = 65, SpeedKn = 540, CruiseAltFt = 50, MinLaunchAltFt = 200, Operable = true });
            StrikeAirMath.Classify(new[] { a });
            return a;
        }

        private static StrikeDeckEval Evalf(StrikeDeckNeed need, List<StrikeAircraft> extra, ref int evals)
        {
            evals++;
            try { return need.Eval(extra); } catch (Exception) { return new StrikeDeckEval(); }
        }

        /// <summary>One entry per aircraft the decks can still give, in rank order; the airframes of a (deck, type) and the magazine of a deck are shared by their loadouts.</summary>
        private static List<StrikeDeckOffer> Expand(IList<StrikeDeckOffer> offers)
        {
            var res = new List<StrikeDeckOffer>();
            var free = new Dictionary<string, int>();
            var stock = new Dictionary<string, int>();
            foreach (var o in offers)
            {
                string fk = I(o.DeckUid) + "|" + o.Type;
                if (!free.ContainsKey(fk)) free[fk] = o.Free;
                if (o.Stock != null) foreach (var kv in o.Needs) { string sk = I(o.DeckUid) + "|" + kv.Key; if (!stock.ContainsKey(sk)) { int v; stock[sk] = o.Stock.TryGetValue(kv.Key, out v) ? v : 0; } }
                for (int k = 0; k < o.Units; k++)
                {
                    if (free[fk] <= 0) break;
                    bool enough = true;
                    if (o.Stock != null) foreach (var kv in o.Needs) if (kv.Value > 0 && stock[I(o.DeckUid) + "|" + kv.Key] < kv.Value) enough = false;
                    if (!enough) break;
                    free[fk]--;
                    if (o.Stock != null) foreach (var kv in o.Needs) stock[I(o.DeckUid) + "|" + kv.Key] -= Math.Max(0, kv.Value);
                    res.Add(o);
                }
            }
            return res;
        }

        /// <summary>The decision of one deck planning: the live orders that are surplus (aborted, latest ready first), or the fewest aircraft to order so that the variant reaches the threshold (binary search over the ranked list, at most EvalMax probes), or everything the decks can give.</summary>
        public static StrikeDeckDecision Decide(StrikeDeckNeed need)
        {
            var d = new StrikeDeckDecision();
            if (need == null || need.Eval == null) { d.Why = "no_eval"; return d; }
            int evals = 0;
            var live = (need.Live ?? new List<StrikeDeckOrder>()).Where(o => o != null && o.Live).ToList();
            var liveAir = new List<StrikeAircraft>();
            for (int i = 0; i < live.Count; i++) liveAir.Add(LiveVirtual(need, live[i], 1000 + i));
            d.Now = Evalf(need, new List<StrikeAircraft>(), ref evals);
            d.With = live.Count == 0 ? d.Now : Evalf(need, new List<StrikeAircraft>(liveAir), ref evals);
            if (d.With.Ok)
            {
                // surplus: the latest ready first, one at a time while the rest still reaches the threshold; a task already launching stays
                var idx = Enumerable.Range(0, live.Count).Where(i => live[i].State != "launch").OrderByDescending(i => live[i].OrderedT + live[i].ReadyS).ThenByDescending(i => i).ToList();
                var keep = new List<StrikeAircraft>(liveAir);
                int budget = need.EvalMax;
                foreach (int i in idx)
                {
                    if (budget-- <= 0) break;
                    var rest = new List<StrikeAircraft>(keep); rest.Remove(liveAir[i]);
                    var e = Evalf(need, rest, ref evals);
                    if (!e.Ok) break;
                    keep = rest; d.Abort.Add(live[i].Key); d.With = e;
                }
                d.Why = "enough"; d.Evals = evals;
                return d;
            }
            if (need.Blocked) { d.Why = "blocked"; d.Evals = evals; return d; }
            var offers = need.Offers ?? new List<StrikeDeckOffer>();
            var list = Expand(offers);
            if (list.Count == 0) { d.Why = "no_offers"; d.Evals = evals; return d; }
            return Search(need, d, live, liveAir, list, evals);
        }

        private static StrikeDeckDecision Search(StrikeDeckNeed need, StrikeDeckDecision d, List<StrikeDeckOrder> live, List<StrikeAircraft> liveAir, List<StrikeDeckOffer> list, int evals)
        {
            int u = list.Count, probes = 0;
            Func<int, StrikeDeckEval> e = len =>
            {
                probes++;
                var extra = new List<StrikeAircraft>(liveAir);
                for (int i = 0; i < len; i++) extra.Add(Virtual(list[i], 1 + i));
                return Evalf(need, extra, ref evals);
            };
            int per0 = Math.Max(1, list[0].PerAircraft);
            int l0 = need.ShortHint > 0 ? (need.ShortHint + per0 - 1) / per0 : 1;
            l0 = Math.Max(1, Math.Min(u, l0));
            int lo = 1, hi;
            StrikeDeckEval best;
            var first = e(l0);
            if (first.Ok) { hi = l0; best = first; }
            else if (l0 == u) { return Short(d, list, first, evals); }
            else
            {
                var all = e(u);
                if (!all.Ok) return Short(d, list, all, evals);
                lo = l0 + 1; hi = u; best = all;
            }
            while (lo < hi && probes < need.EvalMax)
            {
                int mid = (lo + hi) / 2;
                var em = e(mid);
                if (em.Ok) { hi = mid; best = em; } else lo = mid + 1;
            }
            d.Order.AddRange(list.Take(hi));
            d.With = best; d.All = best; d.Why = "ordered"; d.Evals = evals;
            return d;
        }

        private static StrikeDeckDecision Short(StrikeDeckDecision d, List<StrikeDeckOffer> list, StrikeDeckEval all, int evals)
        {
            d.Order.AddRange(list);
            d.All = all; d.ShortAfterAll = true; d.Why = "short_all"; d.Evals = evals;
            return d;
        }

        /// <summary>The state of an order from what the game shows: the item of the task (null = the task is gone) and whether the aircraft of it was adopted.</summary>
        public static string Track(StrikeDeckOrder o, StrikeDeckItem item, bool adopted, double now, double lateS)
        {
            if (o == null) return null;
            if (o.State == "cancelled" || o.State == "aborted" || o.State == "failed" || o.State == "in_group") return o.State;
            if (item == null)
            {
                if (adopted) return "in_group";
                return Late(o, now, LateFor(o.State, lateS)) ? Fail(o) : o.State;
            }
            string st = item.State ?? "";
            string res = o.State;
            if (st == "ready_up") res = "preparing";
            else if (st == "await_deck" || st == "await_crew") res = "await_lift";
            else if (st == "await_spawn" || st == "launching") res = "launch";
            if (Late(o, now, LateFor(res, lateS))) return Fail(o);
            return res;
        }

        private static string Fail(StrikeDeckOrder o) { o.Err = "late"; return "failed"; }

        /// <summary>T-342 C, review of B (2): an order that is already launching fails late only DeckLaunchGraceS after the usual time (the aircraft is spawning, the game is slow, not stuck).</summary>
        private static double LateFor(string state, double lateS) { return state == "launch" ? lateS + StrikeCalib.DeckLaunchGraceS : lateS; }

        /// <summary>T-342 C, review of B (1): a live order without a key (the answer of the broker never came) fails after replyS: failed, Err = no_reply. Else the state as it is.</summary>
        public static string TrackNoKey(StrikeDeckOrder o, double now, double replyS)
        {
            if (o == null) return null;
            if (o.Key == null && o.Live && now >= o.OrderedT + replyS - 1e-9) { o.Err = "no_reply"; return "failed"; }
            return o.State;
        }

        private static bool Late(StrikeDeckOrder o, double now, double lateS) { return now >= o.OrderedT + o.ReadyS + lateS - 1e-9; }

        /// <summary>The next offer of the same missile class with at least as many missiles per aircraft as the failed order, not the failed loadout itself; null = none.</summary>
        public static StrikeDeckOffer Substitute(StrikeDeckOrder failed, IList<StrikeDeckOffer> offers)
        {
            if (failed == null || offers == null) return null;
            foreach (var o in offers)
            {
                if (o == null || o.Why != null || o.Units <= 0) continue;
                if (o.DeckUid == failed.DeckUid && o.Type == failed.Type && o.Loadout == failed.Loadout) continue;
                if (o.Ammo == failed.Ammo && o.PerAircraft >= failed.Missiles) return o;
            }
            return null;
        }

        // ---- self test ----

        private static StrikeMissile Msl(string ammo, int count, double maxNm = 65, string guidance = "arh", string mid = "none", string type = "Missile", string target = "ASuW")
        {
            return new StrikeMissile { Ammo = ammo, Name = ammo, Type = type, TargetType = target, Guidance = guidance, MidCourse = mid, MinNm = 3.5, MaxNm = maxNm, Count = count, SpeedKn = 540, CruiseAltFt = 50, MinLaunchAltFt = 200, Operable = true, WarheadPower = 45 };
        }

        private static StrikeDeckLoadout Lo(string name, double readyS, bool available, params StrikeMissile[] ms)
        {
            var l = new StrikeDeckLoadout { Name = name, Display = name, Available = available, ReadyS = readyS };
            l.Missiles.AddRange(ms);
            StrikeAirMath.Classify(new[] { new StrikeAircraft { Missiles = l.Missiles } });
            return l;
        }

        private static StrikeHangarRow Row(string type, int numbers, double range, params StrikeDeckLoadout[] los)
        {
            var r = new StrikeHangarRow { Type = type, ObjType = "Aircraft", Numbers = numbers, MaxRangeNm = range, CruiseKn = 450, CruiseAltFt = 25000 };
            r.Loadouts.AddRange(los);
            return r;
        }

        /// <summary>Kitty Hawk: A-6E (AntiShip 2 / AntiShipHeavy 4 Harpoon, AntiShipPrecision with a laser weapon), EA-6B (a jammer, an ARM load).</summary>
        internal static StrikeDeckInfo KittyHawk(int uid = 700, double lat = 38, double lon = 10, int a6 = 8, int stock = 48)
        {
            var d = new StrikeDeckInfo { DeckUid = uid, Name = "Kitty Hawk", Lat = lat, Lon = lon, Stock = new Dictionary<string, int> { { "usn_agm-84c", stock } }, StockMax = new Dictionary<string, int> { { "usn_agm-84c", 48 } } };
            d.Rows.Add(Row("usn_a-6e", a6, 1000,
                Lo("AntiShip", 270, true, Msl("usn_agm-84c", 2)), Lo("AntiShipHeavy", 360, true, Msl("usn_agm-84c", 4)),
                Lo("AntiShipPrecision", 300, true, Msl("usn_agm-123", 2, 15, "tv", "none"))));
            d.Rows.Add(Row("usn_ea-6b", 2, 1100, Lo("EW", 300, true, Msl("usn_agm-88", 2, 80, "arm", "none"))));
            return d;
        }

        private static StrikeDeckInput Input(params StrikeDeckInfo[] decks)
        {
            return new StrikeDeckInput { Decks = decks, Target = new StrikeShip { TrackId = 100, Lat = 41, Lon = 10, SamMaxNm = 90 }, AxisFrom = new GeoPt(38, 10), AxisDeg = 0, PlanAmmo = "usn_agm-84c", P = new StrikeParams() };
        }

        private static string SelfTestOffers()
        {
            List<StrikeDeckOffer> rej;
            var kh = KittyHawk();
            var offers = Offers(Input(kh), out rej);
            if (offers.Count != 2 || !offers.All(o => o.Type == "usn_a-6e" && (o.Loadout == "AntiShip" || o.Loadout == "AntiShipHeavy"))) return "kitty hawk offers " + string.Join(",", offers.Select(o => o.Type + ":" + o.Loadout));
            if (rej.Count != 0) return "nothing rejected " + string.Join(",", rej.Select(o => o.Loadout + ":" + o.Why));
            if (offers.Any(o => o.Loadout == "AntiShipPrecision" || o.Type == "usn_ea-6b")) return "a jammer or a laser load offered";
            var heavy = offers.First(o => o.Loadout == "AntiShipHeavy");
            if (heavy.PerAircraft != 4 || heavy.Free != 8 || heavy.Units != 8 || heavy.Ammo != "usn_agm-84c" || heavy.ReadyS != 360) return "heavy " + heavy.PerAircraft + " " + heavy.Free + " " + heavy.Units;
            // the loadout is always from the list of its own type (the SAR defect of T-339 must not repeat): a second striker type on the deck
            var kh2 = KittyHawk();
            kh2.Rows.Add(Row("usn_s-3b", 4, 900, Lo("AntiShipS3", 200, true, Msl("usn_agm-84c", 1))));
            kh2.Rows.Add(new StrikeHangarRow { Type = "ru_ka-29", ObjType = "Helicopter", Numbers = 2, MaxRangeNm = 300, Loadouts = { Lo("AntiShipKa", 200, true, Msl("usn_agm-84c", 1)) } });
            var off2 = Offers(Input(kh2), out rej);
            foreach (var o in off2)
            {
                var row = kh2.Rows.First(r => r.Type == o.Type);
                if (!row.Loadouts.Contains(o.Lo)) return "foreign loadout " + o.Type + ":" + o.Loadout;
            }
            if (!off2.Any(o => o.Type == "usn_s-3b" && o.Loadout == "AntiShipS3") || off2.Any(o => o.Type == "ru_ka-29")) return "types of the deck";
            if (!rej.Any(o => o.Type == "ru_ka-29" && o.Why == "not_striker")) return "helicopter rejected " + string.Join(",", rej.Select(o => o.Type + ":" + o.Why));
            // the airframes of the other windows
            var inp = Input(KittyHawk(a6: 4));
            inp.Claimed = (dk, t) => 3;
            var o1 = Offers(inp, out rej);
            if (o1.Count != 2 || o1.Any(o => o.Free != 1 || o.Units != 1)) return "claimed 3 of 4: free " + string.Join(",", o1.Select(o => o.Free));
            inp.Claimed = (dk, t) => 4;
            o1 = Offers(inp, out rej);
            if (o1.Count != 0 || rej.Count(o => o.Why == "no_airframes") != 2) return "claimed 4 of 4 " + string.Join(",", rej.Select(o => o.Why));
            // no ammunition: the loadout is not available; the stock of the deck
            var na = KittyHawk(); na.Rows[0].Loadouts[1].Available = false;
            o1 = Offers(Input(na), out rej);
            if (o1.Count != 1 || o1[0].Loadout != "AntiShip" || !rej.Any(o => o.Loadout == "AntiShipHeavy" && o.Why == "no_ammo")) return "available false";
            o1 = Offers(Input(KittyHawk(stock: 3)), out rej);
            if (o1.Count != 1 || o1[0].Loadout != "AntiShip" || o1[0].Units != 1 || !rej.Any(o => o.Loadout == "AntiShipHeavy" && o.Why == "stock")) return "stock 3 " + string.Join(",", o1.Select(o => o.Loadout + ":" + o.Units));
            var unk = KittyHawk(); unk.Stock = null;
            o1 = Offers(Input(unk), out rej);
            if (o1.Count != 2 || o1.Any(o => o.Units != 8)) return "stock unknown";
            // range
            var far = Input(KittyHawk()); far.Target = new StrikeShip { TrackId = 100, Lat = 60, Lon = 10, SamMaxNm = 90 };
            o1 = Offers(far, out rej);
            if (o1.Count != 0 || !rej.All(o => o.Why == "range")) return "range " + string.Join(",", rej.Select(o => o.Why));
            var rk = KittyHawk(); rk.Rows[0].MaxRangeNm = -1;
            o1 = Offers(Input(rk), out rej);
            if (o1.Count != 0 || !rej.All(o => o.Why == "range_unknown")) return "range unknown";
            // the deck is down / the launches are off (beta)
            var dn = KittyHawk(); dn.Down = true;
            o1 = Offers(Input(dn), out rej);
            if (o1.Count != 0 || !rej.All(o => o.Why == "deck_down")) return "deck down";
            var ld = KittyHawk(); ld.LaunchesDisabled = true;
            o1 = Offers(Input(ld), out rej);
            if (o1.Count != 0 || !rej.All(o => o.Why == "launches_disabled")) return "launches disabled";
            var bo = Input(KittyHawk()); bo.Backoff = (dk, t, l) => l == "AntiShip";
            o1 = Offers(bo, out rej);
            if (o1.Count != 1 || !rej.Any(o => o.Loadout == "AntiShip" && o.Why == "backoff")) return "backoff";
            // the virtual aircraft
            var v = Virtual(heavy, 3);
            if (v.Uid != -3 || !v.Airborne || v.FuelFrac != 1 || v.Missiles.Count != 1 || v.Missiles[0].Count != 4 || ReferenceEquals(v.Missiles[0], heavy.Lo.Missiles[0]) || v.DeckKey != "virtual:700" || v.Lat != 38 || v.EnduranceS <= 0 || v.Role != "striker") return "virtual";
            return null;
        }

        private static string SelfTestRank()
        {
            List<StrikeDeckOffer> rej;
            var kh = KittyHawk(700, 38, 10);
            var am = KittyHawk(701, 38, 9.2);   // a second deck, off the axis
            am.Name = "America";
            // (a) the plan's own missile first
            var other = KittyHawk(702, 38, 10); other.Name = "Other"; other.Stock = new Dictionary<string, int> { { "usn_agm-84a", 48 } };
            foreach (var l in other.Rows[0].Loadouts) foreach (var m in l.Missiles) m.Ammo = m.Name = "usn_agm-84a";
            var inp = Input(other, kh);
            var offers = Offers(inp, out rej);
            if (offers[0].DeckUid != 700) return "own missile first " + offers[0].DeckUid;
            // (b) the deck nearer to the axis first (same class), 0 nm against ~45 nm
            offers = Offers(Input(am, kh), out rej);
            if (offers[0].DeckUid != 700 || offers.Last().DeckUid != 701) return "axis band " + string.Join(",", offers.Select(o => o.DeckUid + "@" + Math.Round(o.AxisOffNm)));
            // (c) heavy before the light one on a deck
            if (offers[0].Loadout != "AntiShipHeavy") return "heavy first " + offers[0].Loadout;
            // (d) equal: the ready time
            var eq = KittyHawk(); eq.Rows[0].Loadouts[0] = Lo("AntiShip", 100, true, Msl("usn_agm-84c", 4)); eq.Rows[0].Loadouts[1] = Lo("AntiShipHeavy", 360, true, Msl("usn_agm-84c", 4));
            offers = Offers(Input(eq), out rej);
            if (offers[0].Loadout != "AntiShip") return "ready time " + offers[0].Loadout;
            // (e) an illuminated missile (AS-2) after the others of its class
            var il = KittyHawk();
            il.Stock["wp_as-2"] = 10;
            il.Rows.Add(Row("wp_tu-16k", 4, 3000, Lo("AS2", 100, true, Msl("wp_as-2", 4, 150, "arh", "radio"), Msl("usn_agm-84c", 0))));
            offers = Offers(Input(il), out rej);
            var iAs2 = offers.FindIndex(o => o.Illum);
            if (iAs2 < 0 || iAs2 != offers.Count - 1 || !offers[iAs2].Illum) return "illuminated last " + string.Join(",", offers.Select(o => o.Loadout + (o.Illum ? "!" : "")));
            // (f) deterministic: the order of the input does not matter
            var a1 = Offers(Input(kh, am), out rej).Select(o => o.DeckUid + o.Loadout).ToList();
            var a2 = Offers(Input(am, kh), out rej).Select(o => o.DeckUid + o.Loadout).ToList();
            if (!a1.SequenceEqual(a2)) return "order of the input " + string.Join(",", a1) + " vs " + string.Join(",", a2);
            return null;
        }

        /// <summary>The monotone fake: the variant is Ok with 12 missiles or more (live + extra); P grows with the missiles.</summary>
        private static StrikeDeckNeed Fake(IList<StrikeDeckOffer> offers, IList<StrikeDeckOrder> live, int airborne, int needMissiles, List<int> calls = null)
        {
            return new StrikeDeckNeed
            {
                Kind = "min_missiles", Offers = offers, Live = live, ShortHint = -1,
                Eval = extra =>
                {
                    int n = airborne + extra.Sum(a => a.Missiles.Sum(m => m.Count));
                    if (calls != null) calls.Add(n);
                    return new StrikeDeckEval { Ok = n >= needMissiles, P = Math.Min(1, n / (double)Math.Max(1, needMissiles) * 0.8), N = n };
                },
            };
        }

        private static string SelfTestDecide()
        {
            List<StrikeDeckOffer> rej;
            var offers = Offers(Input(KittyHawk()), out rej);   // Heavy (4) first, then AntiShip (2)
            // 8 in the air, 12 needed: one Heavy
            var d = Decide(Fake(offers, new List<StrikeDeckOrder>(), 8, 12));
            if (d.Order.Count != 1 || d.Order[0].Loadout != "AntiShipHeavy" || d.Why != "ordered" || d.ShortAfterAll || !d.With.Ok || d.Abort.Count != 0) return "one heavy " + d.Order.Count + " " + d.Why;
            if (d.Now.Ok || d.Now.N != 8) return "now " + d.Now.N;
            // the first guess from the shortage: 4 missing = one Heavy: few probes
            var need = Fake(offers, new List<StrikeDeckOrder>(), 8, 12); need.ShortHint = 4;
            d = Decide(need);
            if (d.Order.Count != 1 || d.Evals > 4) return "hint probes " + d.Evals;
            // the fewest: 30 needed with 8 in the air = 22 more = 6 Heavy (24)
            d = Decide(Fake(offers, new List<StrikeDeckOrder>(), 8, 30));
            if (d.Order.Sum(o => o.PerAircraft) < 22 || d.Order.Sum(o => o.PerAircraft) > 22 + 4 || d.Why != "ordered") return "fewest " + d.Order.Sum(o => o.PerAircraft);
            // 60 needed, the decks give 8 aircraft: 8 Heavy = 32 (Free 8): everything is ordered, short after all
            d = Decide(Fake(offers, new List<StrikeDeckOrder>(), 8, 60));
            if (d.Why != "short_all" || !d.ShortAfterAll || d.Order.Count != 8 || d.All.Ok || d.All.N != 8 + 32) return "short all " + d.Why + " " + d.Order.Count + " " + d.All.N;
            // blocked: no new orders
            need = Fake(offers, new List<StrikeDeckOrder>(), 8, 12); need.Blocked = true;
            d = Decide(need);
            if (d.Order.Count != 0 || d.Why != "blocked") return "blocked " + d.Why;
            // the probe budget
            need = Fake(offers, new List<StrikeDeckOrder>(), 0, 28); need.EvalMax = 2;
            d = Decide(need);
            int probes = d.Evals - 2;   // Now and With are not probes of the search
            if (probes > 2 || d.Order.Count == 0) return "probe budget " + probes;
            // the magazine of a deck is shared by its loadouts: 6 Harpoon = at most 6 missiles in all
            var low = Offers(Input(KittyHawk(stock: 6)), out rej);
            d = Decide(Fake(low, new List<StrikeDeckOrder>(), 0, 100));
            if (d.Order.Sum(o => o.PerAircraft) > 6) return "stock shared " + d.Order.Sum(o => o.PerAircraft);
            // no offers
            d = Decide(Fake(new List<StrikeDeckOffer>(), new List<StrikeDeckOrder>(), 0, 12));
            if (d.Why != "no_offers") return "no offers " + d.Why;
            return null;
        }

        private static StrikeDeckOrder Ord(string key, double orderedT, double readyS, int missiles = 4, string state = "preparing")
        {
            return new StrikeDeckOrder { Key = key, DeckUid = 700, DeckName = "Kitty Hawk", Type = "usn_a-6e", Loadout = "AntiShipHeavy", Ammo = "usn_agm-84c", Missiles = missiles, OrderedT = orderedT, ReadyS = readyS, State = state };
        }

        private static string SelfTestIdem()
        {
            List<StrikeDeckOffer> rej;
            var offers = Offers(Input(KittyHawk()), out rej);
            var d1 = Decide(Fake(offers, new List<StrikeDeckOrder>(), 8, 20));
            if (d1.Order.Count != 3) return "first decision " + d1.Order.Count;
            var live = new List<StrikeDeckOrder>();
            for (int i = 0; i < d1.Order.Count; i++) live.Add(Ord("task:" + i, 1000, 360));
            var d2 = Decide(Fake(offers, live, 8, 20));
            if (d2.Order.Count != 0 || d2.Why != "enough" || d2.Abort.Count != 0) return "second decision orders again " + d2.Order.Count + " " + d2.Why + " abort " + d2.Abort.Count;
            return null;
        }

        private static string SelfTestSurplus()
        {
            List<StrikeDeckOffer> rej;
            var offers = Offers(Input(KittyHawk()), out rej);
            // three live orders (12 missiles), 4 are enough: the two that are ready the latest go
            var live = new List<StrikeDeckOrder> { Ord("task:1", 1000, 300), Ord("task:2", 1000, 360), Ord("task:3", 1000, 420) };
            var d = Decide(Fake(offers, live, 0, 4));
            if (d.Abort.Count != 2 || !d.Abort.Contains("task:3") || !d.Abort.Contains("task:2") || d.Abort.Contains("task:1")) return "latest first " + string.Join(",", d.Abort);
            // an order that is launching is never aborted
            live = new List<StrikeDeckOrder> { Ord("task:1", 1000, 300), Ord("task:2", 1000, 360, 4, "launch"), Ord("task:3", 1000, 420) };
            d = Decide(Fake(offers, live, 0, 4));
            if (d.Abort.Contains("task:2")) return "launching aborted";
            // the player's block does not stop the removal of the surplus
            var need = Fake(offers, live, 0, 4); need.Blocked = true;
            d = Decide(need);
            if (d.Abort.Count == 0 || d.Why != "enough") return "blocked still aborts " + d.Abort.Count;
            // nothing airborne is needed: the airborne ones are enough, every live order that is not launching goes
            d = Decide(Fake(offers, live, 20, 12));
            if (d.Abort.Count != 2) return "airborne enough " + d.Abort.Count;
            return null;
        }

        private static string SelfTestTrack()
        {
            var o = Ord("task:1", 1000, 300);
            var item = new StrikeDeckItem { Key = "task:1", State = "ready_up" };
            if (Track(o, item, false, 1100, 600) != "preparing") return "ready_up";
            item.State = "await_deck"; if (Track(o, item, false, 1100, 600) != "await_lift") return "await_deck";
            item.State = "await_crew"; if (Track(o, item, false, 1100, 600) != "await_lift") return "await_crew";
            item.State = "launching"; if (Track(o, item, false, 1100, 600) != "launch") return "launching";
            item.State = "await_spawn"; if (Track(o, item, false, 1100, 600) != "launch") return "await_spawn";
            if (Track(o, null, true, 1100, 600) != "in_group") return "adopted";
            if (Track(o, null, false, 1100, 600) != "preparing") return "the task is gone, not adopted yet: the state stays";
            // the late order: ready 1000 + 300 + 600 = 1900
            item.State = "ready_up";
            if (Track(Ord("task:1", 1000, 300), item, false, 1899, 600) != "preparing") return "599 s is not late";
            var late = Ord("task:1", 1000, 300);
            if (Track(late, item, false, 1900, 600) != "failed" || late.Err != "late") return "late " + late.Err;
            // the dead states stay
            foreach (string st in new[] { "cancelled", "aborted", "failed", "in_group" })
                if (Track(Ord("task:1", 1000, 300, 4, st), item, false, 5000, 600) != st) return "dead state " + st;
            // the substitute
            List<StrikeDeckOffer> rej;
            var kh = KittyHawk(700); var am = KittyHawk(701, 38, 9.2); am.Name = "America";
            var offers = Offers(Input(kh, am), out rej);
            var failed = Ord("task:9", 1000, 300);
            var sub = Substitute(failed, offers);
            if (sub == null || sub.DeckUid != 701 || sub.Loadout != "AntiShipHeavy") return "substitute " + (sub == null ? "null" : sub.DeckUid + sub.Loadout);
            var only = Offers(Input(kh), out rej);
            if (Substitute(failed, only) != null) return "no substitute on the same deck";
            return null;
        }

        private static string SelfTestNoReply()
        {
            var o = Ord(null, 1000, 300, 4, "sent");
            if (TrackNoKey(o, 1029, StrikeCalib.DeckReplyS) != "sent" || o.Err != null) return "29 s: sent " + TrackNoKey(o, 1029, StrikeCalib.DeckReplyS);
            if (TrackNoKey(o, 1030, StrikeCalib.DeckReplyS) != "failed" || o.Err != "no_reply") return "30 s: failed no_reply " + o.Err;
            var keyed = Ord("task:1", 1000, 300);
            if (TrackNoKey(keyed, 5000, StrikeCalib.DeckReplyS) != keyed.State) return "an order with a key is not touched";
            var dead = Ord(null, 1000, 300, 4, "cancelled");
            if (TrackNoKey(dead, 5000, StrikeCalib.DeckReplyS) != "cancelled") return "a dead order stays";
            return null;
        }

        private static string SelfTestLaunchLate()
        {
            var item = new StrikeDeckItem { Key = "task:1", State = "launching" };
            // ready 1000 + 300, late after 600: 1900; launching gets 180 s more
            var o = Ord("task:1", 1000, 300);
            if (Track(o, item, false, 1900, 600) != "launch") return "launch at ready + 600 stays launch";
            if (Track(o, item, false, 2079, 600) != "launch") return "launch at ready + 779";
            var late = Ord("task:1", 1000, 300);
            if (Track(late, item, false, 2080, 600) != "failed" || late.Err != "late") return "launch at ready + 780 is failed late " + late.Err;
            item.State = "ready_up";
            var prep = Ord("task:1", 1000, 300);
            if (Track(prep, item, false, 1900, 600) != "failed" || prep.Err != "late") return "preparing at ready + 600 is failed late";
            // the task is gone and the order was launching: the same grace
            var gone = Ord("task:1", 1000, 300, 4, "launch");
            if (Track(gone, null, false, 1900, 600) != "launch") return "a gone launching order keeps the grace";
            if (Track(gone, null, false, 2080, 600) != "failed") return "a gone launching order fails after the grace";
            return null;
        }

        public static string SelfTest(string part = "all")
        {
            string e;
            if ((part == "all" || part == "offers") && (e = SelfTestOffers()) != null) return "offers: " + e;
            if ((part == "all" || part == "rank") && (e = SelfTestRank()) != null) return "rank: " + e;
            if ((part == "all" || part == "decide") && (e = SelfTestDecide()) != null) return "decide: " + e;
            if ((part == "all" || part == "idem") && (e = SelfTestIdem()) != null) return "idem: " + e;
            if ((part == "all" || part == "surplus") && (e = SelfTestSurplus()) != null) return "surplus: " + e;
            if ((part == "all" || part == "track") && (e = SelfTestTrack()) != null) return "track: " + e;
            if ((part == "all" || part == "noreply") && (e = SelfTestNoReply()) != null) return "noreply: " + e;
            if ((part == "all" || part == "launch-late") && (e = SelfTestLaunchLate()) != null) return "launch-late: " + e;
            if (part == "budget" && (e = StrikePlanMath.SelfTest("deck-budget")) != null) return "budget: " + e;
            return null;
        }
    }
}
