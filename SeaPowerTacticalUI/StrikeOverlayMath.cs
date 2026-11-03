using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>One primitive of the strike layer in geo coordinates. Kind: axis | launch | wait | orbit | sam | horizon | gate | descent | route | sam_leg (phase C).</summary>
    internal struct StrikePrim
    {
        public string Kind, Color, Label;
        public GeoPt A, B;
        public double RNm;
        public bool Dashed;
    }

    // T-341 phase B: what the map layer draws for the selected strike group. Pure: no Unity, no game. The drawing is StrikeOverlay.
    internal static class StrikeOverlayMath
    {
        /// <summary>T-342 C: the stage of a clear_pause aircraft next to its label: " 1" / " 2"; none for a plain strike.</summary>
        private static string StageTag(int stage) { return stage == 1 || stage == 2 ? " " + stage.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""; }

        /// <summary>The attack axis (wait point through the target), the launch points of the chosen variant, the wait point with its orbit, and the SAM zones of the known hostile ships
        /// (solid) with the radio horizon of a low-flying missile (dashed, only where it is shorter than the zone). null group = nothing is drawn.</summary>
        public static List<StrikePrim> Build(StrikeGroupView g, IList<StrikeShipRow> ships, int chosen)
        {
            var res = new List<StrikePrim>();
            if (g == null) return res;
            if (ships != null)
                foreach (var s in ships)
                {
                    if (s == null || !s.Hostile || s.Excl == "civilian" || s.SamNm <= 0) continue;
                    var p = new GeoPt(s.Lat, s.Lon);
                    res.Add(new StrikePrim { Kind = "sam", A = p, RNm = s.SamNm, Color = "red", Label = (string.IsNullOrEmpty(s.ClassName) ? "" : s.ClassName + " ") + "#" + s.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                    if (s.HorizonNm > 0 && s.HorizonNm < s.SamNm - 0.5) res.Add(new StrikePrim { Kind = "horizon", A = p, RNm = s.HorizonNm, Dashed = true, Color = "red" });
                }
            if (g.WaitPt.HasValue)
            {
                res.Add(new StrikePrim { Kind = "wait", A = g.WaitPt.Value, Label = g.Name, Color = "cyan" });
                if (g.WaitR > 0) res.Add(new StrikePrim { Kind = "orbit", A = g.WaitPt.Value, RNm = g.WaitR, Dashed = true, Color = "cyan" });
                if (g.TargetPt.HasValue) res.Add(new StrikePrim { Kind = "axis", A = g.WaitPt.Value, B = g.TargetPt.Value, Color = "yellow" });
            }
            if (chosen >= 0 && chosen == g.Chosen && g.Routes != null)
                foreach (var r in g.Routes)
                {
                    string tag = "#" + r.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture) + StageTag(r.Stage);
                    res.Add(new StrikePrim { Kind = "gate", A = r.Gate, Label = tag, Color = "orange" });
                    if (r.Descent.HasValue) res.Add(new StrikePrim { Kind = "descent", A = r.Descent.Value, Label = tag, Color = "yellow" });
                    res.Add(new StrikePrim { Kind = "route", A = r.Gate, B = r.Launch, Color = "orange" });
                    // the way inside the SAM zones of the hostile ships (the same axis the risk of the card is counted on): solid above the radio horizon, dashed under it
                    double axis = AswMath.BearingDeg(r.Launch, r.Gate);
                    var far = AswMath.Offset(r.Launch, axis, 400);
                    if (ships != null)
                        foreach (var s in ships)
                        {
                            if (s == null || !s.Hostile || s.Excl == "civilian" || s.SamNm <= 0) continue;
                            double t1, t2;
                            if (!StrikeGeoMath.Clip(far, r.Launch, new GeoPt(s.Lat, s.Lon), s.SamNm, out t1, out t2)) continue;
                            res.Add(new StrikePrim { Kind = "sam_leg", A = Between(far, r.Launch, t1), B = Between(far, r.Launch, t2), Dashed = r.UnderHorizon, Color = "red", Label = null });
                        }
                }
            if (chosen >= 0 && chosen == g.Chosen) res.AddRange(EwGeo(g));
            if (chosen >= 0 && chosen == g.Chosen) res.AddRange(FighterGeo(g));
            if (chosen >= 0 && chosen == g.Chosen)
                foreach (var l in g.Launches)
                {
                    res.Add(new StrikePrim { Kind = "launch", A = l.Pt, Label = (l.Name ?? ("#" + l.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture))) + StageTag(l.Stage), Color = "orange" });
                    if (g.TargetPt.HasValue) res.Add(new StrikePrim { Kind = "axis", A = l.Pt, B = g.TargetPt.Value, Dashed = true, Color = "orange" });
                }
            return res;
        }

        /// <summary>Phase D: for every jammer of the group the station (a diamond), the cone (two rays to the farthest covered target + a dashed arc), a ring on every uncovered target, the dashed route; a hatch of the cone while the jammer is on.</summary>
        public static List<StrikePrim> EwGeo(StrikeGroupView g)
        {
            var res = new List<StrikePrim>();
            if (g == null || g.Ew == null) return res;
            foreach (var e in g.Ew)
            {
                foreach (var u in e.UncoveredPts) res.Add(new StrikePrim { Kind = "ew_unc", A = u, RNm = 3, Color = "orange" });
                if (!e.Station.HasValue || e.State == "no_station" || e.State == "lost") continue;
                var st = e.Station.Value;
                string tag = e.Name ?? ("#" + e.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                res.Add(new StrikePrim { Kind = "ew_station", A = st, Label = tag, Color = "cyan" });
                GeoPt? prev = null;
                foreach (var r in e.Route) { if (prev.HasValue) res.Add(new StrikePrim { Kind = "ew_route", A = prev.Value, B = r, Dashed = true, Color = "cyan" }); prev = r; }
                if (!e.Aim.HasValue) continue;
                double far = AswMath.DistNm(st, e.Aim.Value);
                foreach (var c in e.CoverPts) far = Math.Max(far, AswMath.DistNm(st, c));
                double brg = AswMath.BearingDeg(st, e.Aim.Value), half = e.ConeDeg / 2;
                if (half <= 0) continue;
                res.Add(new StrikePrim { Kind = "ew_ray", A = st, B = AswMath.Offset(st, brg - half, far), Color = "cyan" });
                res.Add(new StrikePrim { Kind = "ew_ray", A = st, B = AswMath.Offset(st, brg + half, far), Color = "cyan" });
                const int n = 8;
                var p0 = AswMath.Offset(st, brg - half, far);
                for (int i = 1; i <= n; i++)
                {
                    var p1 = AswMath.Offset(st, brg - half + e.ConeDeg * i / n, far);
                    if (i % 2 == 1) res.Add(new StrikePrim { Kind = "ew_arc", A = p0, B = p1, Dashed = true, Color = "cyan" });
                    p0 = p1;
                }
                if (e.On)
                    for (int i = 1; i < 6; i++) res.Add(new StrikePrim { Kind = "ew_hatch", A = st, B = AswMath.Offset(st, brg - half + e.ConeDeg * i / 6, far), Color = "cyan" });
            }
            return res;
        }

        /// <summary>T-342 D: a dashed circle R_t round every fighter that threatens the strikers (red), and for a jammer on a fighter the two rays of its cone towards the fighter (cyan).</summary>
        public static List<StrikePrim> FighterGeo(StrikeGroupView g)
        {
            var res = new List<StrikePrim>();
            if (g == null) return res;
            foreach (var t in g.FighterThreats)
                if (t.RNm > 0) res.Add(new StrikePrim { Kind = "ewf_rt", A = t.Pt, RNm = t.RNm, Dashed = true, Color = "red", Label = string.IsNullOrEmpty(t.Cls) ? null : t.Cls + " #" + t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            foreach (var e in g.Ew)
            {
                if (e.JamKind != "object" || !e.JamPos.HasValue || !e.FighterPt.HasValue || e.ConeDeg <= 0) continue;
                var from = e.JamPos.Value;
                double brg = AswMath.BearingDeg(from, e.FighterPt.Value), far = AswMath.DistNm(from, e.FighterPt.Value), half = e.ConeDeg / 2;
                res.Add(new StrikePrim { Kind = "ewf_ray", A = from, B = AswMath.Offset(from, brg - half, far), Color = "cyan" });
                res.Add(new StrikePrim { Kind = "ewf_ray", A = from, B = AswMath.Offset(from, brg + half, far), Color = "cyan" });
            }
            return res;
        }

        private static GeoPt Between(GeoPt a, GeoPt b, double t) { return new GeoPt(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t); }

        public static string SelfTest()
        {
            var ships = new List<StrikeShipRow>
            {
                new StrikeShipRow { Id = 100, ClassName = "Kidd", Hostile = true, Lat = 41, Lon = 10, SamNm = 90, HorizonNm = 21 },
                new StrikeShipRow { Id = 101, Hostile = true, Lat = 41.1, Lon = 10.2, SamNm = 20, HorizonNm = 20 },
                new StrikeShipRow { Id = 102, Hostile = true, Lat = 41.2, Lon = 10.2, SamNm = 0 },
                new StrikeShipRow { Id = 103, Hostile = true, Excl = "civilian", Lat = 41.2, Lon = 10.3, SamNm = 30, HorizonNm = 21 },
                new StrikeShipRow { Id = 104, Hostile = false, Lat = 41.2, Lon = 10.4, SamNm = 30 },
            };
            var g = new StrikeGroupView { Name = "A", Chosen = 0, WaitPt = new GeoPt(39.25, 10), WaitR = 5, TargetPt = new GeoPt(41, 10) };
            g.Launches.Add(new StrikeLaunchView { Uid = 1, Name = "A-6E 1", Pt = new GeoPt(40.0, 10), TargetId = 100 });
            var l = Build(g, ships, 0);
            if (l.Count(p => p.Kind == "sam") != 2) return "sam circles of the hostile ships with a SAM " + l.Count(p => p.Kind == "sam");
            if (l.Count(p => p.Kind == "horizon") != 1 || !l.First(p => p.Kind == "horizon").Dashed || Math.Abs(l.First(p => p.Kind == "horizon").RNm - 21) > 0.01) return "the horizon: dashed, only where shorter than the zone";
            var axis = l.FirstOrDefault(p => p.Kind == "axis" && !p.Dashed);
            if (axis.Kind == null || axis.A.Lat != 39.25 || axis.B.Lat != 41) return "axis from the wait point through the target";
            if (l.Count(p => p.Kind == "wait") != 1 || l.Count(p => p.Kind == "orbit") != 1 || l.First(p => p.Kind == "orbit").RNm != 5) return "wait point and orbit";
            if (l.Count(p => p.Kind == "launch") != 1 || l.First(p => p.Kind == "launch").Label != "A-6E 1") return "launch point";
            // phase C: the gate, the descent point, the route, the way inside the zone
            var gt = AswMath.Offset(g.Launches[0].Pt, 180, 15);
            g.Routes.Add(new StrikeRoutePrim { Uid = 1, TargetId = 100, Gate = gt, Launch = g.Launches[0].Pt, Descent = AswMath.Offset(gt, 180, 100), InSamNm = 30, UnderHorizon = true });
            g.Routes.Add(new StrikeRoutePrim { Uid = 2, TargetId = 100, Gate = gt, Launch = g.Launches[0].Pt, Descent = null, InSamNm = 30, UnderHorizon = false });
            l = Build(g, ships, 0);
            if (l.Count(p => p.Kind == "gate") != 2 || l.Count(p => p.Kind == "descent") != 1 || l.Count(p => p.Kind == "route") != 2) return "gate, descent, route " + l.Count(p => p.Kind == "gate") + l.Count(p => p.Kind == "descent") + l.Count(p => p.Kind == "route");
            var legs = l.Where(p => p.Kind == "sam_leg").ToList();
            if (legs.Count < 2 || legs.Count(p => p.Dashed) != legs.Count / 2 || legs.Any(p => p.Color != "red")) return "sam legs: dashed under the horizon " + legs.Count + " " + legs.Count(p => p.Dashed);
            foreach (var leg in legs) if (AswMath.DistNm(leg.A, new GeoPt(41, 10)) > 90.5 || AswMath.DistNm(leg.B, new GeoPt(41, 10)) > 90.5) return "a leg outside the zone";
            if (Build(g, ships, 1).Any(p => p.Kind == "gate" || p.Kind == "sam_leg")) return "no gate for another card";
            g.Routes.Clear();
            // T-342 C: the numbers of the stages of clear_pause next to the labels, none for a plain strike
            g.Launches.Add(new StrikeLaunchView { Uid = 7, Name = "A-6E 7", Pt = new GeoPt(40.1, 10), TargetId = 101, Stage = 1 });
            g.Launches.Add(new StrikeLaunchView { Uid = 8, Name = "A-6E 8", Pt = new GeoPt(40.1, 10.1), TargetId = 100, Stage = 2 });
            l = Build(g, ships, 0);
            if (!l.Any(p => p.Kind == "launch" && p.Label == "A-6E 7 1") || !l.Any(p => p.Kind == "launch" && p.Label == "A-6E 8 2") || !l.Any(p => p.Kind == "launch" && p.Label == "A-6E 1")) return "stage numbers at the launch labels";
            g.Launches.RemoveRange(1, 2);
            // the launch points only for the chosen card
            if (Build(g, ships, 1).Any(p => p.Kind == "launch")) return "no launch points for another card";
            if (Build(g, ships, -1).Any(p => p.Kind == "launch")) return "no launch points without a choice";
            // no target: only the wait point and the zones
            g.TargetPt = null;
            l = Build(g, ships, 0);
            if (l.Any(p => p.Kind == "axis" && !p.Dashed) || l.Count(p => p.Kind == "wait") != 1) return "no target: wait point only";
            g.WaitPt = null;
            if (Build(g, ships, 0).Any(p => p.Kind == "wait" || p.Kind == "orbit")) return "no wait point";
            if (Build(null, ships, 0).Count != 0) return "no group, nothing";
            // phase D: the jammer
            var gw = new StrikeGroupView { Name = "A", Chosen = 0 };
            var ew = new StrikeEwRow { Uid = 5, Name = "EA-6B 5", State = "to_station", ConeDeg = 30, Station = new GeoPt(38, 10), Aim = new GeoPt(41, 10) };
            ew.CoverPts.Add(new GeoPt(41, 10)); ew.CoverPts.Add(new GeoPt(41, 10.3)); ew.UncoveredPts.Add(new GeoPt(41, 11)); ew.Route.Add(new GeoPt(38, 9)); ew.Route.Add(new GeoPt(38, 10));
            gw.Ew.Add(ew);
            var eg = EwGeo(gw);
            if (eg.Count(p => p.Kind == "ew_station") != 1 || eg.Count(p => p.Kind == "ew_ray") != 2 || eg.Count(p => p.Kind == "ew_arc") != 4 || eg.Count(p => p.Kind == "ew_unc") != 1 || eg.Count(p => p.Kind == "ew_route") != 1) return "ew geo " + eg.Count;
            if (eg.Any(p => p.Kind == "ew_hatch")) return "no hatch while off";
            var rays = eg.Where(p => p.Kind == "ew_ray").ToList();
            double rayLen = AswMath.DistNm(rays[0].A, rays[0].B), farTarget = AswMath.DistNm(new GeoPt(38, 10), new GeoPt(41, 10.3));
            if (Math.Abs(rayLen - farTarget) > 0.01) return "the rays reach the farthest target " + rayLen + " " + farTarget;
            ew.On = true;
            if (EwGeo(gw).Count(p => p.Kind == "ew_hatch") != 5) return "hatch while on";
            ew.State = "no_station";
            if (EwGeo(gw).Any(p => p.Kind == "ew_station")) return "no station, no diamond";
            if (Build(gw, ships, 1).Any(p => p.Kind.StartsWith("ew_", StringComparison.Ordinal))) return "ew only for the chosen card";
            // T-342 D: the circle R_t of a fighter and the cone of the jammer on it
            var gf = new StrikeGroupView { Name = "A", Chosen = 0 };
            gf.FighterThreats.Add(new StrikeFighterRow { Id = 4021, Cls = "F-14A", Pt = new GeoPt(41, 12), RNm = 38, DistNm = 60 });
            gf.Ew.Add(new StrikeEwRow { Uid = 5, State = "to_station", ConeDeg = 30, JamKind = "object", JamPos = new GeoPt(39, 10), FighterPt = new GeoPt(41, 12) });
            var fg = FighterGeo(gf);
            var circ = fg.FirstOrDefault(p => p.Kind == "ewf_rt");
            if (circ.Kind == null || !circ.Dashed || Math.Abs(circ.RNm - 38) > 0.01 || circ.A.Lat != 41) return "the circle R_t of a fighter";
            var fr = fg.Where(p => p.Kind == "ewf_ray").ToList();
            if (fr.Count != 2) return "two rays to the fighter " + fr.Count;
            double wide = Math.Abs(AswMath.BearingDeg(fr[0].A, fr[0].B) - AswMath.BearingDeg(fr[1].A, fr[1].B));
            if (Math.Abs(wide - 30) > 0.5) return "the rays are one cone wide " + wide;
            if (Math.Abs(AswMath.DistNm(fr[0].A, fr[0].B) - AswMath.DistNm(new GeoPt(39, 10), new GeoPt(41, 12))) > 0.01) return "the rays reach the fighter";
            if (Build(gf, ships, 0).Count(p => p.Kind == "ewf_rt") != 1 || Build(gf, ships, 1).Any(p => p.Kind.StartsWith("ewf_", StringComparison.Ordinal))) return "fighter geo only for the chosen card";
            gf.Ew[0].JamKind = null;
            if (FighterGeo(gf).Any(p => p.Kind == "ewf_ray")) return "no ray without an object jam";
            return null;
        }
    }
}
