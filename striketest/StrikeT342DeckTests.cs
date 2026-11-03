using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>A deck broker that answers like the game: a key and a ready time for an order, a scripted error per (type|loadout), a scripted answer to an abort.</summary>
    internal sealed class FakeOrderDeck : IAirDeck
    {
        public List<DeckRequest> Reqs = new List<DeckRequest>();
        public Dictionary<string, string> OrderErr = new Dictionary<string, string>();   // "type|loadout" -> the error
        public Dictionary<string, string> AbortErr = new Dictionary<string, string>();   // key -> the error
        public double ReadyS = 270;
        public Action<DeckRequest> OnOrder;
        public int NextKey = 100;
        public bool Hold;                                                             // T-342 D: the replies of the orders are kept (the broker is silent / late)
        public List<KeyValuePair<DeckRequest, Action<DeckResult>>> Pending = new List<KeyValuePair<DeckRequest, Action<DeckResult>>>();
        public void Release(int index) { var p = Pending[index]; Pending.RemoveAt(index); p.Value(null); }
        public void ReleaseAll() { while (Pending.Count > 0) Release(0); }
        public List<string> Declared = new List<string>();
        public IEnumerable<DeckRequest> Orders { get { return Reqs.Where(r => r.Kind == DeckKind.Order); } }
        public IEnumerable<DeckRequest> Aborts { get { return Reqs.Where(r => r.Kind == DeckKind.Abort); } }
        public void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids) { Declared.Add(window + ":" + prio.Name + ":" + string.Join(",", deckUids)); }
        public int Submit(DeckRequest r, Action<DeckResult> done)
        {
            Reqs.Add(r);
            var res = new DeckResult { Ticket = Reqs.Count };
            if (r.Kind == DeckKind.Order)
            {
                if (OnOrder != null) OnOrder(r);
                string e;
                if (OrderErr.TryGetValue(r.Type + "|" + r.Loadout, out e)) res.Err = e;
                else { res.Key = "task:" + (NextKey++).ToString(CultureInfo.InvariantCulture); res.ReadyS = ReadyS; }
            }
            else if (r.Kind == DeckKind.Abort) { string e; if (AbortErr.TryGetValue(r.Key ?? "", out e)) res.Err = e; }
            if (Hold && r.Kind == DeckKind.Order) { var held = res; Pending.Add(new KeyValuePair<DeckRequest, Action<DeckResult>>(r, _ => done(held))); return Reqs.Count; }
            done(res);
            return Reqs.Count;
        }
        public double HeldS(string window, string group) { return -1; }
        public bool DeckDown(int deckUid) { return false; }
    }

    // T-342 phase B stand sections "t342 deck-*": the controller over the fake game and the fake order deck.
    internal static class StrikeT342DeckTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }

        public static readonly string[] Names = { "deck-order", "deck-err", "deck-cancel", "deck-tick", "deck-load", "deck-view", "deck-adopt-uid", "deck-late-reply", "deck-noreply-backoff" };

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "sink";
            try
            {
                switch (n)
                {
                    case "deck-order": return Order();
                    case "deck-err": return Err();
                    case "deck-cancel": return Cancel();
                    case "deck-tick": return Tick();
                    case "deck-load": return Load();
                    case "deck-view": return View();
                    case "deck-adopt-uid": return AdoptUid();
                    case "deck-late-reply": return LateReply();
                    case "deck-noreply-backoff": return NoReplyBackoff();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        private sealed class Env
        {
            public StrikeFakeGame Game; public List<string> Log = new List<string>(); public StrikeController C; public FakeOrderDeck Deck; public AirOwnership Own;
        }

        private static Env Make(int[] uids, bool second = false, bool group = true)
        {
            var e = new Env { Game = StrikeT341CtlTests.Game(), Deck = new FakeOrderDeck(), Own = new AirOwnership() };
            e.Game.DeckInfos.Add(StrikeDeckPlanner.KittyHawk());
            if (second) { var am = StrikeDeckPlanner.KittyHawk(701, 38, 9.4); am.Name = "America"; e.Game.DeckInfos.Add(am); }
            e.C = new StrikeController(e.Game, new StrikeParams(), e.Log.Add) { Ownership = e.Own, Deck = e.Deck };
            if (group)
            {
                e.C.Group(C("op", "create", "group", "A", "uids", uids));
                e.C.Target(C("group", "A", "target_id", 100));
            }
            return e;
        }

        private static int OrderCount(Env e) { return e.Deck.Orders.Count(); }

        private static string Order()
        {
            var e = Make(new[] { 1, 2 });
            e.C.Tick(1001);
            string r = e.C.Plan(C("group", "A"));
            if (StrikeController.IsErr(r)) return "plan " + r;
            var reqs = e.Deck.Orders.ToList();
            if (reqs.Count < 1) return "no order (8 Harpoon against a Kidd are not enough) " + string.Join(" | ", e.Log.Where(l => l.Contains("deck")));
            foreach (var q in reqs)
                if (q.Window != "strike" || q.Group != "A" || q.Type != "usn_a-6e" || q.MissionType != "none" || !q.AllowLaunch || q.DeckUid != 700 || q.Prio == null || q.Prio.Order != "strike" || (q.Loadout != "AntiShipHeavy" && q.Loadout != "AntiShip")) return "request " + q.Type + "|" + q.Loadout + " " + q.Window + " " + q.MissionType;
            if (reqs[0].Loadout != "AntiShipHeavy") return "heavy first " + reqs[0].Loadout;
            var g = e.C.Groups[0];
            if (g.DeckOrders.Count != reqs.Count || g.DeckOrders.Any(o => o.Key == null || o.State != "sent") || g.DeckKeys.Count != reqs.Count) return "orders in the group " + g.DeckOrders.Count + "/" + g.DeckKeys.Count;
            e.C.Tick(1002);
            if (e.Deck.Reqs.Count(q => q.Kind == DeckKind.Allow) != 0) return "an ordered key is allowed already (no second allow)";
            if (!e.Log.Any(l => l.StartsWith("[Strike] strike deck plan grp=A ", StringComparison.Ordinal)) || !e.Log.Any(l => l.Contains("strike deck order grp=A deck=Kitty Hawk type=usn_a-6e lo=AntiShipHeavy") && l.EndsWith("-> ok", StringComparison.Ordinal))) return "log " + string.Join(" | ", e.Log.Where(l => l.Contains("deck")));
            if (g.Deck == null || !g.Deck.Notes.Any(n => n.StartsWith("deck_ordered:", StringComparison.Ordinal))) return "note deck_ordered";
            var plan = Arr(Obj(r)["variants"]);
            foreach (object vo in plan) if (!((List<object>)((Dictionary<string, object>)vo)["warnings"]).Any(w => ((string)w).StartsWith("deck_wait:", StringComparison.Ordinal))) return "deck_wait warning";
            // a second plan does not multiply the orders: the live ones count as the aircraft of the plan
            int before = OrderCount(e);
            e.C.Plan(C("group", "A"));
            if (OrderCount(e) != before) return "a second plan orders again " + before + " -> " + OrderCount(e);
            // the order is never a launch: no strike_launch happened
            if (e.C.Groups[0].Timeline != null && e.C.Groups[0].Timeline.Status == "executing") return "no launch by an order";
            // a group without aircraft in the air but with a target and a deck: the plan is empty (no_airborne) and orders from the deck
            var e2 = Make(new int[0], false, false);
            e2.C.Group(C("op", "create", "group", "B"));
            e2.C.Target(C("group", "B", "target_id", 100));
            string r2 = e2.C.Plan(C("group", "B"));
            if (StrikeController.IsErr(r2)) return "plan without aircraft " + r2;
            var o2 = Obj(r2);
            if (Arr(o2["variants"]).Count != 0 || (string)o2["why"] != "no_airborne" || OrderCount(e2) == 0) return "plan from the decks only " + r2;
            // no deck at all: the old answer
            var e3 = Make(new int[0], false, false);
            e3.Game.DeckInfos.Clear();
            e3.C.Group(C("op", "create", "group", "B"));
            e3.C.Target(C("group", "B", "target_id", 100));
            if (e3.C.Plan(C("group", "B")) != "!no_aircraft") return "no aircraft and no deck";
            // the jammer is never ordered: only EA-6B loadouts on the deck
            var e4 = Make(new[] { 1, 2 });
            e4.Game.DeckInfos[0].Rows.RemoveAll(rw => rw.Type == "usn_a-6e");
            e4.C.Plan(C("group", "A"));
            if (OrderCount(e4) != 0) return "a jammer was ordered";
            return null;
        }

        private static string Err()
        {
            // no_ammo of the heavy load: the next offer of the same class with as many missiles on the other deck replaces it; the refused loadout rests for DeckNoAmmoS
            var e = Make(new[] { 1, 2 }, true);
            e.Deck.OrderErr["usn_a-6e|AntiShipHeavy"] = "no_ammo";
            e.C.Tick(1001);
            e.C.Plan(C("group", "A"));
            var errLine = e.Log.FirstOrDefault(l => l.StartsWith("[Strike] strike deck err grp=A ", StringComparison.Ordinal));
            if (errLine == null || !errLine.Contains(" err=no_ammo")) return "err line " + errLine;
            if (e.Deck.Orders.Count(q => q.DeckUid == 700 && q.Loadout == "AntiShipHeavy") != 1 || !e.Deck.Orders.Any(q => q.DeckUid == 701 && q.Loadout == "AntiShipHeavy")) return "tried " + string.Join(",", e.Deck.Orders.Select(q => q.DeckUid + q.Loadout));
            if (!e.C.Groups[0].DeckOrders.Any(o => o.State == "failed" && o.Err == "no_ammo")) return "failed order state";
            if (e.C.Groups[0].DeckOrders.Where(o => o.State == "failed").Any(o => o.Live)) return "a failed order is not live";
            int heavy = e.Deck.Orders.Count(q => q.Loadout == "AntiShipHeavy");
            e.Game.Time += 100; e.C.Tick(e.Game.Time);
            e.C.Plan(C("group", "A"));
            if (e.Deck.Orders.Count(q => q.Loadout == "AntiShipHeavy") != heavy) return "the refused loadout is asked again within the pause";
            // no_ammo with the launches off (beta): launches_disabled, the deck is not offered any more
            var b = Make(new[] { 1, 2 });
            b.Game.DeckInfos[0].LaunchesDisabled = true;
            var offer = StrikeDeckPlanner.Offers(new StrikeDeckInput { Decks = b.Game.DeckInfos, Target = new StrikeShip { Lat = 41, Lon = 10 }, P = new StrikeParams() }, out var rej);
            if (offer.Count != 0 || !rej.All(o => o.Why == "launches_disabled")) return "launches disabled offers";
            var b2 = Make(new[] { 1, 2 });
            b2.Deck.OrderErr["usn_a-6e|AntiShipHeavy"] = "no_ammo"; b2.Deck.OrderErr["usn_a-6e|AntiShip"] = "no_ammo";
            b2.Deck.OnOrder = q => b2.Game.DeckInfos[0].LaunchesDisabled = true;   // the flag turns on after the offers were made
            b2.C.Tick(1001);
            b2.C.Plan(C("group", "A"));
            if (!b2.C.Groups[0].DeckOrders.Any(o => o.Err == "launches_disabled")) return "no_ammo on a deck with the launches off reads launches_disabled " + string.Join(",", b2.C.Groups[0].DeckOrders.Select(o => o.Err));
            b2.Deck.OrderErr.Clear(); b2.Game.DeckInfos[0].LaunchesDisabled = false;
            b2.C.Plan(C("group", "A"));
            int cnt700 = b2.Deck.Orders.Count();
            b2.C.Plan(C("group", "A"));
            if (b2.Deck.Orders.Count() != cnt700) return "idempotent after the pause";
            // no_airframes: the whole type of that deck rests
            var f = Make(new[] { 1, 2 });
            f.Deck.OrderErr["usn_a-6e|AntiShipHeavy"] = "no_airframes"; f.Deck.OrderErr["usn_a-6e|AntiShip"] = "no_airframes";
            f.C.Tick(1001);
            f.C.Plan(C("group", "A"));
            int n1 = OrderCount(f);
            if (n1 == 0) return "no_airframes: nothing tried";
            f.Game.Time += 100; f.C.Tick(f.Game.Time);
            f.C.Plan(C("group", "A"));
            if (OrderCount(f) != n1) return "no_airframes is asked again in 100 s";
            f.Game.Time += 600; f.C.Tick(f.Game.Time);
            f.C.Plan(C("group", "A"));
            if (OrderCount(f) <= n1) return "no_airframes is not asked after 600 s";
            // unknown_loadout is a defect of the mod, written to the log
            var u = Make(new[] { 1, 2 });
            u.Deck.OrderErr["usn_a-6e|AntiShipHeavy"] = "unknown_loadout";
            u.C.Plan(C("group", "A"));
            if (!u.Log.Any(l => l.StartsWith("[Strike] strike deck defect unknown_loadout deck=700 type=usn_a-6e lo=AntiShipHeavy", StringComparison.Ordinal))) return "defect line";
            return null;
        }

        private static string Cancel()
        {
            var e = Make(new[] { 1, 2 });
            e.C.Tick(1001);
            e.C.Plan(C("group", "A"));
            var g = e.C.Groups[0];
            var first = g.DeckOrders[0];
            int total = g.DeckOrders.Count;
            // a foreign task of the same deck (a CAP key) is in the game: no path of the window touches it
            e.Game.Deck.Add(new StrikeDeckItem { DeckUid = 700, Key = "task:999", Type = "usn_f-14", Loadout = "CAP", State = "ready_up", DeckName = "Kitty Hawk", Lat = 38, Lon = 10 });
            string r = e.C.DeckCancel(C("group", "A", "key", first.Key));
            if (StrikeController.IsErr(r)) return "cancel " + r;
            var aborts = e.Deck.Aborts.ToList();
            if (aborts.Count != 1 || aborts[0].Key != first.Key || aborts[0].Window != "strike" || aborts[0].DeckUid != 700) return "abort of this key only " + aborts.Count;
            if (g.DeckBlock != "player" || first.State != "cancelled" || g.DeckKeys.Contains(first.Key)) return "block / state " + g.DeckBlock + " " + first.State;
            int orders = OrderCount(e);
            e.C.Plan(C("group", "A"));
            if (OrderCount(e) != orders) return "an order after the cancel of the player (the block)";
            // order again: the block is lifted and the plan is counted again
            r = e.C.DeckReorder(C("group", "A"));
            if (StrikeController.IsErr(r) || g.DeckBlock != null) return "reorder " + r;
            if (OrderCount(e) <= orders) return "reorder orders again " + OrderCount(e) + " vs " + orders;
            // the answers of the command
            if (e.C.DeckCancel(C("group", "A", "key", "task:nope")) != "!no_order|task:nope") return "no_order";
            if (e.C.DeckCancel(C("group", "Z")) != "!no_group" || e.C.DeckCancel(C()) != "!bad_args|group") return "group errors";
            var live = g.DeckOrders.First(o => o.Live);
            live.State = "launch";
            if (e.C.DeckCancel(C("group", "A", "key", live.Key)) != "!launched|" + live.Key) return "launched";
            live.State = "preparing";
            // a new target: the block goes, the orders of the old target that have not launched are aborted
            e.Game.AddKidd(101, 41.2, 10.2);
            e.C.DeckCancel(C("group", "A"));
            if (g.DeckBlock != "player") return "block again";
            int abortsBefore = e.Deck.Aborts.Count();
            r = e.C.Target(C("group", "A", "target_id", 101));
            if (StrikeController.IsErr(r) || g.DeckBlock != null) return "target change lifts the block " + r;
            // everything was cancelled already; order again and change the target while the orders are alive
            e.C.DeckReorder(C("group", "A"));
            int liveN = g.DeckOrders.Count(o => o.Live);
            if (liveN == 0) return "orders before the change of the target";
            e.C.Target(C("group", "A", "target_id", 100));
            if (g.DeckOrders.Any(o => o.Live)) return "live orders after the change of the target " + g.DeckOrders.Count(o => o.Live);
            if (!e.Log.Any(l => l.Contains("strike deck cancel grp=A") && l.Contains("why=target_changed"))) return "target_changed log";
            // the plan is cancelled: the orders that have not launched go
            e.C.DeckReorder(C("group", "A"));
            if (!g.DeckOrders.Any(o => o.Live)) return "orders before the cancel of the plan";
            e.C.Cancel(C("group", "A"));
            if (g.DeckOrders.Any(o => o.Live)) return "live orders after strike_cancel";
            // a group that is disbanded leaves nothing on the decks
            e.C.DeckReorder(C("group", "A"));
            e.C.Group(C("op", "disband", "group", "A"));
            if (e.Deck.Aborts.Count() <= abortsBefore) return "disband aborts";
            // only the own keys were ever aborted
            var own = new HashSet<string>(e.Deck.Orders.Select((q, i) => "task:" + (100 + i).ToString(CultureInfo.InvariantCulture)));
            if (e.Deck.Aborts.Any(a => a.Key == "task:999" || !own.Contains(a.Key))) return "a foreign key was aborted " + string.Join(",", e.Deck.Aborts.Select(a => a.Key));
            // the task launched meanwhile: deck_task_not_found keeps the key for the adoption
            var e2 = Make(new[] { 1, 2 });
            e2.C.Plan(C("group", "A"));
            var o2 = e2.C.Groups[0].DeckOrders[0];
            e2.Deck.AbortErr[o2.Key] = "deck_task_not_found";
            e2.C.DeckCancel(C("group", "A", "key", o2.Key));
            if (!e2.C.Groups[0].DeckKeys.Contains(o2.Key) || o2.State == "cancelled" || o2.State == "aborted") return "deck_task_not_found keeps the key " + o2.State;
            return null;
        }

        private static StrikeDeckItem Item(string key, string state, double remain = 100)
        {
            return new StrikeDeckItem { DeckUid = 700, Key = key, Type = "usn_a-6e", Loadout = "AntiShipHeavy", State = state, DeckName = "Kitty Hawk", RemainS = remain, Lat = 38, Lon = 10 };
        }

        private static string Tick()
        {
            var e = Make(new[] { 1, 2 });
            e.C.Tick(1001);
            e.C.Plan(C("group", "A"));
            var g = e.C.Groups[0];
            var ord = g.DeckOrders.First();
            string key = ord.Key;
            var item = Item(key, "ready_up");
            e.Game.Deck.Add(item);
            // the states follow the game
            e.Game.Time = 1010; e.C.Tick(1010);
            if (ord.State != "preparing") return "preparing " + ord.State;
            item.State = "await_deck"; e.Game.Time = 1011; e.C.Tick(1011);
            if (ord.State != "await_lift") return "await_lift " + ord.State;
            item.State = "launching"; e.Game.Time = 1012; e.C.Tick(1012);
            if (ord.State != "launch") return "launch " + ord.State;
            // the tick makes no plan, no probe and no time-of-flight call
            int tof = e.Game.TofCalls, plans = e.Log.Count(l => l.StartsWith("[Strike] strike plan ", StringComparison.Ordinal));
            for (int i = 0; i < 10; i++) { e.Game.Time = 1013 + i; e.C.Tick(1013 + i); }
            if (e.Game.TofCalls != tof || e.Log.Count(l => l.StartsWith("[Strike] strike plan ", StringComparison.Ordinal)) != plans || e.Log.Count(l => l.StartsWith("[Strike] strike deck plan ", StringComparison.Ordinal)) != 1) return "the tick plans or probes";
            // the aircraft is in the air: the task is gone, the aircraft is adopted
            e.Game.Deck.Remove(item);
            var a = e.Game.AddPlane(20, "usn_a-6e", "usn_agm-84c", 4, 38.1, 10);
            e.Game.Time = 1030; e.C.Tick(1030);
            if (ord.State != "in_group" || ord.Uid != 20 || !g.Members.Contains(20) || g.DeckKeys.Contains(key)) return "adopted " + ord.State + " " + ord.Uid;
            // the other orders of the same plan (if any) are not in the group yet: no deck_ready
            foreach (var o in g.DeckOrders.Where(x => x.Live).ToList()) { var other = Item(o.Key, "ready_up"); e.Game.Deck.Add(other); }
            // late: ready 360 + DeckLateS 600 from the order
            var e2 = Make(new[] { 1, 2 });
            e2.C.Tick(1001);
            e2.C.Plan(C("group", "A"));
            var g2 = e2.C.Groups[0];
            var o2 = g2.DeckOrders[0];
            e2.Game.Deck.Add(Item(o2.Key, "ready_up"));
            double ordered = o2.OrderedT, ready = o2.ReadyS;
            e2.Game.Time = ordered + ready + 599; e2.C.Tick(e2.Game.Time);
            if (o2.State == "failed") return "599 s after the ready time is not late";
            e2.Game.Time = ordered + ready + 600; e2.C.Tick(e2.Game.Time);
            if (o2.State != "failed" || o2.Err != "late") return "late " + o2.State + " " + o2.Err;
            if (g2.Deck.Aircraft != 0 || g2.DeckKeys.Contains(o2.Key) == false) return "a late order is out of the count but the key stays " + g2.Deck.Aircraft;
            // the target is sunk by other means: the orders that have not launched are aborted
            var e3 = Make(new[] { 1, 2 });
            e3.C.Tick(1001);
            e3.C.Plan(C("group", "A"));
            var g3 = e3.C.Groups[0];
            foreach (var o in g3.DeckOrders) e3.Game.Deck.Add(Item(o.Key, "ready_up"));
            e3.Game.FateMap[100] = "sunk";
            e3.Game.Time = 1020; e3.C.Tick(1020);
            if (g3.DeckOrders.Any(o => o.Live) || !e3.Log.Any(l => l.Contains("strike deck cancel grp=A") && l.Contains("target_sunk"))) return "target sunk " + string.Join(",", g3.DeckOrders.Select(o => o.State));
            // deck_ready: every live order is in the group and the group has gathered at its point: the plan goes stale, no recount and no launch
            var e4 = Make(new[] { 1, 2 });
            e4.C.Tick(1001);
            e4.C.Plan(C("group", "A"));
            var g4 = e4.C.Groups[0];
            if (g4.Timeline == null || g4.Timeline.Status != "draft" || !g4.WaitPt.HasValue) return "draft plan with a wait point";
            foreach (var o in g4.DeckOrders) e4.Game.Deck.Add(Item(o.Key, "ready_up"));
            foreach (var p in e4.Game.Air) { p.Lat = g4.WaitPt.Value.Lat; p.Lon = g4.WaitPt.Value.Lon; }
            e4.Game.Time = 1010; e4.C.Tick(1010);
            int uid = 30;
            foreach (var o in g4.DeckOrders.Where(x => x.Live).ToList())
            {
                e4.Game.Deck.RemoveAll(x => x.Key == o.Key);
                e4.Game.AddPlane(uid++, "usn_a-6e", "usn_agm-84c", 4, g4.WaitPt.Value.Lat, g4.WaitPt.Value.Lon);
            }
            int plansBefore = e4.Log.Count(l => l.StartsWith("[Strike] strike plan ", StringComparison.Ordinal));
            for (int i = 0; i < 12; i++) { e4.Game.Time = 1020 + i; e4.C.Tick(1020 + i); }
            if (g4.DeckOrders.Any(o => o.Live) || g4.DeckOrders.Any(o => o.State != "in_group")) return "all adopted " + string.Join(",", g4.DeckOrders.Select(o => o.State));
            if (g4.Timeline == null || g4.Timeline.Status != "stale" || g4.Timeline.StaleWhy != "deck_ready") return "stale deck_ready " + (g4.Timeline == null ? "no plan" : g4.Timeline.Status + ":" + g4.Timeline.StaleWhy + " gather=" + g4.Gather);
            if (e4.Log.Count(l => l.StartsWith("[Strike] strike plan ", StringComparison.Ordinal)) != plansBefore || g4.Timeline.Status == "executing") return "no recount and no launch";
            if (!g4.Deck.Notes.Contains("deck_ready")) return "note deck_ready";
            return null;
        }

        private static string Load()
        {
            var e = Make(new[] { 1, 2 });
            e.C.Tick(1001);
            e.C.Plan(C("group", "A"));
            if (e.C.Groups[0].DeckOrders.Count == 0) return "orders";
            string json = e.C.Export();
            if (json == null || json.Contains("deck_orders") || json.Contains("task:1") || json.Contains("DeckBlock") || json.IndexOf("block", StringComparison.OrdinalIgnoreCase) >= 0) return "the save holds orders " + json;
            // a new session: the game keeps its tasks, the window does not know their keys: the group is blocked until "order again"
            var e2 = Make(new[] { 1, 2 }, false, false);
            string err;
            e2.C.Import(json, out err);
            var g = e2.C.Groups.FirstOrDefault();
            if (g == null || g.DeckBlock != "loaded") return "block loaded " + err;
            e2.C.Tick(1001);
            e2.C.Plan(C("group", "A"));
            if (OrderCount(e2) != 0) return "an order while blocked after the load";
            e2.C.DeckReorder(C("group", "A"));
            if (OrderCount(e2) == 0 || g.DeckBlock != null) return "reorder after the load";
            // a mission reset forgets the orders, the block and the pauses
            e2.C.ResetMission();
            if (g.DeckOrders.Count != 0 || g.DeckBlock != null || g.Deck != null) return "reset";
            return null;
        }

        private static string View()
        {
            var e = Make(new[] { 1, 2 });
            e.C.Tick(1001);
            e.C.Plan(C("group", "A"));
            e.Game.Deck.Add(Item(e.C.Groups[0].DeckOrders[0].Key, "ready_up"));
            e.C.Tick(1010);
            var o = Obj(e.C.ViewJson(20));
            var gj = (Dictionary<string, object>)Arr(o["groups"])[0];
            var orders = Arr(gj["deck_orders"]);
            if (orders.Count == 0) return "deck_orders";
            var first = (Dictionary<string, object>)orders[0];
            foreach (string k in new[] { "key", "deck", "deck_uid", "type", "loadout", "display", "n", "missiles", "ammo", "ready_s", "state", "err" }) if (!first.ContainsKey(k)) return "order field " + k;
            if ((string)first["state"] != "preparing" || (string)first["type"] != "usn_a-6e") return "order values";
            var dp = (Dictionary<string, object>)gj["deck_plan"];
            foreach (string k in new[] { "block", "kind", "aircraft", "missiles", "last_ready_s", "now", "with", "all", "short_after_all", "stock", "rejected", "notes" }) if (!dp.ContainsKey(k)) return "deck_plan field " + k;
            var with = (Dictionary<string, object>)dp["with"]; var now = (Dictionary<string, object>)dp["now"];
            if (!with.ContainsKey("ok") || !with.ContainsKey("n") || !with.ContainsKey("p") || (bool)now["ok"]) return "eval blocks";
            if (Arr(dp["stock"]).Count == 0 || Arr(dp["notes"]).Count == 0) return "stock and notes";
            if (!gj.ContainsKey("deck") || !(gj["deck"] is List<object>)) return "the v1 deck list stays";
            string raw = e.C.ViewJson(20);
            if (raw.Contains("limit") || raw.Contains("max_limit")) return "hidden state in the view";
            // the window view
            var wv = e.C.View().Groups[0];
            if (wv.DeckOrders.Count == 0 || !wv.HasDeckPlan || wv.DeckAircraft == 0) return "window view";
            // the warning of the variant
            var cards = e.C.View().Groups[0].Cards;
            if (cards.Count > 0 && !cards[0].Warnings.Any(w => w.StartsWith("deck_wait:", StringComparison.Ordinal))) return "deck_wait warning in the card";
            return null;
        }

        private static string AdoptUid()
        {
            // two aircraft of the type take off from the deck: the one of the task, not the look-alike of another window that is nearer to the deck
            foreach (bool withUid in new[] { true, false })
            {
                var e = Make(new[] { 1, 2 });
                e.C.Tick(1001);
                e.C.Plan(C("group", "A"));
                var g = e.C.Groups[0];
                string key = g.DeckOrders[0].Key;
                var item = Item(key, "launching");
                e.Game.Deck.Add(item);
                var others = g.DeckOrders.Skip(1).Select(o => Item(o.Key, "launching")).ToList();
                e.Game.Deck.AddRange(others);
                e.C.Tick(1010);
                var near = e.Game.AddPlane(40, "usn_a-6e", "usn_agm-84c", 4, 38.05, 10);   // nearer to the deck
                var ours = e.Game.AddPlane(41, "usn_a-6e", "usn_agm-84c", 4, 38.4, 10);
                item.VehicleUid = withUid ? 41 : 0;
                e.C.Tick(1011);
                e.Game.Deck.Remove(item); foreach (var x in others) e.Game.Deck.Remove(x);
                e.Game.Time = 1020; e.C.Tick(1020);
                int want = withUid ? 41 : 40;
                if (!g.Members.Contains(want) || g.DeckOrders[0].Uid != want) return (withUid ? "with the task's aircraft " : "without ") + "adopted " + string.Join(",", g.Members) + " order uid " + g.DeckOrders[0].Uid;
            }
            return null;
        }

        // ---- K1: the late answer of the broker
        private static string LateReply()
        {
            // (a) the player cancels while the broker is silent; the late answer carries a key: it is aborted at once and never kept
            var e = Make(new[] { 1, 2 });
            e.Deck.Hold = true;
            e.C.Tick(1001);
            e.C.Plan(C("group", "A"));
            var g = e.C.Groups[0];
            if (e.Deck.Pending.Count == 0 || g.DeckOrders.Any(o => o.Key != null)) return "orders are waiting for the answer " + e.Deck.Pending.Count;
            e.C.Cancel(C("group", "A"));
            if (g.DeckOrders.Any(o => o.Live)) return "no live order after the cancel";
            e.Deck.ReleaseAll();
            var ab = e.Deck.Aborts.Where(q => q.Reason == "strike_late_reply").ToList();
            if (ab.Count == 0 || ab.Any(q => q.Key == null || !q.Key.StartsWith("task:", StringComparison.Ordinal))) return "late reply aborts " + ab.Count;
            if (g.DeckKeys.Count != 0) return "the late key is not kept " + g.DeckKeys.Count;
            if (e.C.ViewJson(10).Contains("\"allowed\":true")) return "the late key is not allowed";
            if (!e.Log.Any(l => l.Contains("-> late_reply"))) return "late_reply log";
            // (b) the same after failed no_reply (31 s without the answer)
            var e2 = Make(new[] { 1, 2 });
            e2.Deck.Hold = true;
            e2.C.Tick(1001);
            e2.C.Plan(C("group", "A"));
            var g2 = e2.C.Groups[0];
            int n2 = e2.Deck.Pending.Count;
            if (n2 == 0) return "(b) orders";
            e2.C.Tick(1033);
            if (g2.DeckOrders.Any(o => o.Live) || !g2.DeckOrders.Any(o => o.State == "failed" && o.Err == "no_reply")) return "(b) failed no_reply " + string.Join(",", g2.DeckOrders.Select(o => o.State));
            e2.Deck.ReleaseAll();
            if (e2.Deck.Aborts.Count(q => q.Reason == "strike_late_reply") != n2 || g2.DeckKeys.Count != 0) return "(b) late answers aborted " + e2.Deck.Aborts.Count();
            // (c) the answer in time is the normal path
            var e3 = Make(new[] { 1, 2 });
            e3.Deck.Hold = true;
            e3.C.Tick(1001);
            e3.C.Plan(C("group", "A"));
            e3.C.Tick(1011);
            e3.Deck.ReleaseAll();
            var g3 = e3.C.Groups[0];
            if (e3.Deck.Aborts.Any() || g3.DeckKeys.Count == 0 || g3.DeckOrders.Any(o => o.Key == null)) return "(c) in time: key kept, no abort";
            return null;
        }

        // ---- K2: the silent broker is not asked again at every replan
        private static string NoReplyBackoff()
        {
            var e = Make(new[] { 1, 2 }, true);
            e.Deck.Hold = true;
            var g = e.C.Groups[0];
            double t0 = 1001;
            e.C.Tick(t0);
            var orderTimes = new List<double>();
            var byKey = new Dictionary<string, HashSet<double>>();
            int seen = 0;
            for (double t = t0; t <= t0 + 900; t += 10)
            {
                e.Game.Time = t;
                e.C.Tick(t);
                if (((int)(t - t0)) % 30 == 0) e.C.Plan(C("group", "A"));
                int now = e.Deck.Orders.Count();
                if (now > seen)
                {
                    for (int k = seen; k < now; k++)
                    {
                        var q = e.Deck.Orders.ElementAt(k); string key = q.DeckUid + "|" + q.Type;
                        if (!byKey.ContainsKey(key)) byKey[key] = new HashSet<double>();
                        byKey[key].Add(t - t0); orderTimes.Add(t - t0);
                    }
                    seen = now;
                }
            }
            // per (deck, type): 0, after 60, after 120 more, after 240 more: at most 4 orders in 900 s (without the pause: one at every replan = 30)
            foreach (var kv in byKey)
                if (kv.Value.Count > 4) return "deck " + kv.Key + " was asked in " + kv.Value.Count + " passes in 900 s: " + string.Join(",", kv.Value);
            if (byKey.Values.Any(v => v.Count < 2)) return "the deck is asked again after the pause";
            if (!e.Log.Any(l => l.Contains("err=no_reply") && l.Contains("next=none backoff_s=60"))) return "first backoff 60 " + string.Join(" | ", e.Log.Where(l => l.Contains("no_reply")).Take(4));
            if (!e.Log.Any(l => l.Contains("err=no_reply") && l.Contains("next=none backoff_s=120"))) return "second backoff 120";
            if (e.Log.Any(l => l.Contains("strike deck err") && l.Contains("err=no_reply") && !l.Contains("next=none"))) return "no substitute after no_reply";
            // the broker is back after the pause: the normal order with a key
            e.Deck.Hold = false;
            e.Deck.ReleaseAll();
            e.Game.Time = t0 + 2000;
            e.C.Tick(t0 + 2000);
            int before = e.Deck.Orders.Count();
            e.C.Plan(C("group", "A"));
            if (e.Deck.Orders.Count() == before && !g.DeckOrders.Any(o => o.Key != null && o.Live)) return "an order after the pause " + string.Join(",", g.DeckOrders.Select(o => o.State));
            return null;
        }
    }
}
