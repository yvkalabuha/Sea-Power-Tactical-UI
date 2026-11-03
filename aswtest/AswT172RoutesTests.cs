using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VoiceBotBridge
{
    // T-172: regressions of the ASW review fixes (2026-10-22) - routes.
    internal static class AswT172RoutesTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }

        private static object G(AswController c, int i = 0)
        {
            var l = (IList)typeof(AswController).GetField("_groups", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(c);
            return l[i];
        }

        private static T F<T>(object o, string n) { return (T)o.GetType().GetField(n, BindingFlags.Public | BindingFlags.Instance).GetValue(o); }

        private static string RectJson(double[] r) { return "[" + string.Join(",", r.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]"; }

        private static int Outside(IEnumerable<GeoPt> pts, GeoPt ctr, double north, double east)
        {
            int n = 0;
            foreach (var p in pts) { var q = AswMath.ToLocal(ctr, 0, p); if (Math.Abs(q.X) > north / 2 + 0.01 || Math.Abs(q.Y) > east / 2 + 0.01) n++; }
            return n;
        }

        private static string TacticOf(AswController c, int uid)
        {
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) if (r.Uid == uid) return r.Tactic;
            return null;
        }

        public static string Run()
        {
            string e;
            if ((e = Rect()) != null) return "rect: " + e;
            if ((e = BigPackage()) != null) return "big package: " + e;
            if ((e = Refused()) != null) return "refused orders: " + e;
            if ((e = MixedPassive()) != null) return "mixed passive types: " + e;
            if ((e = ReplanThrash()) != null) return "replan thrash: " + e;
            if ((e = Dump()) != null) return "dump: " + e;
            if ((e = Leaving()) != null) return "leaving: " + e;
            if ((e = ReserveTakesOver()) != null) return "reserve takes over: " + e;
            if ((e = HolderStays()) != null) return "holder stays: " + e;
            if ((e = HolderNoTorpedo()) != null) return "holder without torpedoes: " + e;
            if ((e = OrderChange()) != null) return "order change: " + e;
            return null;
        }

        // ---- B3: at most 40 points per order; a refused order is not repeated every tick ----

        private static string BigPackage()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", passive: 80));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchArea(P("{\"bearing\":0,\"dist_nm\":30,\"size_nm\":40,\"tactics\":[\"rows\"]}"));
            Run(c, f, 5);
            if (f.Drops.Count == 0) return "no package";
            foreach (var d in f.Drops) if (d.Pts.Count > AswMath.MaxDropPoints) return "package of " + d.Pts.Count + " points";
            // course: 80 buoys, the rows package is far under 40 already; the cross of a wide formation too
            var f2 = new AswFakeGame();
            f2.Mem.Add(AswFakeGame.Helo(1, "air", passive: 200));
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c2.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c2, f2, 5);
            foreach (var d in f2.Drops) if (d.Pts.Count > AswMath.MaxDropPoints) return "course package of " + d.Pts.Count + " points";
            return null;
        }

        private static string Refused()
        {
            var f = new AswFakeGame { DropError = "bad_args" };
            f.Mem.Add(AswFakeGame.Helo(1, "air", passive: 40));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 120);
            int clears = f.Calls.Count(x => x == "clear:1");
            if (f.Drops.Count > 5 || clears > 5) return "drop repeated: calls " + f.Drops.Count + " clears " + clears;
            if (f.Drops.Count == 0) return "no drop attempt at all";
            var f2 = new AswFakeGame { DipError = "bad_args" };
            f2.Mem.Add(AswFakeGame.Helo(1, "air", dip: true, passive: 0));
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c2.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c2, f2, 120);
            if (f2.Dips.Count > 5 || f2.Dips.Count == 0) return "dip calls " + f2.Dips.Count;
            var f3 = new AswFakeGame { GotoError = "bad_args" };
            var mh = AswFakeGame.Helo(1, "air", passive: 0); mh.Dipping = false; mh.Mad = true; f3.Mem.Add(mh);
            var c3 = Make(f3);
            c3.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c3.SearchCourse(P("{\"tactics\":[\"mad\"]}"));
            Run(c3, f3, 120);
            int gotos = f3.Calls.Count(x => x == "goto:1");
            if (gotos > 5 || gotos == 0) return "mad goto calls " + gotos;
            // area dip refused
            var f4 = new AswFakeGame { DipError = "bad_args" };
            f4.Mem.Add(AswFakeGame.Helo(1, "air", dip: true, passive: 0));
            var c4 = Make(f4);
            c4.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c4.SearchArea(P("{\"bearing\":0,\"dist_nm\":5,\"size_nm\":20,\"tactics\":[\"dip\"]}"));
            Run(c4, f4, 120);
            if (f4.Dips.Count > 5 || f4.Dips.Count == 0) return "area dip calls " + f4.Dips.Count;
            return null;
        }

        // ---- B4: two passive types on one aircraft (SH-3H 1972-79): the count of the very type decides ----

        private static string MixedPassive()
        {
            var f = new AswFakeGame();
            var h = AswFakeGame.Helo(1, "air", passive: 16); h.Vlad = 0; h.ActiveBuoys = 0; h.EnduranceS = 1e7; h.FuelFrac = 1;
            h.BuoysByAmmo = new Dictionary<string, int> { { "usn_ssq-41", 12 }, { "usn_ssq-53", 4 } };
            f.Mem.Add(h);
            f.TypesOf[1] = new List<BuoyType>
            {
                new BuoyType { Ammo = "usn_ssq-41", Sensor = "SSQ-41_Sonar", PassiveKm = 10, LifeS = 3600 },
                new BuoyType { Ammo = "usn_ssq-53", Sensor = "SSQ-53_Sonar", PassiveKm = 20, LifeS = 28800 }
            };
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 120);
            if (f.Drops.Count == 0) return "no drop in 120 s";
            if (f.Drops.Count > 2) return "too many drop calls in 120 s: " + f.Drops.Count;
            if (f.Drops[0].Ammo != "usn_ssq-53" || f.Drops[0].Pts.Count > 4) return "first package " + f.Drops[0].Ammo + " x" + f.Drops[0].Pts.Count;
            Run(c, f, 2400);
            if (f.Logs.Exists(l => l.Contains("not_enough") || l.Contains("drop_failed"))) return "warning: " + f.Logs.Find(l => l.StartsWith("asw warn"));
            bool second = false; foreach (var d in f.Drops) if (d.Ammo == "usn_ssq-41") second = true;
            if (!second) return "the SSQ-41 package never came: " + string.Join(" | ", f.Logs.Where(l => l.StartsWith("asw drop") || l.StartsWith("asw warn")).Select(l => l.Length > 160 ? l.Substring(0, 160) : l));
            foreach (var d in f.Drops) if (d.Ammo == "usn_ssq-53" && d.Pts.Count > 4) return "SSQ-53 package of " + d.Pts.Count;
            return null;
        }

        // ---- live 0.29.0 (Invader 1): a wobbling escort axis re-planned every 15 s and cancelled the package before the first buoy fell ----

        private static string ReplanThrash()
        {
            var f = new AswFakeGame { DropDelayS = 200, DropEveryS = 60, SimBuoys = true };
            var h = AswFakeGame.Helo(1, "air", passive: 40); h.EnduranceS = 1e7; h.FuelFrac = 1; h.SpeedKn = 250; f.Mem.Add(h);   // T-334: this fake aircraft never moves on its package, the ETA check of a slow one would cut it again and again
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            for (int i = 0; i < 1500; i++)
            {
                f.Tgt.CourseDeg = (i / 20) % 2 == 0 ? 90 : 125;   // the escort weaves 35 degrees every 20 s
                f.Advance(1); f.Tick(c);
            }
            int idle = f.Logs.Count(l => l.Contains(" cancelled ") && l.Contains("fell=0"));
            if (idle > 1) return idle + " packages cancelled before a single buoy fell";
            if (f.Fallen.Count < 4) return "only " + f.Fallen.Count + " buoys fell in 25 min";
            return null;
        }

        // ---- B5: "drop the rest" once per sortie, inside the zone ----

        private static string Dump()
        {
            var f = new AswFakeGame { DropDelayS = 150, DropEveryS = 60, SimBuoys = true };
            var h = AswFakeGame.Helo(1, "air", passive: 40); h.FuelFrac = 0.2; f.Mem.Add(h);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 1500);
            int dumps = f.Logs.Count(l => l.StartsWith("asw drop G dump"));
            int clears = f.Calls.Count(x => x == "clear:1");
            if (dumps > 1) return "dump lines " + dumps;
            if (clears > 8) return "ClearRoute calls " + clears;   // T-176: the package is cut to what the fuel pays for (Capacity), so a low aircraft lays several small ones
            if (f.Fallen.Count == 0) return "no buoy fell";
            // area: the dump goes to the next points of the grid, inside the rectangle
            var f2 = new AswFakeGame { DropDelayS = 150, DropEveryS = 60, SimBuoys = true };
            var h2 = AswFakeGame.Helo(1, "air", passive: 40); h2.FuelFrac = 0.2; f2.Mem.Add(h2);
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            var ctr = new GeoPt(f2.Tgt.Lat + 0.3, f2.Tgt.Lon);
            c2.SearchArea(P("{\"rect\":" + RectJson(AswController.RectAround(ctr, 20, 6)) + ",\"tactics\":[\"rows\"]}"));
            Run(c2, f2, 1500);
            if (f2.Logs.Count(l => l.StartsWith("asw drop G dump")) > 1) return "area dump lines";
            var all = new List<GeoPt>(); foreach (var d in f2.Drops) all.AddRange(d.Pts);
            if (all.Count == 0 || Outside(all, ctr, 6, 20) != 0) return "area dump outside the rectangle";
            return null;
        }

        // ---- B9: an airborne reserve takes the tactic of a holder that is released or still on deck ----

        private static string ReserveTakesOver()
        {
            // P7: the holder runs low and lands; the second helicopter is airborne in reserve
            var f = new AswFakeGame();
            var a = AswFakeGame.Helo(1, "air"); a.FuelFrac = 0.2; f.Mem.Add(a);
            var b = AswFakeGame.Helo(2, "air"); b.FuelFrac = 1.0; f.Mem.Add(b);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            double landed = -1, first2 = -1, took = -1;
            for (int i = 0; i < 4800; i++)
            {
                f.Advance(1); f.Tick(c);
                if (landed < 0 && a.Where != "air") landed = f.Now;
                if (first2 < 0 && f.Drops.Exists(d => d.Uid == 2)) first2 = f.Now;
                if (took < 0 && TacticOf(c, 2) == "rows") took = f.Now;
            }
            if (first2 < 0) return "P7: the reserve never dropped";
            if (landed >= 0 && took > landed + 5) return "P7: the reserve took the tactic " + (took - landed) + " s after the landing";
            // P12: the tactic sits on a machine in maintenance on deck while an armed one is airborne
            var f2 = new AswFakeGame();
            var d1 = AswFakeGame.Helo(1, "deck_service", passive: 40); d1.ReadyInS = 2700; f2.Mem.Add(d1);
            var d2 = AswFakeGame.Helo(2, "air", passive: 30); d2.FuelFrac = 1; f2.Mem.Add(d2);
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c2.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            double first = -1;
            for (int i = 0; i < 3600; i++) { f2.Advance(1); f2.Tick(c2); if (first < 0 && f2.Drops.Count > 0) first = i; }
            if (first < 0 || first > 60) return "P12: first drop after " + first + " s";
            if (f2.Drops[0].Uid != 2) return "P12: first drop by uid " + f2.Drops[0].Uid;
            if (f2.Calls.Count(x => x == "goto:2") > 1) return "P12: standby orders " + f2.Calls.Count(x => x == "goto:2");
            if (f2.Logs.Count(l => l.StartsWith("asw assign")) > 6) return "P12: assignment flip-flop";
            return null;
        }

        // ---- B7: the helicopter that holds a contact is not pulled away by the screen line of its tactic ----

        private static string HolderStays()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true, passive: 0));
            f.Queue = uid => f.Route.ContainsKey(uid) ? f.Route[uid].Count : 0;   // crude: the queue never empties by itself
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c, f, 60);
            var sub = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 7);
            f.Subs.Add(new AswContact { Id = 77, Name = "S1", Lat = sub.Lat, Lon = sub.Lon, CourseDeg = 90, SpeedKn = 0 });
            int k0 = f.Dips.Count;
            for (int i = 0; i < 1500; i++) { f.Advance(1); f.Tick(c); if (i % 200 == 199) f.Route.Remove(1); }
            int screen = 0, onContact = 0;
            // T-176: the chain stands on the axis (north offset from the contact 0), the dip on the contact is abeam of its course at the spook radius (2.3)
            for (int i = k0; i < f.Dips.Count; i++) { double north = Math.Abs(AswMath.ToLocal(sub, 0, f.Dips[i][0]).X); if (north < 0.5) screen++; else if (Math.Abs(AswMath.DistNm(f.Dips[i][0], sub) - 2.3) < 0.7) onContact++; }
            if (screen != 0) return screen + " screen lines while the contact was held";
            if (onContact == 0) return "the contact was never held";
            f.Subs.Clear();
            int k1 = f.Dips.Count;
            for (int i = 0; i < 700; i++) { f.Advance(1); f.Tick(c); if (i % 200 == 199) f.Route.Remove(1); }
            bool back = false; for (int i = k1; i < f.Dips.Count; i++) if (Math.Abs(AswMath.ToLocal(sub, 0, f.Dips[i][0]).X) < 0.5) back = true;
            if (!back) return "the screen line did not come back after the contact was lost";
            return null;
        }

        // ---- strategy B5: a holder of a live contact without torpedoes is not taken off ----

        private static string HolderNoTorpedo()
        {
            var f = new AswFakeGame();
            var h = AswFakeGame.Helo(1, "air", dip: true, passive: 0); h.Torpedoes = 0; h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h);
            f.Queue = uid => f.Route.ContainsKey(uid) ? f.Route[uid].Count : 0;
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c, f, 60);
            var sub = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 7);
            f.Subs.Add(new AswContact { Id = 77, Name = "S1", Lat = sub.Lat, Lon = sub.Lon, CourseDeg = 90, SpeedKn = 0 });
            Run(c, f, 20);
            f.Calls.Clear();
            for (int i = 0; i < 600; i++) { f.Advance(1); f.Tick(c); if (i % 200 == 199) f.Route.Remove(1); }
            if (TacticOf(c, 1) != "dip") return "tactic " + TacticOf(c, 1);
            if (f.Logs.Exists(l => l.StartsWith("asw standby"))) return "sent to a waiting point";
            return null;
        }

        // ---- live 0.29.0: after an order changes, stops or the group is disbanded, the old zone / route is not drawn any more ----

        private static AswGroupView GroupView(AswController c, string name)
        {
            foreach (var g in c.View().Groups) if (g.Name == name) return g;
            return null;
        }

        private static string OrderChange()
        {
            var f = new AswFakeGame { SimBuoys = true };
            var h = AswFakeGame.Helo(1, "air", passive: 100); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            var ctr = new GeoPt(f.Tgt.Lat + 0.3, f.Tgt.Lon);
            var rectA = AswController.RectAround(ctr, 20, 6);
            c.SearchArea(P("{\"rect\":" + RectJson(rectA) + ",\"tactics\":[\"rows\"]}"));
            Run(c, f, 20);
            var v = GroupView(c, "G");
            if (v.Rect == null || v.Order != "area") return "area order not shown";
            // live 0.29.0: an area order did not read "2+2" in the member table
            if (v.MemberRows.Count == 0 || AswWindowModel.MemberTacticCell(v.MemberRows[0]) == UiText.Get("asw.cell.corridor") || AswWindowModel.MemberTacticText(v.MemberRows[0]) == UiText.Get("asw.tactic.corridor")) return "an area order reads as the corridor: " + AswWindowModel.MemberTacticCell(v.MemberRows[0]);
            // area -> course
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            v = GroupView(c, "G");
            if (v.MemberRows.Count > 0 && AswWindowModel.MemberTacticCell(v.MemberRows[0]) != UiText.Get("asw.cell.corridor")) return "a course order does not read the corridor: " + AswWindowModel.MemberTacticCell(v.MemberRows[0]);
            if (v.Rect != null) return "the old area is still in the view after a course order";
            if (v.Lines.Count != 0) return "old lines after a course order: " + v.Lines.Count;
            Run(c, f, 20);
            v = GroupView(c, "G");
            foreach (var l in v.Lines) if (l.Kind == "area") return "an area line in a course order";
            // course -> a new area
            var rectB = AswController.RectAround(new GeoPt(ctr.Lat + 0.5, ctr.Lon + 0.5), 10, 10);
            c.SearchArea(P("{\"rect\":" + RectJson(rectB) + ",\"tactics\":[\"rows\"]}"));
            v = GroupView(c, "G");
            if (v.Rect == null || Math.Abs(v.Rect[0] - rectB[0]) > 1e-9) return "the new area is not the one shown";
            if (v.Lines.Count != 0) return "old lines after a new area";
            Run(c, f, 20);
            if (GroupView(c, "G").Buoys.Count == 0) return "no buoys in the water (the test does not test)";
            int buoys = GroupView(c, "G").Buoys.Count;
            // stop: only the buoys stay
            c.Stop(P("{\"group\":\"G\"}"));
            v = GroupView(c, "G");
            if (v.Rect != null || v.Lines.Count != 0) return "zone or lines after stop";
            if (v.Buoys.Count != buoys) return "buoys changed by stop";
            // disband and create again with the same name
            c.Group(P("{\"op\":\"disband\",\"group\":\"G\"}"));
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            v = GroupView(c, "G");
            if (v == null || v.Rect != null || v.Lines.Count != 0 || v.Buoys.Count != 0 || v.Order != "none") return "a re-created group carries the old picture";
            return null;
        }

        // ---- B10: an aircraft the game sends home gets no waiting point, route or package ----

        private static string Leaving()
        {
            foreach (string state in new[] { "BingoFuel", "Winchester", "ReturnToBase" })
            {
                var f = new AswFakeGame();
                var h = AswFakeGame.Helo(1, "air", passive: 40); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h);
                var c = Make(f);
                c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
                c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
                Run(c, f, 120);
                f.Calls.Clear(); f.Drops.Clear();
                h.AiState = state;
                Run(c, f, 900);
                if (f.Calls.Exists(x => x.StartsWith("goto:") || x.StartsWith("clear:")) || f.Drops.Count > 0) return state + ": " + string.Join(",", f.Calls) + " drops=" + f.Drops.Count;
            }
            // the waiting point of an airborne reserve keeps the current height
            var f2 = new AswFakeGame();
            f2.Mem.Add(AswFakeGame.Helo(1, "air", passive: 40)); f2.Mem.Add(AswFakeGame.Helo(2, "air", passive: 40));
            var c2 = new AswController(f2, new AswParams(), new AswConfig { RestoreOnLoad = "off", Loc = new AswLocParams { Lanes = "single" } });   // T-176: with the lanes every aircraft works; "single" keeps the reserve
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c2.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c2, f2, 30);
            if (f2.GotoAlts.Count == 0) return "no waiting point";
            foreach (int a in f2.GotoAlts) if (a != 0) return "waiting point at " + a + " ft";
            return null;
        }

        // ---- B1: the area is east-west x north-south, whatever the order of the corners ----

        private static string Rect()
        {
            foreach (double north in new[] { 6.0, 4.0 })
                for (int order = 0; order < 2; order++)
                {
                    var f = new AswFakeGame { DropDelayS = 0 };
                    f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true, passive: 0));
                    f.Mem.Add(AswFakeGame.Helo(2, "air", passive: 400));
                    var mad = AswFakeGame.Helo(3, "air", passive: 0); mad.Dipping = false; mad.Mad = true; f.Mem.Add(mad);
                    var c = Make(f);
                    c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2,3]}"));
                    var ctr = new GeoPt(f.Tgt.Lat + 0.3, f.Tgt.Lon);
                    var rect = AswController.RectAround(ctr, 20, north);   // east-west 20, north-south `north`
                    if (order == 1) rect = new[] { rect[6], rect[7], rect[0], rect[1], rect[2], rect[3], rect[4], rect[5] };
                    string r = c.SearchArea(P("{\"rect\":" + RectJson(rect) + ",\"tactics\":[\"rows\",\"dip\",\"mad\"]}"));
                    if (AswController.IsErr(r)) return r;
                    Run(c, f, 600);
                    var g = G(c);
                    string tag = " (20x" + north + " order " + order + ")";
                    foreach (string name in new[] { "AreaAll", "AreaDip", "AreaMad" })
                    {
                        var l = F<List<GeoPt>>(g, name);
                        if (l.Count == 0) return name + " is empty" + tag;
                        int o = Outside(l, ctr, north, 20);
                        if (o != 0) return name + ": " + o + " of " + l.Count + " points outside the rectangle" + tag;
                    }
                    var dropped = new List<GeoPt>(); foreach (var d in f.Drops) dropped.AddRange(d.Pts);
                    if (dropped.Count == 0) return "nothing dropped" + tag;
                    if (Outside(dropped, ctr, north, 20) != 0) return "dropped points outside the rectangle" + tag;
                    foreach (var d in f.Dips) if (Outside(d, ctr, north, 20) != 0) return "dip points outside the rectangle" + tag;
                }
            return null;
        }
    }
}
