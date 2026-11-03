using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VoiceBotBridge
{
    // T-172: regressions of the ASW review fixes (2026-10-22) - buoys.
    internal static class AswT172BuoysTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }

        private static string RectJson(double[] r) { return "[" + string.Join(",", r.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]"; }

        private const double R53 = 0.5 * 10 / AswMath.KmPerNm, R77 = 0.5 * 20 / AswMath.KmPerNm;

        /// <summary>
        /// The worst distance from a point of the four rows (Y = +-R, +-2.8R from the origin, X from x0 to x1) to the nearest buoy that fell (any
        /// buoy covers the water): above R = a hole of the barrier.
        /// </summary>
        private static double MaxHole(IList<GeoPt> fallen, GeoPt origin, double axis, double r, double x0, double x1, out string where)
        {
            double worst = 0; where = "";
            foreach (double y in new[] { -r, r, -2.8 * r, 2.8 * r })
                for (double x = x0; x <= x1; x += 0.25)
                {
                    double near = AswMath.NearestNm(AswMath.ToGeo(origin, axis, new NmPt(x, y)), fallen);
                    if (near > worst) { worst = near; where = "x=" + x.ToString("0.0") + " y=" + y.ToString("0.0"); }
                }
            return worst;
        }

        public static string Run()
        {
            string e;
            if ((e = ContactCutsPackage()) != null) return "contact cuts a package: " + e;
            if ((e = ShortLoads(10)) != null) return "short loads 10: " + e;
            if ((e = ShortLoads(6)) != null) return "short loads 6: " + e;
            if ((e = MixedTypes()) != null) return "mixed types: " + e;
            if ((e = ContactCross()) != null) return "contact cross: " + e;
            if ((e = RemainingLife()) != null) return "remaining life: " + e;
            if ((e = ActiveBuoy()) != null) return "active buoy: " + e;
            return null;
        }

        // ---- O2: a package cut short by a small load leaves no hole behind the cursor ----

        private static string ShortLoads(int load)
        {
            var f = new AswFakeGame { DropDelayS = 60, DropEveryS = 30, SimBuoys = true };
            for (int u = 1; u <= 3; u++) { var h = AswFakeGame.Helo(u, "air", passive: load); h.EnduranceS = 1e7; h.FuelFrac = 1; h.Vlad = 0; h.ActiveBuoys = 0; f.Mem.Add(h); }
            var c = Make(f);
            var origin = new GeoPt(f.Tgt.Lat, f.Tgt.Lon);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2,3]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 3 * 3600);
            if (f.Fallen.Count < load) return "only " + f.Fallen.Count + " buoys fell";
            double maxX = 0; foreach (var p in f.Fallen) maxX = Math.Max(maxX, AswMath.ToLocal(origin, 90, p).X);
            string where;
            double hole = MaxHole(f.Fallen, origin, 90, R53, 3, maxX - 2 * AswMath.Spacing(R53), out where);
            if (hole > 1.25 * R53) return "a hole: " + hole.ToString("0.00") + " nm from the nearest buoy at " + where + " (" + f.Fallen.Count + " buoys, to x=" + maxX.ToString("0.0") + ")";
            return null;
        }

        // ---- R2: the merge gap between two buoy types is 0.56 (Ra + Rb) ----

        private static string MixedTypes()
        {
            var f = new AswFakeGame { SimBuoys = true };
            var a = AswFakeGame.Helo(1, "air", passive: 12); a.Vlad = 0; a.ActiveBuoys = 0;
            var b = AswFakeGame.Helo(2, "air", passive: 0); b.Vlad = 60; b.ActiveBuoys = 0;
            f.Mem.Add(a); f.Mem.Add(b);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"A\",\"uids\":[1]}"));
            c.Group(P("{\"op\":\"create\",\"group\":\"B\",\"uids\":[2]}"));
            var ctr = new GeoPt(f.Tgt.Lat + 0.3, f.Tgt.Lon);
            string rect = RectJson(AswController.RectAround(ctr, 30, 30));
            c.SearchArea(P("{\"group\":\"A\",\"rect\":" + rect + ",\"tactics\":[\"rows\"]}"));
            c.SearchArea(P("{\"group\":\"B\",\"rect\":" + rect + ",\"tactics\":[\"rows\"]}"));
            Run(c, f, 1200);
            var p53 = new List<GeoPt>(); var p77 = new List<GeoPt>();
            foreach (var d in f.Drops) (d.Ammo == "usn_ssq-77" ? p77 : p53).AddRange(d.Pts);
            if (p53.Count == 0 || p77.Count == 0) return "53: " + p53.Count + " 77: " + p77.Count;
            double want = AswMath.PairGapNm(R53, R77) - 1e-6, worst = double.MaxValue;
            foreach (var x in p53) worst = Math.Min(worst, AswMath.NearestNm(x, p77));
            if (worst < want) return "an SSQ-53 buoy lies " + worst.ToString("0.00") + " nm from an SSQ-77 one, less than " + want.ToString("0.00");
            return null;
        }

        // ---- live 0.29.0: the cross over a fresh contact is not suppressed by the buoys of the search field around it ----

        private static string ContactCross()
        {
            var f = new AswFakeGame { SimBuoys = true };
            var h = AswFakeGame.Helo(1, "air", passive: 120); h.EnduranceS = 1e7; h.FuelFrac = 1; h.Vlad = 0; h.ActiveBuoys = 0; f.Mem.Add(h);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 900);
            // the contact sits among the field buoys that already lie in the water
            var field = f.Fallen[f.Fallen.Count / 2];
            f.Subs.Add(new AswContact { Id = 5, Name = "S5", Lat = field.Lat + 0.01, Lon = field.Lon + 0.01, Source = "buoy" });
            int before = f.Drops.Count;
            Run(c, f, 5);
            var cross = f.Logs.Find(l => l.StartsWith("asw drop G contact"));
            if (cross == null) return "no contact package";
            if (cross.Contains("kept=0")) return "the cross was suppressed: " + cross.Substring(0, Math.Min(200, cross.Length));
            return null;
        }

        // ---- R6: the life of a buoy dropped before the order comes from the game (RemainingLife), not 8 h ----

        private static string RemainingLife()
        {
            var f = new AswFakeGame();
            var h = AswFakeGame.Helo(1, "air", passive: 40); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h);
            var o = new GeoPt(f.Tgt.Lat, f.Tgt.Lon);
            var at = AswMath.ToGeo(o, 90, new NmPt(6, 0));
            f.Buoys.Add(new AswBuoyObs { Id = 1, OwnerUid = 1, Lat = at.Lat, Lon = at.Lon, Ammo = "x", LifeS = 28800, RemainingS = 600 });
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 3);
            var g = ((System.Collections.IList)typeof(AswController).GetField("_groups", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(c))[0];
            var buoys = (System.Collections.IList)g.GetType().GetField("Buoys", BindingFlags.Public | BindingFlags.Instance).GetValue(g);
            foreach (var b in buoys)
            {
                if (!(bool)b.GetType().GetField("Pre").GetValue(b)) continue;
                double life = (double)b.GetType().GetField("LifeS").GetValue(b), at0 = (double)b.GetType().GetField("DroppedS").GetValue(b);
                if (Math.Abs(life - at0 - 600) > 5 && Math.Abs(life - 600) > 5) return "life " + life + " dropped " + at0;
                // a buoy that has no life left covers nothing: the lattice point on top of it is laid
                var f2 = new AswFakeGame();
                var h2 = AswFakeGame.Helo(1, "air", passive: 40); h2.SpeedKn = 250; f2.Mem.Add(h2);   // T-334: fast enough that the ETA filter keeps the node
                var o2 = new GeoPt(f2.Tgt.Lat, f2.Tgt.Lon);
                var pt = AswMath.ToGeo(o2, 90, new NmPt(3, -2.16));
                f2.Buoys.Add(new AswBuoyObs { Id = 1, OwnerUid = 1, Lat = pt.Lat, Lon = pt.Lon, Ammo = "x", RemainingS = 0 });
                var c2 = Make(f2);
                c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
                c2.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
                Run(c2, f2, 3);
                if (f2.Drops.Count == 0) return "dead buoy: nothing laid";
                if (AswMath.NearestNm(pt, f2.Drops[0].Pts) > 0.3) return "a dead buoy still blocked its point";
                return null;
            }
            return "the pre-order buoy was not taken into the group";
        }

        // ---- R9: the active buoy of the cross goes through the land / air-defence filter and is counted ----

        private static string ActiveBuoy()
        {
            for (int hazard = 0; hazard < 2; hazard++)
            {
                var f = new AswFakeGame { DropDelayS = 150, DropEveryS = 30 };
                var h = AswFakeGame.Helo(1, "air", passive: 40); h.EnduranceS = 1e7; h.FuelFrac = 1; h.SpeedKn = 250; f.Mem.Add(h);   // T-334: fast enough to lay row 5
                var o = new GeoPt(f.Tgt.Lat, f.Tgt.Lon);
                var at = AswMath.ToGeo(o, 90, new NmPt(5, 0));   // T-176: the active buoy of the centre of a barrier row only on request (BarrierActive); T-229: the first row stands on x = I = 5
                if (hazard == 1) f.HazardList.Add(new AswHazard(at, 3));
                var c = new AswController(f, new AswParams { Pat = new AswPatternParams { BarrierActive = true } }, new AswConfig { RestoreOnLoad = "off" });
                c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
                c.SearchCourse(P("{\"tactics\":[\"barrier\"]}"));
                Run(c, f, 120);
                int active = f.Drops.Count(d => d.Ammo == "usn_ssq-62");
                int passive = f.Drops.Count(d => d.Ammo != "usn_ssq-62");
                if (passive != 1) return "hazard=" + hazard + ": " + passive + " passive packages in 120 s (the active buoy closed the package?)";
                if (hazard == 1 && active != 0) return "an active buoy was ordered inside an air-defence zone";
                if (hazard == 0)
                {
                    if (active != 1) return "no active buoy";
                    var g = ((System.Collections.IList)typeof(AswController).GetField("_groups", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(c))[0];
                    var buoys = (System.Collections.IList)g.GetType().GetField("Buoys", BindingFlags.Public | BindingFlags.Instance).GetValue(g);
                    bool counted = false; foreach (var b in buoys) if ((string)b.GetType().GetField("Ammo").GetValue(b) == "usn_ssq-62") counted = true;
                    if (!counted) return "the active buoy is not in the group's list";
                }
            }
            return null;
        }

        // ---- O1: a contact cancels the rows package in flight: the cut-off part is laid again, no hole stays in the barrier ----

        private static string ContactCutsPackage()
        {
            var f = new AswFakeGame { DropDelayS = 60, DropEveryS = 30, SimBuoys = true };
            var hh = AswFakeGame.Helo(1, "air", passive: 200); hh.EnduranceS = 1e7; hh.FuelFrac = 1; f.Mem.Add(hh);
            var c = Make(f);
            var origin = new GeoPt(f.Tgt.Lat, f.Tgt.Lon);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 200);
            var tp = new GeoPt(f.Tgt.Lat, f.Tgt.Lon);
            var sub = AswMath.Offset(tp, 130, 5);
            f.Subs.Add(new AswContact { Id = 7, Name = "Sub 7", Lat = sub.Lat, Lon = sub.Lon, Source = "buoy" });
            Run(c, f, 30);
            f.Subs.Clear();
            Run(c, f, 2 * 3600);
            if (f.Logs.Count(l => l.Contains(" cancelled ")) == 0) return "the contact did not cancel anything (the test does not test)";
            string where;
            double hole = MaxHole(f.Fallen, origin, 90, R53, 3, 45, out where);
            if (hole > 1.25 * R53) return "a hole in the barrier: " + hole.ToString("0.00") + " nm from the nearest buoy at " + where + ", more than 1.25R=" + (1.25 * R53).ToString("0.00");
            return null;
        }
    }
}
