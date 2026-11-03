using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    // T-250 (ADR-0102 follow-up): the birds of a small CAP zone keep an equal spacing on the shared circle (phase plan + radius correction).
    // Scenarios of `captest t250 <name>`.
    internal static class CapT250Tests
    {
        private static readonly string[] Names = { "phase_angle", "plan_even", "plan_rank", "plan_bounds", "route_from_ahead", "converge_flown", "reissue_rate", "phase_log", "t244_kept" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private static double Nm(GeoPt a, GeoPt b) { return AswMath.DistNm(a, b); }

        public static string Run(string only = null)
        {
            foreach (var n in Names)
            {
                if (only != null && only != n) continue;
                string e = Scenario(n);
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario " + only;
        }

        private static CapZone Zone(double latNm, double lonNm)
        {
            return new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + latNm / 60.0, 20.0 + lonNm / 60.0 } };
        }

        private static List<int> Ids(int n) { var l = new List<int>(); for (int i = 0; i < n; i++) l.Add(550 + i); return l; }

        private static string Scenario(string n)
        {
            var k = new Chk(); var p = new CapParams();
            double R = 2.2;
            switch (n)
            {
                case "phase_angle":
                {
                    var c = new GeoPt(10, 20);
                    k.Is("north 0", Math.Abs(CapMath.CirclePhaseDeg(new GeoPt(10.1, 20), c)) < 1e-6);
                    k.Is("east 90", Math.Abs(CapMath.CirclePhaseDeg(new GeoPt(10, 20.1), c) - 90) < 1e-6);
                    k.Is("south 180", Math.Abs(CapMath.CirclePhaseDeg(new GeoPt(9.9, 20), c) - 180) < 1e-6);
                    k.Is("west 270", Math.Abs(CapMath.CirclePhaseDeg(new GeoPt(10, 19.9), c) - 270) < 1e-6);
                    k.Is("wrap 190", Math.Abs(CapMath.Wrap180(190) + 170) < 1e-9);
                    k.Is("wrap -180", Math.Abs(CapMath.Wrap180(-180) - 180) < 1e-9);
                    double lap = CapMath.LapS(2.2, 300, p);
                    k.Is("lap 2.2 nm / 300 kn about 166 s: " + lap, Math.Abs(lap - 166) < 1.5);
                    k.Is("lap v<=0 uses CircleSpeedKn", Math.Abs(CapMath.LapS(2.2, 0, p) - lap) < 1e-9);
                    break;
                }
                case "plan_even":
                {
                    foreach (double sh in new[] { 0.0, 37.0 })
                    {
                        var pl = CapMath.PhasePlan(Ids(3), new[] { sh, sh + 120, sh + 240 }, null, R, p);
                        k.Is("3 records", pl.Count == 3);
                        foreach (var f in pl) { k.Is("err < 1 shift " + sh + ": " + f.ErrDeg, Math.Abs(f.ErrDeg) < 1); k.Is("no fix", !f.Fix); k.Is("nominal radius", Math.Abs(f.RadiusNm - R) < 1e-9); }
                    }
                    break;
                }
                case "plan_rank":
                {
                    var pl = CapMath.PhasePlan(Ids(3), new[] { 10.0, 20.0, 250.0 }, null, R, p);
                    k.Is("order = input order", pl[0].Uid == 550 && pl[1].Uid == 551 && pl[2].Uid == 552);
                    k.Is("errors balanced around the circular mean: " + (pl[0].ErrDeg + pl[1].ErrDeg + pl[2].ErrDeg), Math.Abs(pl[0].ErrDeg + pl[1].ErrDeg + pl[2].ErrDeg) < 30);
                    k.Is("opposite signs", pl[0].ErrDeg * pl[1].ErrDeg < 0);
                    // the pair 10/20 sits 80 deg short of the 120 deg slot: the bird at 20 is the laggard (cut), the one at 10 is ahead of its slot (stretch)
                    k.Is("ahead stretches, behind cuts", pl[0].ErrDeg > 0 && pl[0].RadiusNm > R && pl[1].ErrDeg < 0 && pl[1].RadiusNm < R);
                    var tie = CapMath.PhasePlan(Ids(2), new[] { 0.0, 0.0 }, null, R, p);
                    k.Is("tie: +-90 and both fix", Math.Abs(Math.Abs(tie[0].ErrDeg) - 90) < 1 && Math.Abs(tie[0].ErrDeg + tie[1].ErrDeg) < 1 && tie[0].Fix && tie[1].Fix);
                    k.Is("k = 1: empty", CapMath.PhasePlan(Ids(1), new[] { 5.0 }, null, R, p).Count == 0);
                    break;
                }
                case "plan_bounds":
                {
                    k.Is("+170 -> 1.35 R", Math.Abs(CapMath.PhaseRadius(R, 170, 1, p) - 1.35 * R) < 1e-9);
                    k.Is("-170 -> 0.85 R", Math.Abs(CapMath.PhaseRadius(R, -170, 1, p) - 0.85 * R) < 1e-9);
                    k.Is("small R: floor 1 nm", Math.Abs(CapMath.PhaseRadius(1.1, -170, 1, p) - 1.0) < 1e-9);
                    var pl = CapMath.PhasePlan(Ids(3), new[] { 0.0, 120.0, 240.0 }, new[] { 400.0, 420.0, 440.0 }, R, p);
                    k.Is("speeds: radius ~ speed", pl[0].RadiusNm < pl[1].RadiusNm && pl[1].RadiusNm < pl[2].RadiusNm);
                    k.Is("fix on the extremes", pl[0].Fix && pl[2].Fix);
                    k.Is("middle follows the mean", Math.Abs(pl[1].RadiusNm - R) < 0.05);
                    break;
                }
                case "route_from_ahead":
                {
                    var z = Zone(1, 1);
                    foreach (int cnt in new[] { 1, 2, 3 })
                    {
                        var ls = CapMath.Lanes(z, cnt, p, 19);
                        foreach (var l in ls)
                        {
                            int kk = l.Track.Count; double step = 360.0 / kk;
                            foreach (double ph in new[] { 0.0, 10, 44, 359, 200, l.StartDeg, l.StartDeg + step / 2, l.StartDeg + step / 2 - 0.01 })
                            {
                                foreach (double rr in new[] { l.RadiusNm, 1.3 * l.RadiusNm, 0.85 * l.RadiusNm })
                                {
                                    foreach (int laps in new[] { 1, 2 })
                                    {
                                        var rt = CapMath.CircleRouteFrom(l, ph, rr, laps, p);
                                        k.Is("points " + rt.Count, rt.Count == laps * kk + 1);
                                        k.Is("closed", Nm(rt[0], rt[rt.Count - 1]) < 1e-9);
                                        double a0 = CapMath.CirclePhaseDeg(rt[0], l.Center);
                                        double d0 = ((a0 - ph) % 360 + 360) % 360;
                                        k.Is("first point ahead (half step; 1.5 step] ph=" + ph + " d=" + d0, d0 >= step / 2 - 0.05 && d0 <= 1.5 * step + 0.05);
                                        double prev = a0;
                                        for (int i = 0; i < rt.Count; i++)
                                        {
                                            k.Is("on radius", Math.Abs(Nm(rt[i], l.Center) - rr) < 0.01);
                                            k.Is("not toward the centre", Nm(rt[i], l.Center) >= 0.85 * l.RadiusNm - 0.01);
                                            if (i > 0 && i < rt.Count - 1)
                                            {
                                                double a = CapMath.CirclePhaseDeg(rt[i], l.Center);
                                                double dd = ((a - prev) % 360 + 360) % 360;
                                                k.Is("clockwise step", Math.Abs(dd - step) < 0.05);
                                                prev = a;
                                            }
                                        }
                                    }
                                }
                                var same = CapMath.CircleRouteFrom(l, ph, l.RadiusNm, 1, p);
                                bool onGrid = false;
                                foreach (var t in l.Track) onGrid |= Nm(t, same[0]) < 0.01;
                                k.Is("R' = R gives lane grid points", onGrid);
                            }
                        }
                    }
                    var plan = new List<CapPhaseFix> { new CapPhaseFix { Uid = 550, PhaseDeg = 12.2, ErrDeg = 35 }, new CapPhaseFix { Uid = 570, PhaseDeg = 130.6, ErrDeg = -10 } };
                    double mx; string txt = CapMath.PhaseText(plan, out mx);
                    k.Is("phase text: " + txt, txt == "550:12/+35 570:131/-10" && Math.Abs(mx - 35) < 1e-9);
                    break;
                }
                case "converge_flown": return Flown(true);
                case "reissue_rate": return Flown(false);
                case "phase_log": return PhaseLog();
                case "t244_kept": return T244Kept();
            }
            return k.Err;
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams();
            public CapController C;
            public Rig() { C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public CapGroupView Gv() { return C.View().Groups[0]; }
        }

        private static string OrderSmall(Rig r, int n)
        {
            return r.C.Order(CapT179Tests.J("{\"group\":\"ПВО-1\",\"rect\":[10.5,11,10.516667,11.016667],\"on_station\":" + n + ",\"ready\":0}"));
        }

        private static Rig OnStation(int n, double[] speeds, out FakeBird[] birds)
        {
            var r = new Rig();
            var ids = new List<string>(); for (int i = 1; i <= n; i++) ids.Add(i.ToString());
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[" + string.Join(",", ids) + "]}"));
            OrderSmall(r, n);
            birds = new FakeBird[n];
            for (int i = 0; i < n; i++) { birds[i] = r.G.Bird(i + 1); if (speeds != null) birds[i].SpeedKn = speeds[i]; }
            for (int i = 0; i < 3000; i++)
            {
                r.Step(1);
                foreach (var b in birds) b.ReserveNm = 100000;
                bool all = true;
                foreach (var b in birds) all &= b.State == "Air";
                if (all && r.Gv().Members.Count == n && r.Gv().Members.TrueForAll(m => m.Where == "zone")) break;
            }
            return r;
        }

        // 3 birds with speeds 400/420/440 that arrive bunched together (phases 0/25/50) must spread to 120 deg +- 30 within 6 laps
        private static string Flown(bool converge)
        {
            var k = new Chk(); FakeBird[] birds;
            var r = OnStation(3, new[] { 400.0, 420.0, 440.0 }, out birds);
            var gv = r.Gv();
            k.Is("all on station", gv.Members.Count == 3 && gv.Members.TrueForAll(m => m.Where == "zone") && gv.Lanes[0].Circle);
            var zc = new GeoPt(10.5 + 1 / 120.0, 11 + 1 / 120.0);
            double rad = gv.Lanes[0].RadiusNm;
            // bunch them on the circle and let the routes run out: the controller must re-issue from the point ahead
            double[] ph0 = { 0, 25, 50 };
            for (int u = 0; u < 3; u++)
            {
                double a = ph0[u] * Math.PI / 180;
                birds[u].Lat = zc.Lat + rad * Math.Cos(a) / 60; birds[u].Lon = zc.Lon + rad * Math.Sin(a) / 60;
                birds[u].Idx = birds[u].Route.Count;
            }
            var uids = new List<int> { 1, 2, 3 };
            double lap = CapMath.LapS(rad, 420, r.P);
            int total = (int)(6.5 * lap), lastTwo = (int)(total - 2 * lap), lastFour = (int)(total - 4 * lap);
            int patrols0 = r.G.Patrols, close = 0, samples = 0, bad = 0, errBad = 0;
            double minC = double.MaxValue, worst = 0;
            var stamps = new Dictionary<int, List<double>> { { 1, new List<double>() }, { 2, new List<double>() }, { 3, new List<double>() } };
            int j0 = r.G.Journal.Count;
            for (int i = 0; i < total; i++)
            {
                r.Step(1);
                foreach (var b in birds) b.ReserveNm = 100000;
                for (; j0 < r.G.Journal.Count; j0++)
                {
                    var pm = Regex.Match(r.G.Journal[j0], "^patrol (\\d+) ");
                    if (pm.Success) stamps[int.Parse(pm.Groups[1].Value)].Add(r.G.NowS);
                }
                var ph = new List<double>(); var pos = new List<GeoPt>();
                foreach (var b in birds) { var q = new GeoPt(b.Lat, b.Lon); pos.Add(q); ph.Add(CapMath.CirclePhaseDeg(q, zc)); minC = Math.Min(minC, Nm(q, zc)); }
                if (i >= lastFour)
                {
                    samples++;
                    double md = double.MaxValue;
                    for (int a = 0; a < 3; a++) for (int b = a + 1; b < 3; b++) md = Math.Min(md, Nm(pos[a], pos[b]));
                    if (md < 0.5) close++;
                }
                if (i >= lastTwo)
                {
                    foreach (var f in CapMath.PhasePlan(uids, ph, null, rad, r.P)) { worst = Math.Max(worst, Math.Abs(f.ErrDeg)); if (Math.Abs(f.ErrDeg) > 30) errBad++; }
                }
            }
            if (converge)
            {
                k.Is("spacing error <= 30 deg in the last 2 laps (worst " + worst.ToString("0") + ")", errBad == 0);
                k.Is("distance >= 0.5 nm in 90% of the last 4 laps: close " + close + "/" + samples, close <= 0.1 * samples);
                double floor = 0.85 * rad * Math.Cos(Math.PI / r.P.CirclePoints) - 0.05;   // the polygon chord lies inside the circle
                k.Is("never toward the centre: min " + minC.ToString("0.00") + " floor " + floor.ToString("0.00"), minC >= floor);
            }
            else
            {
                double t = total / lap;
                int n = r.G.Patrols - patrols0;
                k.Is("re-issues " + n + " <= (laps " + t.ToString("0.0") + " + 1) per bird", n <= 3 * (Math.Ceiling(t) + 1));
                double minGap = double.MaxValue;
                foreach (var kv in stamps)
                    for (int i = 2; i < kv.Value.Count; i++) minGap = Math.Min(minGap, kv.Value[i] - kv.Value[i - 1]);   // the first two come from the start-up transient
                double minLap = CapMath.LapS(0.85 * rad, 440, r.P);
                k.Is("a bird on the circle is re-routed no more than once per lap: min gap " + minGap.ToString("0") + " s, lap(min) " + minLap.ToString("0"), minGap >= 0.75 * minLap);
            }
            return k.Err;
        }

        private static string PhaseLog()
        {
            var k = new Chk(); FakeBird[] birds;
            var r = OnStation(3, null, out birds);
            r.P.Debug = false;
            int l0 = r.G.Logs.Count;
            for (int i = 0; i < 600; i++) { r.Step(1); foreach (var b in birds) b.ReserveNm = 100000; }
            int cnt = 0; bool shape = false;
            for (int i = l0; i < r.G.Logs.Count; i++)
            {
                string l = r.G.Logs[i];
                if (!l.Contains("phase group=")) continue;
                cnt++;
                shape |= Regex.IsMatch(l, "k=3 err_max_deg=\\d+(\\.\\d)? birds=\\d+:\\d+/[+-]\\d+,\\d+:\\d+/[+-]\\d+,\\d+:\\d+/[+-]\\d+");
            }
            k.Is("phase lines in 600 s: " + cnt, cnt >= 1 && cnt <= 600 / 120 + 1);
            k.Is("line shape", shape);
            return k.Err;
        }

        private static string T244Kept()
        {
            var k = new Chk();
            // a bird that is not on station yet gets the plain route from the start point of its slot (2 laps + 1 = 17 points)
            var r = new Rig();
            r.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1,2,3]}"));
            OrderSmall(r, 3);
            r.Step(3);
            string first = null;
            foreach (var j in r.G.Journal) if (j.StartsWith("patrol 1 ", StringComparison.Ordinal)) { first = j; break; }
            k.Is("first route of a bird off station: " + first, first == "patrol 1 17");
            // a large zone keeps racetracks and never gets a phase line
            var r2 = new Rig();
            r2.C.Group(CapT179Tests.J("{\"op\":\"create\",\"uids\":[1,2]}"));
            r2.C.Order(CapT179Tests.J("{\"group\":\"ПВО-1\",\"rect\":[10.5,11,11.0,11.5],\"on_station\":2,\"ready\":0}"));
            var b1 = r2.G.Bird(1); var b2 = r2.G.Bird(2);
            for (int i = 0; i < 1500; i++) { r2.Step(1); b1.ReserveNm = 100000; b2.ReserveNm = 100000; }
            k.Is("big zone: racetrack route (4 + 1 points)", r2.G.Journal.Exists(j => j == "patrol 1 5"));
            k.Is("big zone: no phase lines", !r2.G.LogHas("phase group=") && !r2.G.LogHas("phase fix"));
            return k.Err;
        }
    }
}
