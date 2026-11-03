using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    internal sealed class FakeDeck : IAirDeck
    {
        public List<DeckRequest> Reqs = new List<DeckRequest>();
        public List<string> Declared = new List<string>();
        public bool Held; public string Err; public double HeldFor = -1;
        public void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids) { Declared.Add(window + ":" + prio.Name + ":" + string.Join(",", deckUids)); }
        public int Submit(DeckRequest r, Action<DeckResult> done) { Reqs.Add(r); done(new DeckResult { Ticket = Reqs.Count, Err = Err, Held = Held }); return Reqs.Count; }
        public double HeldS(string window, string group) { return HeldFor; }
        public bool DeckDown(int deckUid) { return false; }
    }

    // T-341 phase B stand sections "t341 <scenario>": the ownership, the wait point and the orbit, the fuel, the deck, the veto rule, cancel / choose, the window view, the save, the ring.
    internal static class StrikeT341BTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }

        public static string Dispatch(string n)
        {
            switch (n)
            {
                case "own-claim": return OwnClaim();
                case "own-release": return OwnRelease();
                case "wait-gather": return WaitGather();
                case "wait-orbit": return WaitOrbit();
                case "wait-sam": return WaitSam();
                case "wait-fuel": return WaitFuel();
                case "wait-leave": return WaitLeave();
                case "wait-notarget": return WaitNoTarget();
                case "deck-allow": return DeckAllow();
                case "deck-adopt": return DeckAdopt();
                case "deck-gap": return DeckGap();
                case "veto-members": return VetoMembers();
                case "cancel-choose": return CancelChoose();
                case "view-window": return ViewWindow();
                case "save-load": return SaveLoad();
                case "ring-window": return RingWindow();
            }
            return StrikeT341ExecTests.Dispatch(n);
        }

        private static StrikeController Make(StrikeFakeGame game, List<string> log, out AirOwnership own, bool withDeck = false)
        {
            var c = new StrikeController(game, new StrikeParams(), log.Add);
            var o = new AirOwnership();
            own = o;
            c.Ownership = o;
            c.OwnerOf = uid => { string w; string g = o.Owner(uid, out w); return g == null || w == "strike" ? null : w + ":" + g; };
            return c;
        }

        private static StrikeController Ready(StrikeFakeGame game, List<string> log, out AirOwnership own, params int[] uids)
        {
            var c = Make(game, log, out own);
            c.Group(C("op", "create", "group", "A", "uids", uids.Length == 0 ? new[] { 1, 2 } : uids));
            c.Target(C("group", "A", "target_id", 100));
            return c;
        }

        private static int Count(List<string> l, string prefix) { return l.Count(x => x.StartsWith(prefix, StringComparison.Ordinal)); }

        // ---- G4 ownership
        private static string OwnClaim()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own; string w;
            var c = Make(game, log, out own);
            own.Claim(1, "cap", "ПВО-1");
            c.Group(C("op", "create", "group", "A"));
            string r = c.Group(C("op", "add", "group", "A", "uids", new[] { 1 }));
            if (r != "!in_group|cap:ПВО-1") return "add of a CAP aircraft " + r;
            r = c.Group(C("op", "add", "group", "A", "uids", new[] { 2 }));
            if (StrikeController.IsErr(r) || own.Owner(2, out w) != "A" || w != "strike") return "claim " + r;
            // the claim alone (no resolver) refuses too
            var c2 = new StrikeController(game, new StrikeParams(), s => { }); c2.Ownership = own;
            c2.Group(C("op", "create", "group", "B"));
            if (c2.Group(C("op", "add", "group", "B", "uids", new[] { 2 })) != "!in_group|A") return "claim vs another controller " + c2.Group(C("op", "add", "group", "B", "uids", new[] { 2 }));
            if (c.GroupNameOf(2) != "A" || c.GroupNameOf(4) != null) return "GroupNameOf";
            return null;
        }

        private static string OwnRelease()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own; string w;
            var c = Ready(game, log, out own);
            int seq = own.GroupSeq("strike", "A");
            c.Group(C("op", "remove", "group", "A", "uids", new[] { 1 }));
            if (own.Owner(1, out w) != null) return "remove releases";
            c.Group(C("op", "rename", "group", "A", "name", "Hammer"));
            if (own.Owner(2, out w) != "Hammer" || own.GroupSeq("strike", "Hammer") != seq) return "rename moves the claims and the number";
            int calls = game.Calls.Count;
            c.Release(2);
            if (own.Owner(2, out w) != null || c.IsMember(2, out w)) return "Release";
            if (game.Calls.Count != calls) return "Release gives no orders";
            if (!log.Any(l => l == "[Strike] strike leave Hammer uid=2 why=released")) return "release log";
            c.Group(C("op", "add", "group", "Hammer", "uids", new[] { 1, 2 }));
            c.Group(C("op", "disband", "group", "Hammer"));
            if (own.Owner(1, out w) != null || own.Owner(2, out w) != null) return "disband releases";
            c.Release(77);   // not a member: nothing
            return null;
        }

        // ---- G5 wait point, orbit, fuel
        private static StrikeAircraft A(StrikeFakeGame g, int uid) { return g.Air.First(a => a.Uid == uid); }

        private static string WaitGather()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Ready(game, log, out own);
            var g = c.Groups[0];
            if (!g.WaitPt.HasValue || g.WaitWhy != "ok") return "wait point " + g.WaitWhy;
            var kidd = game.Fleet[0];
            double d = AswMath.DistNm(g.WaitPt.Value, new GeoPt(kidd.Lat, kidd.Lon));
            if (d < 90 + 10 + 5 - 0.01) return "inside the SAM zone + standoff + orbit: " + d;
            if (g.WaitPt.Value.Lat >= 41) return "the point is on the reverse axis (south of the target)";
            game.Calls.Clear();
            c.Tick(1000);
            if (Count(game.Calls, "goto 1 ") != 1 || Count(game.Calls, "goto 2 ") != 1) return "one goto each " + string.Join("|", game.Calls);
            c.Tick(1001); c.Tick(1002);
            if (game.Calls.Count != 2) return "no repeated goto " + string.Join("|", game.Calls);
            if (!log.Any(l => l.StartsWith("[Strike] strike wait A pt=", StringComparison.Ordinal) && l.Contains(" why=ok r_nm=5.0"))) return "wait log " + string.Join(" | ", log);
            if (!log.Any(l => l == "[Strike] strike gather A uid=1 -> goto")) return "gather log";
            if (c.Groups[0].Gather != "gather") return "status gather " + c.Groups[0].Gather;
            // game clock, not the wall clock: the same second twice is one tick
            game.Calls.Clear();
            c.Tick(1002.2);
            if (game.Calls.Count != 0) return "tick throttle";
            // a refused order is retried after a while, not every second
            game.GotoErr = "waypoint_failed"; A(game, 1).Lat += 0.0; var c2 = new StrikeController(game, new StrikeParams(), log.Add);
            c2.Group(C("op", "create", "group", "B", "uids", new[] { 4 })); c2.Target(C("group", "B", "target_id", 100));
            game.Calls.Clear();
            c2.Tick(2000); c2.Tick(2001); c2.Tick(2002);
            if (Count(game.Calls, "goto 4 ") != 1) return "a refused goto is not repeated every second " + string.Join("|", game.Calls);
            c2.Tick(2040);
            if (Count(game.Calls, "goto 4 ") != 2) return "a refused goto is retried after 30 s";
            return null;
        }

        private static string WaitOrbit()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Ready(game, log, out own);
            var wp = c.Groups[0].WaitPt.Value;
            foreach (int uid in new[] { 1, 2 }) { A(game, uid).Lat = wp.Lat; A(game, uid).Lon = wp.Lon; }
            game.Calls.Clear();
            c.Tick(1000);
            if (Count(game.Calls, "orbit 1") != 1 || Count(game.Calls, "orbit 2") != 1 || Count(game.Calls, "goto") != 0) return "orbit at the point " + string.Join("|", game.Calls);
            if (c.Groups[0].Gather != "waiting") return "status waiting " + c.Groups[0].Gather;
            // on the circle (5 nm from the centre) the aircraft is still on the orbit
            A(game, 1).Lat = wp.Lat + 5.0 / 60; A(game, 2).Lat = wp.Lat - 5.0 / 60;
            game.Calls.Clear();
            c.Tick(1001); c.Tick(1002);
            if (game.Calls.Count != 0) return "an aircraft on its orbit gets no new order " + string.Join("|", game.Calls);
            // a plan, a cancel, an add or a remove while the group flies about does not drift the point (the axis starts at the group)
            A(game, 1).Lat = wp.Lat + 8.0 / 60; A(game, 2).Lat = wp.Lat + 9.0 / 60; A(game, 2).Lon = wp.Lon - 0.2;
            c.Plan(C("group", "A")); c.Cancel(C("group", "A")); c.Group(C("op", "add", "group", "A", "uids", new[] { 4 })); c.Group(C("op", "remove", "group", "A", "uids", new[] { 4 }));
            if (AswMath.DistNm(c.Groups[0].WaitPt.Value, wp) > 0.01) return "the wait point drifted " + AswMath.DistNm(c.Groups[0].WaitPt.Value, wp);
            // it was pushed far away: back to the point
            A(game, 1).Lat = wp.Lat + 30.0 / 60;
            c.Tick(1003);
            if (Count(game.Calls, "goto 1 ") != 1) return "far from the orbit: goto again " + string.Join("|", game.Calls);
            return null;
        }

        private static string WaitSam()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Ready(game, log, out own);
            c.Tick(1000);
            var wp0 = c.Groups[0].WaitPt.Value;
            game.Calls.Clear();
            // a new cruiser with a long SAM appears near the wait point
            var cruiser = game.AddKidd(101, wp0.Lat + 0.4, wp0.Lon + 0.3); cruiser.SamMaxNm = 60;
            c.Tick(1003);
            if (AswMath.DistNm(c.Groups[0].WaitPt.Value, wp0) > 0.01) return "no check inside 5 s";
            c.Tick(1006);
            var wp1 = c.Groups[0].WaitPt.Value;
            if (AswMath.DistNm(wp1, wp0) < 2) return "the point moved";
            foreach (var s in game.Fleet) if (AswMath.DistNm(wp1, new GeoPt(s.Lat, s.Lon)) < s.SamMaxNm + 15 - 0.01) return "the new point is inside the zone of ship " + s.TrackId;
            if (!log.Any(l => l.Contains(" strike wait A pt=") && l.EndsWith("trigger=new_sam", StringComparison.Ordinal))) return "log new_sam " + string.Join(" | ", log);
            c.Tick(1007);
            if (Count(game.Calls, "goto 1 ") != 1) return "a new goto after the move " + string.Join("|", game.Calls);
            return null;
        }

        private static string WaitFuel()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own; string w;
            var c = Ready(game, log, out own);
            c.Plan(C("group", "A"));
            var a = A(game, 1);
            a.FuelFracLive = true; a.FuelFrac = 0.5; a.MaxRangeNm = 1800; a.ReserveNm = 9;
            game.Calls.Clear();
            c.Tick(1000);
            if (Count(game.Calls, "rtb 1") != 0) return "9 nm > 5 nm margin: no return";
            a.ReserveNm = 4;
            c.Tick(1001);
            if (Count(game.Calls, "rtb 1") != 1 || c.IsMember(1, out w) || own.Owner(1, out w) != null) return "margin: rtb, out of the group, released " + string.Join("|", game.Calls);
            if (c.Groups[0].Left.Count != 1 || c.Groups[0].Left[0].Why != "fuel_margin") return "left fuel_margin";
            if (!log.Any(l => l == "[Strike] strike leave A uid=1 why=fuel_margin")) return "leave log";
            if (c.Groups[0].Timeline.Status != "stale") return "the plan is stale after a member left";
            // a live fraction 0.08 and no path: the hard floor
            var b = A(game, 2);
            b.FuelFracLive = true; b.FuelFrac = 0.08; b.MaxRangeNm = 0; b.ReserveNm = -1;
            c.Tick(1002);
            if (c.Groups[0].Left.Count != 2 || c.Groups[0].Left[1].Why != "fuel_hard") return "fuel_hard";
            return null;
        }

        private static string WaitLeave()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own; string w;
            var c = Ready(game, log, out own);
            A(game, 1).Missiles[0].Count = 0;
            game.Calls.Clear();
            c.Tick(1000);
            if (Count(game.Calls, "rtb 1") != 1 || c.Groups[0].Left.All(l => l.Why != "no_missiles")) return "no_missiles " + string.Join("|", game.Calls);
            game.Air.RemoveAll(x => x.Uid == 2);
            c.Tick(1001);
            if (c.IsMember(2, out w) || c.Groups[0].Left.All(l => l.Why != "lost") || Count(game.Calls, "rtb 2") != 0) return "lost aircraft is dropped without an order";
            if (own.Owner(2, out w) != null) return "lost aircraft is released";
            return null;
        }

        private static string WaitNoTarget()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Make(game, log, out own);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1 }));
            var g = c.Groups[0];
            if (!g.WaitPt.HasValue) return "no target: the point comes from the nearest hostile with a SAM";
            var kidd = game.Fleet[0];
            if (AswMath.DistNm(g.WaitPt.Value, new GeoPt(kidd.Lat, kidd.Lon)) < 105 - 0.01) return "inside the zone";
            // no hostile ships at all: no point, status idle, nothing is ordered
            var game2 = new StrikeFakeGame(); game2.AddPlane(1, "usn_a-6e", "usn_agm-84c", 4);
            var c2 = new StrikeController(game2, new StrikeParams(), s => { });
            c2.Group(C("op", "create", "group", "A", "uids", new[] { 1 }));
            c2.Tick(1000);
            if (c2.Groups[0].WaitPt.HasValue || c2.Groups[0].WaitWhy != "no_hostiles" || game2.Calls.Count != 0 || c2.Groups[0].Gather != "idle") return "no hostiles";
            // over land the point shifts
            var game3 = StrikeT341CtlTests.Game();
            var probe = new StrikeController(game3, new StrikeParams(), s => { });
            probe.Group(C("op", "create", "group", "A", "uids", new[] { 1 })); probe.Target(C("group", "A", "target_id", 100));
            var w0 = probe.Groups[0].WaitPt.Value;
            game3.Land = p => AswMath.DistNm(p, w0) < 3;
            var c3 = new StrikeController(game3, new StrikeParams(), s => { });
            c3.Group(C("op", "create", "group", "A", "uids", new[] { 1 })); c3.Target(C("group", "A", "target_id", 100));
            if (c3.Groups[0].WaitWhy != "land_shifted") return "land_shifted " + c3.Groups[0].WaitWhy;
            return null;
        }

        // ---- G6 deck
        private static StrikeDeckItem Item(string key, string type = "usn_a-6e", double lat = 39.0)
        {
            var it = new StrikeDeckItem { DeckUid = 700, Key = key, Type = type, Loadout = "ASM", State = "ready_up", RemainS = 300, Lat = lat, Lon = 10, DeckName = "Kitty Hawk" };
            it.Missiles.Add(new StrikeMissile { Ammo = "usn_agm-84c", Name = "AGM-84C", Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", Count = 4, MinNm = 3.5, MaxNm = 65, SpeedKn = 540, CruiseAltFt = 50, Operable = true });
            StrikeAirMath.Classify(new[] { new StrikeAircraft { Missiles = it.Missiles } });
            return it;
        }

        private static string DeckAllow()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Make(game, log, out own);
            var deck = new FakeDeck(); c.Deck = deck;
            game.Deck.Add(Item("task:5"));
            c.Group(C("op", "create", "group", "A"));
            c.Group(C("op", "create", "group", "B"));
            c.Target(C("group", "A", "target_id", 100));
            if (c.Group(C("op", "add", "group", "A", "deck_keys", new[] { "task:99" })) != "!no_aircraft") return "unknown key";
            string r = c.Group(C("op", "add", "group", "A", "deck_keys", new[] { "task:5" }));
            if (StrikeController.IsErr(r) || Arr(Obj(r)["added_keys"]).Count != 1) return "add key " + r;
            if (c.Group(C("op", "add", "group", "B", "deck_keys", new[] { "task:5" })) != "!deck_busy|A") return "busy key";
            game.Deck[0].Why = "loadout_unknown";
            if (c.Group(C("op", "add", "group", "B", "deck_keys", new[] { "task:5" })) != "!not_striker|loadout_unknown") return "a task with an unknown loadout is not a striker";
            game.Deck[0].Why = null;
            c.Tick(1000);
            if (deck.Reqs.Count != 1 || deck.Reqs[0].Window != "strike" || deck.Reqs[0].Kind != DeckKind.Allow || deck.Reqs[0].Key != "task:5" || deck.Reqs[0].DeckUid != 700 || deck.Reqs[0].Group != "A") return "allow request";
            if (deck.Reqs[0].Prio == null || deck.Reqs[0].Prio.Order != "strike" || deck.Reqs[0].Prio.Name != "A") return "prio";
            if (!log.Any(l => l == "[Strike] strike deck A key=task:5 -> allow")) return "allow log " + string.Join(" | ", log);
            if (deck.Declared.Count == 0 || !deck.Declared[0].StartsWith("strike:A:700", StringComparison.Ordinal)) return "declared " + string.Join(",", deck.Declared);
            c.Tick(1001); c.Tick(1002);
            if (deck.Reqs.Count != 1) return "one allow per key";
            if (c.Groups[0].Gather != "gather") return "the group gathers while a deck task is pending";
            // held by the broker: the log says so
            var game2 = StrikeT341CtlTests.Game(); var log2 = new List<string>(); AirOwnership own2;
            var c2 = Make(game2, log2, out own2); var deck2 = new FakeDeck { Held = true, HeldFor = 12 }; c2.Deck = deck2;
            game2.Deck.Add(Item("task:6"));
            c2.Group(C("op", "create", "group", "A", "deck_keys", new[] { "task:6" }));
            c2.Tick(1000);
            if (!log2.Any(l => l == "[Strike] strike deck A key=task:6 -> held")) return "held log";
            if (c2.View().Groups[0].DeckHeldS != 12) return "held seconds in the view";
            // a refused allow is asked again (the key stays)
            var game3 = StrikeT341CtlTests.Game(); var log3 = new List<string>(); AirOwnership own3;
            var c3 = Make(game3, log3, out own3); var deck3 = new FakeDeck { Err = "launch_failed" }; c3.Deck = deck3;
            game3.Deck.Add(Item("task:7"));
            c3.Group(C("op", "create", "group", "A", "deck_keys", new[] { "task:7" }));
            c3.Tick(1000); c3.Tick(1001);
            if (deck3.Reqs.Count != 2 || !log3.Any(l => l == "[Strike] strike deck A key=task:7 -> launch_failed")) return "refused allow is repeated " + deck3.Reqs.Count;
            // the plan counts only the aircraft in the air: a warning for the deck one
            c.Target(C("group", "A", "target_id", 100));
            c.Group(C("op", "add", "group", "A", "uids", new[] { 1, 2 }));
            var o = Obj(c.Plan(C("group", "A")));
            var w = Arr(((Dictionary<string, object>)Arr(o["variants"])[0])["warnings"]).Select(x => (string)x).ToList();
            if (!w.Contains("deck_pending:1")) return "deck_pending warning " + string.Join(";", w);
            return null;
        }

        private static string DeckAdopt()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own; string w;
            var c = Make(game, log, out own);
            var deck = new FakeDeck(); c.Deck = deck;
            game.Deck.Add(Item("task:5"));
            c.Group(C("op", "create", "group", "A", "deck_keys", new[] { "task:5" }));
            c.Target(C("group", "A", "target_id", 100));
            c.Tick(1000);   // allow
            // the task is gone; an aircraft of the same type appears near the deck (uid 4 was in the air before: not ours)
            game.Deck.Clear();
            game.AddPlane(50, "usn_a-6e", "usn_agm-84c", 4, 39.0, 10.01);
            game.AddPlane(51, "usn_f-14", "usn_aim-9", 2, 39.0, 10.0).Missiles[0].TargetType = "AAW";
            StrikeAirMath.Classify(game.Air);
            game.Calls.Clear();
            c.Tick(1001);
            if (!c.IsMember(50, out w) || w != "A") return "adopted uid 50";
            if (c.IsMember(4, out w) || c.IsMember(51, out w)) return "a known aircraft and another type are not taken";
            if (own.Owner(50, out w) != "A" || w != "strike") return "claimed";
            if (c.Groups[0].DeckKeys.Count != 0) return "the key is spent";
            if (!log.Any(l => l == "[Strike] strike adopt A key=task:5 uid=50")) return "adopt log " + string.Join(" | ", log);
            c.Tick(1002);
            if (Count(game.Calls, "goto 50 ") != 1) return "the adopted aircraft flies to the wait point " + string.Join("|", game.Calls);
            // an aircraft of another automation is never adopted
            var game2 = StrikeT341CtlTests.Game(); var log2 = new List<string>(); AirOwnership own2;
            var c2 = Make(game2, log2, out own2); c2.Deck = new FakeDeck();
            game2.Deck.Add(Item("task:5"));
            c2.Group(C("op", "create", "group", "A", "deck_keys", new[] { "task:5" }));
            c2.Tick(1000); game2.Deck.Clear();
            game2.AddPlane(60, "usn_a-6e", "usn_agm-84c", 4, 39.0, 10.0);
            own2.Claim(60, "cap", "ПВО-1");
            c2.Tick(1001);
            if (c2.IsMember(60, out w)) return "an owned aircraft is not adopted";
            return null;
        }

        private static string DeckGap()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Make(game, log, out own); c.Deck = new FakeDeck();
            game.Deck.Add(Item("task:5"));
            c.Group(C("op", "create", "group", "A", "deck_keys", new[] { "task:5" }));
            c.Tick(1000); game.Deck.Clear();
            c.Tick(1001);
            if (c.Groups[0].DeckKeys.Count != 1) return "the key waits for the aircraft";
            c.Tick(1300);
            if (c.Groups[0].DeckKeys.Count != 1) return "still inside the gap";
            c.Tick(1700);
            if (c.Groups[0].DeckKeys.Count != 0 || c.Groups[0].Left.All(l => l.Why != "deck_lost")) return "the key is dropped after AdoptGapS";
            // the task vanished before the allow: dropped at once
            var game2 = StrikeT341CtlTests.Game(); var log2 = new List<string>(); AirOwnership own2;
            var c2 = Make(game2, log2, out own2);   // no broker: nothing is asked, so nothing is allowed
            game2.Deck.Add(Item("task:5"));
            c2.Group(C("op", "create", "group", "A", "deck_keys", new[] { "task:5" }));
            c2.Tick(1000); game2.Deck.Clear(); c2.Tick(1001);
            if (c2.Groups[0].DeckKeys.Count != 0 || !log2.Any(l => l == "[Strike] strike deck A key=task:5 -> gone")) return "gone before the allow";
            return null;
        }

        // ---- G7 veto, cancel, choose
        private static string VetoMembers()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Ready(game, log, out own);
            if (!c.ShouldVeto(1, true, false)) return "a member's anti-ship auto attack is vetoed";
            if (c.ShouldVeto(1, false, false)) return "an air-to-air missile is not";
            if (c.ShouldVeto(4, true, false)) return "not a member";
            if (c.ShouldVeto(1, true, true)) return "a launch the mod expects goes through";
            c.Group(C("op", "remove", "group", "A", "uids", new[] { 1 }));
            if (c.ShouldVeto(1, true, false)) return "a removed aircraft is free again";
            return null;
        }

        private static string CancelChoose()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Ready(game, log, out own);
            if (c.Plan(C("group", "A", "choose", 0)) != "!no_plan") return "choose without a plan";
            c.Plan(C("group", "A"));
            int n = c.Groups[0].Variants.Count;
            if (n < 1) return "no variants";
            if (c.Plan(C("group", "A", "choose", n)) != "!bad_args|choose" || c.Plan(C("group", "A", "choose", -1)) != "!bad_args|choose") return "choose out of the range";
            string r = c.Plan(C("group", "A", "choose", n - 1));
            if (StrikeController.IsErr(r) || (double)Obj(r)["chosen"] != n - 1 || c.Groups[0].Chosen != n - 1) return "choose " + r;
            if (c.Groups[0].Timeline.PlanId != 1) return "choose does not make a new plan";
            if (c.Plan(C("group", "Z", "choose", 0)) != "!no_group") return "choose of a missing group";
            var wp = c.Groups[0].WaitPt;
            r = c.Cancel(C("group", "A"));
            var g = c.Groups[0];
            if (StrikeController.IsErr(r) || g.Variants.Count != 0 || g.Chosen != -1 || g.Timeline != null) return "cancel " + r;
            if (!g.WaitPt.HasValue || AswMath.DistNm(g.WaitPt.Value, wp.Value) > 0.01) return "the group keeps its wait point";
            if (c.Cancel(C("group", "Z")) != "!no_group" || c.Cancel(C()) != "!bad_args|group") return "cancel errors";
            if (!log.Any(l => l == "[Strike] strike cancel A")) return "cancel log";
            if (g.Members.Count != 2) return "the members stay in the group";
            return null;
        }

        private static string ViewWindow()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            game.AddKidd(101, 41.1, 10.1, "neutral");
            game.AddKidd(102, 41.1, 10.2, "hostile", true, true, "Merchant");
            game.AddKidd(103, 41.1, 10.3, "friendly");
            game.Deck.Add(Item("task:5"));
            var c = Ready(game, log, out own);
            c.Deck = new FakeDeck();
            c.Group(C("op", "create", "group", "B"));
            c.Group(C("op", "add", "group", "B", "deck_keys", new[] { "task:5" }));
            var v = c.View();
            if (v.Groups.Count != 2 || !v.Ready || v.Estimate != StrikeCalib.Estimate || v.CalibId != StrikeCalib.CalibId || v.Calib != "cache") return "view header";
            var a = v.Groups[0];
            if (a.Name != "A" || a.Status != "idle" || a.Members.Count != 2 || a.MainId != 100 || a.Mode != "single" || !a.WaitPt.HasValue || a.WaitR != 5 || a.WaitWhy != "ok") return "group A";
            if (v.Groups[1].DeckKeys.Count != 1) return "group B deck keys";
            var r1 = v.Air.First(x => x.Uid == 1);
            if (r1.Why != null || r1.Group != "A" || !r1.Airborne || r1.Missiles.Count != 1 || !r1.Missiles[0].EndsWith("x4", StringComparison.Ordinal)) return "row of an aircraft in a group";
            if (v.Air.First(x => x.Uid == 3).Why != "helicopter") return "helicopter grey";
            if (v.Air.First(x => x.Uid == 4).Why != null || v.Air.First(x => x.Uid == 4).Group != null) return "a free striker";
            var dr = v.Air.First(x => x.DeckKey == "task:5");
            if (dr.Uid != 0 || dr.Where != "deck" || dr.ReadyInS != 300 || dr.Group != "B" || dr.Why != null) return "deck row";
            if (v.Air.TakeWhile(x => x.Why == null).Count() != v.Air.Count(x => x.Why == null)) return "strikers first";
            var s0 = v.Ships[0];
            if (s0.Id != 100 || s0.Prio != 1 || s0.Need != 8 || s0.Excl != null || s0.SamNm != 90 || s0.Why != "ad") return "first ship " + s0.Id + " need=" + s0.Need;
            if (v.Ships.Any(x => x.Id == 103)) return "friendly ships are not listed";
            var ex = v.Ships.Where(x => x.Excl != null).ToList();
            if (ex.Count != 2 || ex.First(x => x.Id == 101).Excl != "neutral" || ex.First(x => x.Id == 102).Excl != "civilian") return "excluded " + string.Join(",", ex.Select(x => x.Id + ":" + x.Excl));
            if (v.Ships.TakeWhile(x => x.Excl == null).Count() != v.Ships.Count(x => x.Excl == null)) return "excluded ships last";
            c.Plan(C("group", "A"));
            v = c.View();
            a = v.Groups[0];
            if (a.Status != "draft" || a.Cards.Count < 1 || a.Chosen != 0) return "plan in the view";
            var card = a.Cards[0];
            if (card.Kind != "min_missiles" || card.Missiles != 8 || card.PerTarget.Count != 1 || card.PerTarget[0].N != 8 || card.PerTarget[0].P < 0.9 || card.Red || card.Estimate != StrikeCalib.Estimate || card.Carriers.Count < 2) return "card " + card.Kind + " " + card.Missiles;
            if (card.T <= 0 || card.MainP < 0.9) return "card times";
            if (a.Timeline.Count != card.Carriers.Count || a.Timeline.Any(t => !(t.TArrive > t.TLaunch && t.TLaunch > t.TIssue - 1e-6))) return "timeline";
            if (a.Launches.Count != a.Timeline.Count || double.IsNaN(a.AxisDeg) || !a.TargetPt.HasValue) return "launches and axis";
            // a card below the threshold is red
            var few = StrikeT341CtlTests.Game(); few.Air.RemoveAll(x => x.Uid != 1); few.Air[0].Missiles[0].Count = 3;
            var c2 = new StrikeController(few, new StrikeParams(), s => { });
            c2.Group(C("op", "create", "group", "A", "uids", new[] { 1 })); c2.Target(C("group", "A", "target_id", 100)); c2.Plan(C("group", "A"));
            var rc = c2.View().Groups[0].Cards[0];
            if (!rc.Red || !rc.Warnings.Any(x => x.StartsWith("short n=", StringComparison.Ordinal))) return "red card";
            // json agrees with the view
            var o = Obj(c.ViewJson(20));
            var g0 = (Dictionary<string, object>)Arr(o["groups"])[0];
            foreach (string k in new[] { "wait_why", "left", "deck", "gather" }) if (!g0.ContainsKey(k)) return "json key " + k;
            if (g0["wait_why"] as string != "ok") return "json wait_why";
            return null;
        }

        // ---- G8 save
        private static string SaveLoad()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Ready(game, log, out own);
            c.Plan(C("group", "A"));
            string json = c.Export();
            if (json == null || json.Contains("variants") || json.Contains("timeline") || json.Contains("\"plan")) return "no plan in the save";
            if (new StrikeController(game, new StrikeParams(), s => { }).Export() != null) return "no groups, no file";
            var log2 = new List<string>(); AirOwnership own2;
            game.Calls.Clear();
            var c2 = Make(game, log2, out own2);
            game.Air.RemoveAll(x => x.Uid == 2);   // one member is gone after the load
            string err;
            string r = c2.Import(json, out err);
            if (r != "n=1" || err != null) return "import " + r + " " + err;
            var g = c2.Groups[0];
            if (g.Name != "A" || g.Members.Count != 1 || g.Members[0] != 1 || g.Target.MainId != 100 || !g.WaitPt.HasValue) return "group after the load";
            if (g.Variants.Count != 0 || g.Timeline == null || g.Timeline.Status != "stale" || g.Timeline.StaleWhy != "loaded" || g.Chosen != -1) return "stale, loaded";
            string w;
            if (own2.Owner(1, out w) != "A" || w != "strike") return "claim after the load";
            if (game.Calls.Count != 0) return "no order before the first tick " + string.Join("|", game.Calls);
            if (!log2.Any(l => l == "[Strike] strike load n=1 stale=loaded")) return "load log";
            c2.Tick(1000);
            if (Count(game.Calls, "goto 1 ") != 1) return "the first tick sends the aircraft to the wait point " + string.Join("|", game.Calls);
            if (c2.Plan(C("group", "A")) == null || c2.Groups[0].Timeline.Status != "draft") return "a new plan after the load";
            if (c2.Import("{broken", out err) != null || err == null) return "broken file";
            if (c2.Import(json, out err) != "n=0") return "a group that exists is not imported twice";
            return null;
        }

        // ---- J1 ring
        private sealed class RingWin : IRingGroupWindow
        {
            private readonly StrikeController _c;
            public RingWin(StrikeController c) { _c = c; }
            public string Window { get { return "strike"; } }
            public bool Ready { get { return _c.RingReady; } }
            public IList<RingGroupOption> Groups() { return _c.RingGroups(); }
            public string GroupOf(int uid) { return _c.RingGroupOf(uid); }
            public string AdmitWhy(int uid, string group, string iw, string ig) { return _c.AdmitWhy(uid, group, iw, ig); }
            public bool Add(int uid, string group) { string r = _c.Group(C("op", "add", "group", group, "uids", new[] { uid })); return !StrikeController.IsErr(r); }
            public bool Remove(int uid, string group) { string r = _c.Group(C("op", "remove", "group", group, "uids", new[] { uid })); return !StrikeController.IsErr(r); }
        }

        private sealed class OtherWin : IRingGroupWindow
        {
            public Dictionary<int, string> Mem = new Dictionary<int, string>();
            public string Window { get { return "cap"; } }
            public bool Ready { get { return true; } }
            public IList<RingGroupOption> Groups() { return new List<RingGroupOption> { new RingGroupOption { Window = "cap", Name = "C1", Kind = "cap", Members = Mem.Count } }; }
            public string GroupOf(int uid) { string g; return Mem.TryGetValue(uid, out g) ? g : null; }
            public string AdmitWhy(int uid, string group, string iw, string ig) { return null; }
            public bool Add(int uid, string group) { Mem[uid] = group; return true; }
            public bool Remove(int uid, string group) { Mem.Remove(uid); return true; }
        }

        private static string RingWindow()
        {
            var game = StrikeT341CtlTests.Game(); var log = new List<string>(); AirOwnership own;
            var c = Make(game, log, out own);
            c.Group(C("op", "create", "group", "A")); c.Group(C("op", "create", "group", "B", "uids", new[] { 2 }));
            var cap = new OtherWin(); cap.Mem[4] = "C1";
            var wins = new List<IRingGroupWindow> { cap, new RingWin(c) };
            var v = RingGroups.View(1, wins);
            if (v == null || v.Current != null || v.Options.Count != 3) return "view of a free striker " + (v == null ? "null" : v.Options.Count.ToString());
            if (v.Options[0].Kind != "cap" || v.Options[1].Kind != "asuw" || v.Options[1].Name != "A" || v.Options[1].Why != null || v.Options[2].Members != 1) return "options " + string.Join(",", v.Options.Select(o => o.Window + ":" + o.Name + ":" + o.Kind));
            if (c.RingGroups().Count != 2 || c.RingGroups()[0].Kind != "asuw" || c.RingGroups()[0].Window != "strike") return "RingGroups";
            // a helicopter: the strike groups are hidden for it
            var vh = RingGroups.View(3, wins);
            if (vh == null || vh.Options.Any(o => o.Window == "strike")) return "helicopter sees no strike group";
            if (c.AdmitWhy(3, "A", null, null) != "hidden") return "helicopter hidden";
            // join, move from the CAP window and back (the old group is left with the rollback rule), leave
            cap.Mem[1] = "C1";
            var res = RingGroups.Join(1, wins, "strike", "A");
            if (!res.Ok || c.RingGroupOf(1) != "A" || cap.GroupOf(1) != null) return "move from the CAP window " + res.Json();
            string w;
            if (own.Owner(1, out w) != "A" || w != "strike") return "claimed by the ring join";
            res = RingGroups.Join(1, wins, "strike", "B");
            if (!res.Ok || c.RingGroupOf(1) != "B") return "move between strike groups " + res.Json();
            res = RingGroups.Leave(1, wins, "strike", "B");
            if (!res.Ok || c.RingGroupOf(1) != null) return "leave " + res.Json();
            // an aircraft of the CAP window is refused unless the ring is leaving that group
            if (c.AdmitWhy(4, "A", null, null) != null) return "uid 4 is not claimed in this rig";
            own.Claim(4, "cap", "C1");
            if (c.AdmitWhy(4, "A", null, null) != "in_group:C1") return "a CAP aircraft is in_group " + c.AdmitWhy(4, "A", null, null);
            if (c.AdmitWhy(4, "A", "cap", "C1") != null) return "leaving its group makes it free";
            if (c.AdmitWhy(1, "Z", null, null) != "unknown_group" || c.AdmitWhy(999, "A", null, null) != "not_found") return "unknown group / aircraft";
            c.Ready = false;
            if (RingGroups.View(1, wins) != null || RingGroups.Join(1, wins, "strike", "A").Error != "not_ready") return "not ready";
            return null;
        }
    }
}
