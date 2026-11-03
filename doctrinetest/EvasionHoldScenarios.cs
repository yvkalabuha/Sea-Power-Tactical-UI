using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-268 (ADR-0107): synthetic replays of the T-267 torpedo hits (numbers from docs/research/T-267-torpedo-hits.md, section 3; the logs have no torpedo coordinates).
    /// A 1 s simulator: the ship (flank 24.5 kn, slow 6.4 kn, 1.3 deg/s turn to the escape course, back to the route course after the evasion ends),
    /// torpedoes in pure pursuit; the plotted mark is fresh until freezeS, then stands (view a) or is dead-reckoned by the game along the last velocity (view b).
    /// Every tick: TrackPursuit.Update -> EvTorp -> EvasionMath.Done / SonarSlowNow (they set the speed). The old rule (ADR-0091) is replayed for comparison.
    /// </summary>
    internal static class EvasionHoldScenarios
    {
        private const double FlankKn = 24.5, SlowKn = 6.4, RouteKn = 20, TurnDegS = 1.3, SimEndS = 4000, HitNm = 0.1;

        private sealed class Torp
        {
            public double X, Y, VKn, CourseDeg;   // nm east / north, knots, course of a non-homing (receding) torpedo
            public bool Recede, Wake, DeadReckon;
            public double FreezeS;
            public double MarkX, MarkY, MvX, MvY;   // mark position and its velocity (nm/s) after the freeze (view b)
            public bool Frozen, Sunk;
        }

        private sealed class Scn
        {
            public string Name;
            public double DistNm, BrgDeg, RouteCourse, FreezeS, VKn;
            public bool Wake, DeadReckon, Recede;
            public int N = 1;
            public double VanishS = double.NaN, RunEndS = double.NaN;   // T-280: the track leaves the plot / the torpedo runs out of endurance (NaN = never)
        }

        private sealed class Result { public double HitS = double.NaN, SlowS, EndS = double.NaN; public int Toggles; public string Err; }

        private static double Brg(double dx, double dy) { return EvasionMath.Norm360(Math.Atan2(dx, dy) * 180 / Math.PI); }

        // the ADR-0091 end rule (HardMaxS 900, a frozen track within HoldNm holds up to HoldStaleMaxS) and slow rule (1 nm, the nearest track only)
        private static bool OldDone(EvState s, double now, EvTorp nearestFresh, double nearestFrozenNm)
        {
            const double hardMaxS = 900;
            if (now - s.StartedS >= hardMaxS) return true;
            bool fresh = nearestFresh != null;
            if (fresh && nearestFresh.DistNm <= EvasionMath.HoldNm) return false;
            if (nearestFrozenNm <= EvasionMath.HoldNm && now - s.LastSeenS < EvasionMath.HoldStaleMaxS) return false;
            if (now - s.StartedS >= EvasionMath.MaxS) return true;
            return !fresh && now - s.LastSeenS >= EvasionMath.DoneGoneS && s.LastDistNm > EvasionMath.DoneFarNm;
        }

        private static Result Run(Scn sc, bool oldRule, bool noLostNear = false)
        {
            var res = new Result();
            var torps = new List<Torp>();
            for (int i = 0; i < sc.N; i++)
            {
                double b = sc.BrgDeg + i * 3;
                var t = new Torp
                {
                    X = sc.DistNm * Math.Sin(b * Math.PI / 180), Y = sc.DistNm * Math.Cos(b * Math.PI / 180), VKn = sc.VKn,
                    Wake = sc.Wake, DeadReckon = sc.DeadReckon, Recede = sc.Recede, FreezeS = sc.FreezeS + i * 2
                };
                if (sc.Recede) t.CourseDeg = b;   // runs straight away from the ship
                torps.Add(t);
            }
            var pur = new TrackPursuit();
            var st = new EvState { Phase = "evading", StartedS = 0, LastSeenS = 0 };
            double sx = 0, sy = 0, hdg = sc.RouteCourse, esc = double.NaN;
            bool ended = false, slow = false, prevSlow = false;
            int toggles = 0;   // T-273: slow <-> full speed switches
            for (int sec = 1; sec <= SimEndS; sec++)
            {
                double now = sec;
                // torpedoes
                foreach (var t in torps)
                {
                    double v = t.VKn / 3600.0;
                    double dvx, dvy;
                    if (t.Recede) { dvx = Math.Sin(t.CourseDeg * Math.PI / 180) * v; dvy = Math.Cos(t.CourseDeg * Math.PI / 180) * v; }
                    else
                    {
                        double dx = sx - t.X, dy = sy - t.Y, d = Math.Sqrt(dx * dx + dy * dy);
                        dvx = d > 1e-9 ? dx / d * v : 0; dvy = d > 1e-9 ? dy / d * v : 0;
                    }
                    if (now > sc.RunEndS) { dvx = 0; dvy = 0; t.Sunk = true; }
                    t.X += dvx; t.Y += dvy;
                    if (!t.Frozen && now >= t.FreezeS) { t.Frozen = true; t.MarkX = t.X - dvx; t.MarkY = t.Y - dvy; t.MvX = dvx; t.MvY = dvy; }
                    if (t.Frozen && t.DeadReckon) { t.MarkX += t.MvX; t.MarkY += t.MvY; }
                }
                // the ship
                if (!ended)
                {
                    if (!double.IsNaN(esc)) { double d = EvasionMath.Diff(hdg, esc); hdg = EvasionMath.Norm360(hdg + Math.Max(-TurnDegS, Math.Min(TurnDegS, d))); }
                }
                else { double d = EvasionMath.Diff(hdg, sc.RouteCourse); hdg = EvasionMath.Norm360(hdg + Math.Max(-TurnDegS, Math.Min(TurnDegS, d))); }
                double kn = ended ? RouteKn : (slow ? SlowKn : FlankKn);
                if (slow && !ended) res.SlowS += 1;
                sx += Math.Sin(hdg * Math.PI / 180) * kn / 3600.0; sy += Math.Cos(hdg * Math.PI / 180) * kn / 3600.0;
                // hit
                foreach (var t in torps)
                {
                    if (t.Sunk) continue;
                    double dx = t.X - sx, dy = t.Y - sy;
                    if (Math.Sqrt(dx * dx + dy * dy) < HitNm) { res.HitS = now; return res; }
                }
                if (ended) continue;
                // the plot -> EvTorp list
                var list = new List<EvTorp>();
                int tid = 100;
                EvTorp nearestFresh = null; double nearestLive = double.PositiveInfinity, nearestFrozen = double.PositiveInfinity, minThreat = double.PositiveInfinity, minTrue = double.PositiveInfinity;
                bool anyWake = false; EvTorp nearestAny = null;
                foreach (var t in torps)
                {
                    tid++;
                    if (now >= sc.VanishS) continue;   // T-280: the track left the plot
                    double mx = t.Frozen ? t.MarkX : t.X, my = t.Frozen ? t.MarkY : t.Y;
                    double dmx = mx - sx, dmy = my - sy;
                    var e = new EvTorp { TrackId = tid, BrgDeg = Brg(dmx, dmy), DistNm = Math.Sqrt(dmx * dmx + dmy * dmy), Stale = t.Frozen, WakeHoming = t.Wake };
                    if (!oldRule)
                    {
                        pur.Update(1, tid, my / 60.0, mx / 60.0, !e.Stale, t.Wake, sy / 60.0, sx / 60.0, now);
                        if (e.Stale)
                        {
                            e.ThreatNm = pur.PursuitNm(1, tid, sy / 60.0, sx / 60.0);
                            e.PursuitExpired = pur.Expired(1, tid, now);
                        }
                    }
                    double th = oldRule ? e.DistNm : EvasionMath.ThreatDist(e);
                    if (th < minThreat) minThreat = th;
                    if (!double.IsInfinity(th) && t.Wake) anyWake = true;
                    if (e.DistNm < nearestLive) { nearestLive = e.DistNm; nearestAny = e; }
                    if (e.Stale) { if (th < nearestFrozen) nearestFrozen = th; }
                    else if (nearestFresh == null || e.DistNm < nearestFresh.DistNm) nearestFresh = e;
                    double dx2 = t.X - sx, dy2 = t.Y - sy, dt2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
                    if (dt2 < minTrue) minTrue = dt2;
                    list.Add(e);
                }
                if (nearestFresh != null)
                {
                    st.LastSeenS = now; st.LastDistNm = nearestFresh.DistNm; slow = false;
                    if (double.IsNaN(esc)) esc = EscapeCourse(hdg, nearestFresh.BrgDeg);
                    else esc = EscapeCourse(hdg, nearestFresh.BrgDeg, esc);
                }
                else
                {
                    st.LastDistNm = nearestLive;
                    slow = oldRule
                        ? now - st.LastSeenS >= EvasionMath.SonarSlowAfterS && nearestLive > 1.0 && !(nearestAny != null && nearestAny.WakeHoming)
                        : EvasionMath.SonarSlowNow(st, now, minThreat, anyWake, slow);   // T-273: hysteresis
                }
                if (!oldRule && slow != prevSlow)
                {
                    toggles++;
                    // (4) the slow run starts only beyond SonarSlowStartNm
                    if (slow && minThreat <= EvasionMath.SonarSlowStartNm && res.Err == null)
                        res.Err = sc.Name + ": slow run started at " + now + " s with threat " + minThreat.ToString("0.00", CultureInfo.InvariantCulture);
                }
                prevSlow = slow;
                if (!oldRule && !noLostNear) EvasionMath.NoteThreat(st, now, list);   // T-280
                bool done = oldRule ? OldDone(st, now, nearestFresh, nearestFrozen)
                                    : EvasionMath.Done(st, now, nearestFresh, nearestFresh != null, nearestLive, nearestFrozen);
                if (!oldRule && !done)
                {
                    // checks of the new rule: while a true-near, non-expired live threat exists the evasion must not end (checked below on end)
                }
                if (!oldRule && done && res.Err == null)
                {
                    // (1) no end while the true distance is within HoldNm (T-273: was NearHoldNm) before HardMaxS, unless every frozen pursuit expired
                    bool anyOpen = false;
                    foreach (var e in list) if (!e.Stale || !e.PursuitExpired) anyOpen = true;
                    if (minTrue <= EvasionMath.HoldNm && now < EvasionMath.HardMaxS && anyOpen && !sc.Recede)
                        res.Err = sc.Name + ": evasion ended at " + now + " s with the torpedo " + minTrue.ToString("0.00", CultureInfo.InvariantCulture) + " nm away";
                }
                if (!oldRule && done && nearestFresh == null && res.Err == null && now < EvasionMath.HardMaxS)
                {
                    // (3) T-273: a frozen-only evasion ends only when every threat is beyond HoldReleaseNm (or the pursuit expired: threat = infinity)
                    if (minThreat <= EvasionMath.HoldReleaseNm)
                        res.Err = sc.Name + ": evasion ended at " + now + " s with threat " + minThreat.ToString("0.00", CultureInfo.InvariantCulture);
                }
                if (!oldRule && slow && res.Err == null)
                {
                    // (2) no slow run while the true distance is within SonarSlowMinNm or a wake torpedo exists (the pursuit point is the model of the truth)
                    if (anyWake || minThreat <= EvasionMath.SonarSlowMinNm)
                        res.Err = sc.Name + ": slow run at " + now + " s with threat " + minThreat.ToString("0.00", CultureInfo.InvariantCulture) + " wake=" + anyWake;
                }
                if (done) { ended = true; res.EndS = now; }
            }
            res.Toggles = toggles;
            return res;
        }

        // escape course: the torpedo 150 deg off the bow, the smaller turn; the side is kept once chosen
        private static double EscapeCourse(double hdg, double brg, double prev = double.NaN)
        {
            double a = EvasionMath.Norm360(brg + 150), b = EvasionMath.Norm360(brg - 150);
            if (!double.IsNaN(prev))
                return Math.Abs(EvasionMath.Diff(prev, a)) <= Math.Abs(EvasionMath.Diff(prev, b)) ? a : b;
            return Math.Abs(EvasionMath.Diff(hdg, a)) <= Math.Abs(EvasionMath.Diff(hdg, b)) ? a : b;
        }

        private static string F(double v) { return double.IsNaN(v) ? "na" : v.ToString("0", CultureInfo.InvariantCulture); }

        public static string SelfTest()
        {
            var scns = new List<Scn>
            {
                new Scn { Name = "S1", DistNm = 3.48, BrgDeg = 171, RouteCourse = 148, FreezeS = 24, VKn = 25 },
                new Scn { Name = "S2", DistNm = 4.09, BrgDeg = 175, RouteCourse = 150, FreezeS = 22, VKn = 25 },
                new Scn { Name = "S3", DistNm = 4.07, BrgDeg = 170, RouteCourse = 150, FreezeS = 28, VKn = 25, DeadReckon = true },
                new Scn { Name = "S4", DistNm = 3.43, BrgDeg = 171, RouteCourse = 148, FreezeS = 24, VKn = 25, N = 2 },
                new Scn { Name = "S5", DistNm = 3.72, BrgDeg = 170, RouteCourse = 150, FreezeS = 80, VKn = 45, Wake = true },
                new Scn { Name = "S6", DistNm = 1.0, BrgDeg = 90, RouteCourse = 0, FreezeS = 30, VKn = 45, Wake = true, Recede = true },
            };
            // T-273: the run-15 cycle (the Yorktown cases 7048 and 7046)
            var cyc = new List<Scn>
            {
                new Scn { Name = "S7", DistNm = 3.70, BrgDeg = 218, RouteCourse = 194, FreezeS = 18, VKn = 25 },
                new Scn { Name = "S8", DistNm = 3.66, BrgDeg = 226, RouteCourse = 163, FreezeS = 12, VKn = 40, Wake = true },
            };
            foreach (var sc in cyc)
            {
                var nw = Run(sc, false);
                if (nw.Err != null) return nw.Err;
                var old = Run(sc, true);
                Console.WriteLine("t273 scenario " + sc.Name + " hit_s=" + F(nw.HitS) + " slow_s=" + F(nw.SlowS) + " end_s=" + F(nw.EndS) + " toggles=" + nw.Toggles + " | old hit_s=" + F(old.HitS) + " end_s=" + F(old.EndS));
                if (nw.Toggles > 2) return sc.Name + ": the slow run switched " + nw.Toggles + " times";
                if (!double.IsNaN(nw.HitS) && !double.IsNaN(old.HitS) && nw.HitS < old.HitS) return sc.Name + ": the new rule hit at " + nw.HitS + " s, earlier than the old one (" + old.HitS + " s)";
            }
            // T-280 (ADR-0113): the Udaloy case (N2). Numbers tuned in this 1 s simulator (the live torpedo of 43 kn would hit in both modes: the sim has no endurance
            // data): a 24 kn wake torpedo (between the 20 kn route run and the 24.5 kn flank run) 2.37 nm at 245 deg, route course 245 (back toward it), RunEndS 660:
            // with the memory the ship flees at flank until 519 s and the torpedo sinks before it catches up (it would hit at 696 s); without it the ship turns back at 400 s and is hit at 639 s.
            // The Udaloy case (N2): the tracks leave the plot at VanishS, the torpedo still runs and hits without the memory
            {
                var s9 = new Scn { Name = "S9", DistNm = 2.37, BrgDeg = 245, RouteCourse = 245, FreezeS = 20, VKn = 24, Wake = true, VanishS = 400, RunEndS = 660 };
                var nw = Run(s9, false);
                if (nw.Err != null) return nw.Err;
                var nm = Run(s9, false, true);
                Console.WriteLine("t280 scenario S9 hit_s=" + F(nw.HitS) + " end_s=" + F(nw.EndS) + " | nomem hit_s=" + F(nm.HitS) + " end_s=" + F(nm.EndS));
                if (Math.Abs(nm.EndS - s9.VanishS) > 1 || double.IsNaN(nm.HitS)) return "S9: without the memory the evasion must end at the vanish and the torpedo hit, end_s=" + F(nm.EndS) + " hit_s=" + F(nm.HitS);
                if (!double.IsNaN(nw.HitS)) return "S9: with the memory no hit, hit_s=" + F(nw.HitS);
                if (!double.IsNaN(nw.EndS) && nw.EndS < s9.VanishS + EvasionMath.LostNearHoldS - 1) return "S9: the memory must hold 120 s, end_s=" + F(nw.EndS);
            }
            foreach (var sc in scns)
            {
                var nw = Run(sc, false);
                if (nw.Err != null) return nw.Err;
                var old = Run(sc, true);
                Console.WriteLine("t268 scenario " + sc.Name + " hit_s=" + F(nw.HitS) + " slow_s=" + F(nw.SlowS) + " end_s=" + F(nw.EndS) + " | old hit_s=" + F(old.HitS) + " slow_s=" + F(old.SlowS) + " end_s=" + F(old.EndS));
                if (sc.Name != "S5" && sc.Name != "S6")
                {
                    if (double.IsNaN(old.HitS)) return sc.Name + ": the old rule must hit (the replay of the T-267 case)";
                    // S2/S3: the torpedo is still beyond 2 nm at the 300 s timer, the rule ends the evasion as before (the later route return is P3, out of scope): not earlier is enough
                    bool strict = sc.Name == "S1" || sc.Name == "S4";
                    if (!double.IsNaN(nw.HitS) && (strict ? nw.HitS <= old.HitS : nw.HitS < old.HitS)) return sc.Name + ": the new rule hit at " + nw.HitS + " s, earlier than (or as early as) the old one (" + old.HitS + " s)";
                }
                if (sc.Name == "S6")
                {
                    // R1: the receding frozen track must expire: the evasion ends before HardMaxS (pursuit_expired), not at it
                    if (double.IsNaN(nw.EndS) || nw.EndS >= EvasionMath.HardMaxS) return "S6: the expired pursuit must end the evasion before HardMaxS, end_s=" + F(nw.EndS);
                }
                else if ((sc.Name == "S1" || sc.Name == "S4") && !(nw.EndS > EvasionMath.MaxS + 1)) return sc.Name + ": ended at the 300 s timer (" + nw.EndS + " s)";
            }
            return null;
        }
    }
}
