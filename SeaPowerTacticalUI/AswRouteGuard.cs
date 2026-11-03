using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-150 (B1): pure helpers for the ASW route: land / enemy air defence filter, steady search axis, job duration. No Unity.

    /// <summary>A known hostile surface or ground object with the reach of its air defence (nm); RangeNm &lt;= 0 = class unknown (default radius applies).</summary>
    internal sealed class AswHazard
    {
        public GeoPt P;
        public double RangeNm;
        public int TrackId; public string Kind;   // T-180: which track and kind (for the circle names in the log)
        public AswHazard() { }
        public AswHazard(GeoPt p, double rangeNm) { P = p; RangeNm = rangeNm; }
    }

    /// <summary>T-176 F1: why a route point is not placed (ADR-0076 s.6). Land / Shallow are dropped for good, Hazard / Air are deferred.</summary>
    internal enum AswSkip { None, Land, Shallow, Hazard, Air }

    /// <summary>A circle a leg must not cross (air defence or air threat); Why = Hazard | Air.</summary>
    internal sealed class AswCircle { public GeoPt C; public double RNm; public AswSkip Why; public string Label; /* T-180: "t12:Surface:5" | "t40:air:50" */ }

    internal static class AswRouteGuard
    {
        /// <summary>
        /// T-176 F1: classifies a point. heightM &gt;= 0 = land; depth (-heightM) below minWaterM = shallow; inside a hazard circle (own range, else hazardNm) = hazard;
        /// airBlocked(p) = air. NaN height = unknown (no land / shallow verdict). <paramref name="dip"/> only documents the kind (the caller passes the matching minWaterM).
        /// </summary>
        public static AswSkip Classify(GeoPt p, bool dip, double heightM, double minWaterM, IList<AswHazard> hazards, double hazardNm, Func<GeoPt, bool> airBlocked)
        {
            int idx;
            return Classify(p, dip, heightM, minWaterM, hazards, hazardNm, airBlocked, out idx);
        }

        /// <summary>T-180: which hostile tracks give a circle. Unknown / empty kind: none (it may be the very submarine we look for). Land / Building: only when identified with an air defence reach &gt; 0. Surface / Ground: yes (reach 0 = default radius).</summary>
        public static bool HazardOf(string kind, bool identified, double airDefenceNm, out double rangeNm)
        {
            rangeNm = 0;
            switch (kind ?? "")
            {
                case "Land":
                case "Building":
                    if (!identified || airDefenceNm <= 0) return false;
                    rangeNm = airDefenceNm; return true;
                case "Surface":
                case "Ground":
                    rangeNm = identified && airDefenceNm > 0 ? airDefenceNm : 0; return true;
                default: return false;
            }
        }

        /// <summary>T-180: "t12:Surface:5,t40:air:50" - at most max circles, then "+N"; empty when there are none.</summary>
        public static string CirclesNote(IList<string> labels, int max = 6)
        {
            if (labels == null || labels.Count == 0) return "";
            // T-205: sorted, so the same set of circles in another order gives the same note (and the same skip signature)
            var sorted = new List<string>(labels);
            sorted.Sort(StringComparer.Ordinal);
            labels = sorted;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < labels.Count && i < max; i++) { if (i > 0) sb.Append(','); sb.Append(labels[i]); }
            if (labels.Count > max) sb.Append(",+").Append(labels.Count - max);
            return sb.ToString();
        }

        /// <summary>Classify with the index of the hazard circle that fired (-1 = none).</summary>
        public static AswSkip Classify(GeoPt p, bool dip, double heightM, double minWaterM, IList<AswHazard> hazards, double hazardNm, Func<GeoPt, bool> airBlocked, out int hazardIdx)
        {
            hazardIdx = -1;
            if (!double.IsNaN(heightM))
            {
                if (heightM >= 0) return AswSkip.Land;
                if (-heightM < minWaterM) return AswSkip.Shallow;
            }
            if (hazards != null)
                for (int i = 0; i < hazards.Count; i++)
                {
                    var h = hazards[i];
                    double r = h.RangeNm > 0 ? h.RangeNm : hazardNm;
                    if (AswMath.DistNm(p, h.P) < r) { hazardIdx = i; return AswSkip.Hazard; }
                }
            if (airBlocked != null && airBlocked(p)) return AswSkip.Air;
            return AswSkip.None;
        }

        /// <summary>Shallow limit of a buoy: max(config, 3 m + 0.3 x sensor depth); the game keeps the sensor 3 m above the seabed. ft &lt;= 0 = unknown -> config.</summary>
        public static double MinWaterBuoyM(double cfgM, double sonarDepthFt)
        {
            return sonarDepthFt <= 0 ? cfgM : Math.Max(cfgM, 3 + 0.3 * sonarDepthFt * 0.3048);
        }

        /// <summary>Shallow limit of a dip: max(config, 15.2 m body above the seabed + 15 m of cable).</summary>
        public static double MinWaterDipM(double cfgM) { return Math.Max(cfgM, 15.2 + 15); }

        /// <summary>Distance (nm) from point c to the segment a-b: flat on stable, x LonK on the beta globe (T-348).</summary>
        public static double SegDistNm(GeoPt a, GeoPt b, GeoPt c)
        {
            double k = AswMath.LonK(a.Lat, b.Lat);
            double bx = (b.Lat - a.Lat) * 60.0, by = (b.Lon - a.Lon) * 60.0 * k, cx = (c.Lat - a.Lat) * 60.0, cy = (c.Lon - a.Lon) * 60.0 * k;
            double len2 = bx * bx + by * by;
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (cx * bx + cy * by) / len2));
            double dx = cx - t * bx, dy = cy - t * by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Index of the far point of the first leg (from -> p0 -> p1 ...) that passes through a circle (distance &lt; r; touching is fine); -1 = none.</summary>
        public static int FirstBlockedLeg(GeoPt from, IList<GeoPt> pts, IList<AswCircle> circles)
        {
            if (pts == null || circles == null || circles.Count == 0) return -1;
            GeoPt prev = from;
            for (int i = 0; i < pts.Count; i++)
            {
                foreach (var c in circles)
                    if (SegDistNm(prev, pts[i], c.C) < c.RNm - 1e-9) return i;
                prev = pts[i];
            }
            return -1;
        }

        /// <summary>
        /// Route points without those over land (heightM &gt; 0) and those inside the air defence reach of a hazard (its own RangeNm, else hazardNm).
        /// The order of the kept points is unchanged. <paramref name="land"/> / <paramref name="hazard"/> count what was dropped (a land point is not counted twice).
        /// </summary>
        public static List<GeoPt> Filter(IList<GeoPt> pts, Func<GeoPt, double> heightM, IList<AswHazard> hazards, double hazardNm, out int land, out int hazard)
        {
            land = 0; hazard = 0;
            var res = new List<GeoPt>();
            foreach (var p in pts)
            {
                if (heightM != null && heightM(p) > 0) { land++; continue; }
                bool bad = false;
                if (hazards != null)
                    foreach (var h in hazards)
                    {
                        double r = h.RangeNm > 0 ? h.RangeNm : hazardNm;
                        if (AswMath.DistNm(p, h.P) < r) { bad = true; break; }
                    }
                if (bad) { hazard++; continue; }
                res.Add(p);
            }
            return res;
        }

        /// <summary>
        /// Search axis that does not jump: slow ship (v &lt; minKn) or no usable track -> heading; otherwise the raw axis.
        /// A change of the held axis is taken only when it is more than keepDeg away.
        /// </summary>
        public static double StableAxis(double prevAxis, bool havePrev, double rawAxis, string rawSrc, double headingDeg, double vKn,
                                        double minKn, double keepDeg, out string src)
        {
            double cand; 
            if (vKn < minKn || rawSrc == null || rawSrc == "heading") { cand = AswMath.Norm360(headingDeg); src = "heading"; }
            else { cand = AswMath.Norm360(rawAxis); src = rawSrc; }
            if (havePrev && AswMath.AngleDiff(cand, prevAxis) <= keepDeg) { src = src + "~"; return prevAxis; }
            return cand;
        }

        /// <summary>T-260 / ADR-0104: the frame of a barrier with a band follows the player's route (not the track) while the mod does not steer the ship.</summary>
        public static bool FrameByRoute(bool barrierBand, bool frameSet, bool modSteered, double? routeBrg)
        {
            return barrierBand && frameSet && !modSteered && routeBrg.HasValue;
        }

        /// <summary>T-264: a barrier with a band and no player's route whose track still holds the evasion leg (now - lastSteerS &lt; spanS): the frame axis is the heading, not the chord.</summary>
        public static bool FrameByHeading(bool barrierBand, bool frameSet, bool modSteered, double? routeBrg, double lastSteerS, double now, double spanS)
        {
            return barrierBand && frameSet && !modSteered && !routeBrg.HasValue && lastSteerS >= 0 && now - lastSteerS < spanS - 1e-9;
        }

        /// <summary>T-266 / ADR-0106: a barrier with a band whose ship cannot move keeps its anchor, frame axis and band axis.</summary>
        public static bool FreezeImmobile(bool barrierBand, bool frameSet, string immobileWhy) { return barrierBand && frameSet && immobileWhy != null; }

        public const double StandDeferMaxS = 180;

        /// <summary>T-274: keep the band of a stopped ship whose telegraph still asks for way (wait for no_way). true = keep; timedOut = the wait ran out (the caller resets).</summary>
        public static bool StandDefer(bool bandSet, bool wayPending, double now, double maxS, ref double sinceS, out bool timedOut)
        {
            timedOut = false;
            if (!bandSet || !wayPending) { sinceS = -1; return false; }
            if (sinceS < 0 || sinceS > now) sinceS = now;
            if (now - sinceS >= maxS) { timedOut = true; return false; }
            return true;
        }

        /// <summary>T-274: why a barrier of a slow ship (v &lt; standKn) is not frozen: no_band | no_frame | pending | player_stop; null = frozen, not slow, or not a barrier.</summary>
        public static string FreezeWait(bool barrier, bool bandSet, bool frameSet, string immobileWhy, bool wayPending, double vKn, double standKn = 3)
        {
            if (!barrier || double.IsNaN(vKn) || Math.Abs(vKn) >= standKn) return null;
            if (!bandSet) return "no_band";
            if (!frameSet) return "no_frame";
            if (immobileWhy != null) return null;
            return wayPending ? "pending" : "player_stop";
        }

        /// <summary>Seconds a package of points takes: the path from the aircraft over the points at speedKn plus perPointS for each drop / dip.</summary>
        public static double JobSeconds(GeoPt from, IList<GeoPt> pts, double speedKn, double perPointS)
        {
            double len = 0; GeoPt cur = from;
            foreach (var p in pts) { len += AswMath.DistNm(cur, p); cur = p; }
            return len / Math.Max(1.0, speedKn) * 3600.0 + perPointS * pts.Count;
        }

        // ---- T-238 / ADR-0098: the barrier axis follows the player's route ------------------------------------------

        /// <summary>A route point nearer than this to the ship is skipped when the bearing to the next point is taken (the bearing jumps next to a point).</summary>
        public const double RouteNearNm = 1.0;

        /// <summary>
        /// The player's route: the current waypoints unless the mod steers the ship (its first point is the mod's point within 0.2 nm, or there are no points while the mod holds a record):
        /// then the saved route. hasSaved = ControlEntry.HasRoute &amp;&amp; PlacedByMod != null.
        /// </summary>
        public static IList<double[]> PlayerRoute(IList<double[]> current, bool hasSaved, IList<double[]> saved, double[] placedByMod, out bool modSteered)
        {
            modSteered = false;
            if (!hasSaved || placedByMod == null) return current ?? new List<double[]>();
            if (current == null || current.Count == 0) { modSteered = true; return saved ?? new List<double[]>(); }
            double[] f = current[0];
            if (f != null && f.Length >= 2 && AswMath.DistNm(new GeoPt(f[0], f[1]), new GeoPt(placedByMod[0], placedByMod[1])) <= 0.2) { modSteered = true; return saved ?? new List<double[]>(); }
            return current;
        }

        /// <summary>Bearing (flat) from (lat, lon) to the first route point farther than nearNm; null = none.</summary>
        public static double? NextRouteBrg(double lat, double lon, IList<double[]> route, double nearNm = RouteNearNm)
        {
            if (route == null) return null;
            var me = new GeoPt(lat, lon);
            foreach (var w in route)
            {
                if (w == null || w.Length < 2) continue;
                var p = new GeoPt(w[0], w[1]);
                if (AswMath.DistNm(me, p) > nearNm) return AswMath.BearingDeg(me, p);
            }
            return null;
        }

        /// <summary>ADR-0098 s.3: held (mod steers, havePrev) &gt; route &gt; saved_course (mod steers, no prev) &gt; course (smoothed). src: held|route|saved_course|course.</summary>
        public static double BandAxis(double? routeBrg, bool modSteered, bool havePrev, double prevAxis, double? savedCourseDeg, double smoothedAxis, out string src)
        {
            if (modSteered && havePrev) { src = "held"; return prevAxis; }
            if (routeBrg.HasValue) { src = "route"; return AswMath.Norm360(routeBrg.Value); }
            if (modSteered && savedCourseDeg.HasValue) { src = "saved_course"; return AswMath.Norm360(savedCourseDeg.Value); }
            src = "course"; return smoothedAxis;
        }

        /// <summary>
        /// T-251 / ADR-0103: the axis of the save holds the band after a load. Keep (true) while the mod steers the ship (sinceS = 0);
        /// release (false) when the candidate axis is within deg of the held one (sinceS = 0) or differs by &gt;= deg for holdS in a row
        /// without the mod steering (sinceS stays = the start of the deviation, the caller moves it into the band). why: steered | pending (keep); consistent | confirmed (release).
        /// </summary>
        public static bool LoadHold(double holdAxisDeg, double candAxisDeg, bool modSteered, double now, double deg, double holdS, ref double sinceS, out string why)
        {
            if (modSteered) { sinceS = 0; why = "steered"; return true; }
            if (Math.Abs(AswMath.AngleDiff(holdAxisDeg, candAxisDeg)) < deg - 1e-9) { sinceS = 0; why = "consistent"; return false; }
            if (sinceS <= 0) sinceS = Math.Max(now, 1e-6);
            if (now - sinceS >= holdS - 1e-9) { why = "confirmed"; return false; }
            why = "pending"; return true;
        }

        private static string HoldSelfTest()
        {
            string why; double since = 0;
            if (!LoadHold(167, 13, true, 50, 30, 60, ref since, out why) || why != "steered" || since != 0) return "hold steered";
            if (LoadHold(167, 160, false, 50, 30, 60, ref since, out why) || why != "consistent") return "hold consistent";
            since = 0;
            if (!LoadHold(167, 13, false, 100, 30, 60, ref since, out why) || why != "pending" || since != 100) return "hold pending";
            if (!LoadHold(167, 13, false, 159, 30, 60, ref since, out why)) return "hold 159";
            if (LoadHold(167, 13, false, 160, 30, 60, ref since, out why) || why != "confirmed" || since != 100) return "hold confirmed";
            since = 0;
            if (LoadHold(350, 10, false, 100, 30, 60, ref since, out why) || why != "consistent") return "hold wrap";
            return null;
        }

        private static string RouteSelfTest()
        {
            string h = HoldSelfTest(); if (h != null) return h;
            var o = new GeoPt(35, 20);
            var east = AswMath.Offset(o, 90, 10); var mod = AswMath.Offset(o, 0, 3); var north = AswMath.Offset(o, 0, 10);
            Func<GeoPt, double[]> w = q => new[] { q.Lat, q.Lon };
            var cur = new List<double[]> { w(mod) }; var saved = new List<double[]> { w(east) };
            bool st;
            var r = PlayerRoute(cur, true, saved, w(mod), out st);
            if (!st || r != saved) return "route: the mod's point must give the saved route";
            r = PlayerRoute(new List<double[]> { w(north) }, true, saved, w(mod), out st);
            if (st || r[0][0] != north.Lat) return "route: a player's route must win";
            r = PlayerRoute(new List<double[]>(), true, saved, w(mod), out st);
            if (!st || r != saved) return "route: no points + record = saved";
            r = PlayerRoute(cur, false, saved, w(mod), out st);
            if (st || r != cur) return "route: no record = current";
            var near = AswMath.Offset(o, 0, 0.5);
            double? b = NextRouteBrg(o.Lat, o.Lon, new List<double[]> { w(near), w(east) });
            if (!b.HasValue || AswMath.AngleDiff(b.Value, 90) > 0.5) return "route: near point skipped " + b;
            if (NextRouteBrg(o.Lat, o.Lon, new List<double[]> { w(near) }).HasValue) return "route: one near point = none";
            if (NextRouteBrg(o.Lat, o.Lon, new List<double[]>()).HasValue) return "route: empty = none";
            if (!FrameByRoute(true, true, false, 150) || FrameByRoute(true, true, true, 150) || FrameByRoute(true, true, false, null)
                || FrameByRoute(false, true, false, 150) || FrameByRoute(true, false, false, 150)) return "frame by route";
            if (!FrameByHeading(true, true, false, null, 100, 200, 180) || FrameByHeading(true, true, true, null, 100, 200, 180) || FrameByHeading(true, true, false, 150, 100, 200, 180)
                || FrameByHeading(true, true, false, null, -1, 200, 180) || FrameByHeading(true, true, false, null, 100, 280, 180)
                || FrameByHeading(false, true, false, null, 100, 200, 180) || FrameByHeading(true, false, false, null, 100, 200, 180)) return "frame by heading";
            if (!FreezeImmobile(true, true, "engine") || FreezeImmobile(false, true, "engine") || FreezeImmobile(true, false, "engine") || FreezeImmobile(true, true, null)) return "freeze immobile";
            bool to; double sd = -1;
            if (StandDefer(false, true, 0, 180, ref sd, out to) || to) return "stand defer no band";
            sd = 5; if (StandDefer(true, false, 10, 180, ref sd, out to) || sd != -1) return "stand defer not pending";
            sd = -1;
            if (!StandDefer(true, true, 100, 180, ref sd, out to) || sd != 100) return "stand defer start";
            if (!StandDefer(true, true, 279, 180, ref sd, out to) || to) return "stand defer 179";
            if (StandDefer(true, true, 280, 180, ref sd, out to) || !to) return "stand defer 180";
            sd = 500; StandDefer(true, true, 100, 180, ref sd, out to); if (sd != 100) return "stand defer rewind";
            if (FreezeWait(true, true, true, null, true, 0) != "pending" || FreezeWait(true, true, true, null, false, 0) != "player_stop"
                || FreezeWait(true, false, true, null, true, 0) != "no_band" || FreezeWait(true, true, false, null, true, 0) != "no_frame"
                || FreezeWait(true, true, true, "no_way", true, 0) != null || FreezeWait(true, true, true, null, true, 3) != null || FreezeWait(false, true, true, null, true, 0) != null) return "freeze wait";
            string src;
            double a = BandAxis(90, true, true, 40, 10, 5, out src);
            if (a != 40 || src != "held") return "axis held " + a + src;
            a = BandAxis(90, false, true, 40, 10, 5, out src);
            if (a != 90 || src != "route") return "axis route " + a + src;
            a = BandAxis(null, true, false, 0, 370, 5, out src);
            if (a != 10 || src != "saved_course") return "axis saved " + a + src;
            a = BandAxis(null, false, true, 40, 10, 5, out src);
            if (a != 5 || src != "course") return "axis course " + a + src;
            a = BandAxis(null, true, true, 40, 10, 5, out src);
            if (a != 40 || src != "held") return "axis held no route " + a + src;
            return null;
        }

        public static string SelfTest()
        {
            return RouteSelfTest() ?? SelfTestCore();
        }

        private static string SelfTestCore()
        {
            var o = new GeoPt(35, 20);
            var pts = new List<GeoPt>
            {
                AswMath.Offset(o, 0, 1), AswMath.Offset(o, 0, 10), AswMath.Offset(o, 90, 20), AswMath.Offset(o, 180, 30), AswMath.Offset(o, 270, 40), AswMath.Offset(o, 0, 50)
            };
            Func<GeoPt, double> h = p => (AswMath.DistNm(p, pts[1]) < 0.1 || AswMath.DistNm(p, pts[4]) < 0.1) ? 50 : -200;
            var hz = new List<AswHazard> { new AswHazard(AswMath.Offset(pts[2], 90, 3), 0) };
            int land, hazard;
            var kept = Filter(pts, h, hz, 5, out land, out hazard);
            if (kept.Count != 3 || land != 2 || hazard != 1) return "filter counts " + kept.Count + "/" + land + "/" + hazard;
            if (AswMath.DistNm(kept[0], pts[0]) > 1e-9 || AswMath.DistNm(kept[1], pts[3]) > 1e-9 || AswMath.DistNm(kept[2], pts[5]) > 1e-9) return "filter order";
            // a hazard with a class range wins over the default radius
            var hz2 = new List<AswHazard> { new AswHazard(AswMath.Offset(pts[2], 90, 3), 2) };
            kept = Filter(new List<GeoPt> { pts[2] }, null, hz2, 5, out land, out hazard);
            if (kept.Count != 1 || hazard != 0) return "filter class range";
            kept = Filter(new List<GeoPt> { pts[2] }, null, new List<AswHazard> { new AswHazard(AswMath.Offset(pts[2], 90, 3), 0) }, 5, out land, out hazard);
            if (kept.Count != 0 || hazard != 1) return "filter default range";
            string src;
            double a = StableAxis(30, true, 40, "track", 100, 2, 5, 20, out src);
            if (Math.Abs(a - 100) > 1e-9 || src != "heading") return "axis slow";
            a = StableAxis(30, true, 40, "track", 100, 10, 5, 20, out src);
            if (Math.Abs(a - 30) > 1e-9) return "axis hold " + a;
            a = StableAxis(30, true, 60, "track", 100, 10, 5, 20, out src);
            if (Math.Abs(a - 60) > 1e-9 || src != "track") return "axis change " + a + " " + src;
            a = StableAxis(0, false, 60, "track", 100, 10, 5, 20, out src);
            if (Math.Abs(a - 60) > 1e-9) return "axis first";
            a = StableAxis(350, true, 5, "track", 100, 10, 5, 20, out src);
            if (Math.Abs(a - 350) > 1e-9) return "axis wrap hold";
            var line = new List<GeoPt> { AswMath.Offset(o, 0, 8), AswMath.Offset(o, 0, 16), AswMath.Offset(o, 0, 24) };
            double s = JobSeconds(o, line, 120, 60);
            if (Math.Abs(s - 900) > 1) return "job seconds " + s;
            return null;
        }
    }
}
