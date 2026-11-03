using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-230 (ADR-0095, customer answer Q-T230-1): "turn onto the salvo". A torpedo inside the salvo distance but outside every firing arc of the
    // anti-torpedo mounts: the ship under its own evasion turns just enough to bring the torpedo into the nearest arc (with a margin), fires and
    // goes back to the evasion course at once. The turn is a temporary override of the evasion course (Evasion.cs sends it instead of its own
    // course); it never starts or stops the evasion. Pure maths, no game, no Unity: compiled by the atwtest stand and the selfcheck-ui.

    /// <summary>A firing arc of a mount as the game stores it (WeaponParameters._firingArcs, Vector2): x &lt; y plain, otherwise wrapped through 180; degrees, relative to the ship heading.</summary>
    internal sealed class AtwArc
    {
        public double X, Y;
        public AtwArc() { }
        public AtwArc(double x, double y) { X = x; Y = y; }
    }

    internal sealed class AtwTurnIn
    {
        public double HdgDeg, BrgDeg, DistNm;       // ship heading, true bearing to the torpedo, distance
        public double ClosingKn = double.NaN;       // measured closing speed (NaN = unknown)
        public double TorpKn = AntiTorpedoMath.TorpedoAssumedKn;   // speed of the torpedo: a ship turning beam-on is closed at about this speed, not at the measured stern closing
        public double ShipKn = double.NaN;          // T-237: own speed for the bearing drift while turning (NaN = no compensation)
        public double TtiS = double.PositiveInfinity;
        public double TurnRateDegS = AtwTurnMath.DefaultRateDegS;
        public double MinNm, SpeedKn, ReactionS = AntiTorpedoMath.DefaultReactionS, FireNm = AntiTorpedoMath.DefaultFireNm;
        public List<AtwArc> Arcs = new List<AtwArc>();
    }

    /// <summary>T-287 (ADR-0114): input of AtwTurnMath.FrozenAim. Flat earth, nm (x east, y north), origin = the own ship now.</summary>
    internal sealed class AtwFrozenIn
    {
        public double PX, PY;                 // pursuit point now
        public double PursuitKn;              // TrackPursuit.SpeedKn
        public double OwnHdgDeg, OwnKn;       // dead reckoning of the ship
        public double ReactionS = AntiTorpedoMath.DefaultReactionS;
        public double SalvoKn;                // AtwOption.SpeedKn; <= 0 -> AntiTorpedoMath.DefaultSpeedKn
        public double ExtraLeadS = AtwTurnMath.FrozenExtraLeadS;   // mount take-over + sink time
    }

    internal sealed class AtwFrozenAim
    {
        public double PursuitNm;
        public double AimX, AimY, AimNm, AimBrgDeg;   // pursuit point advanced by LeadS, relative to the ship now
        public double LeadS;
        public double TtiS = double.PositiveInfinity;
    }

    internal sealed class AtwTurnPlan
    {
        public bool Ok;
        public string Why;                          // Ok=false: "early" (wait) | "late" | "arcs" (no arcs known)
        public string Late;                         // Why=="late": "turn_deg" | "turn_s" | "min_range" | "hit_time"
        public double HeadingDeg, TurnDeg, TurnS, FireDistNm, TtiHitS;
        public double ArcFrom, ArcTo;               // the arc chosen, game values
    }

    internal static class AtwTurnMath
    {
        public const double MarginDeg = 10, LagS = 2, MaxTurnDeg = 120, MaxTurnS = 120, HitMarginS = 5, HoldMaxS = 10, TurnExtraS = 6, CooldownS = 10;
        public const double DefaultRateDegS = 1, MinRateDegS = 0.5, SlowRateFactor = 0.6, RateSafety = 0.8, RateWindowS = 60, MinFireAfterNm = 0.35;   // T-248: was 0.05; two turns started at 1.0-1.1 nm ended in a timeout in run 10
        public const double MaxYawDegS = 5;   // T-253: faster heading change between two samples is a jump (load, teleport), not a turn
        public const double BackArcMarginDeg = 5, BackSweepStepDeg = 5;   // T-258

        /// <summary>T-258: does the torpedo (true bearing brgDeg) stay inside one arc, keeping the margin, while the ship swings from hdgNowDeg
        /// to backHdgDeg the short way? false on NaN input or no arcs.</summary>
        public static bool StaysInArc(double brgDeg, double hdgNowDeg, double backHdgDeg, IList<AtwArc> arcs, double marginDeg)
        {
            if (arcs == null || arcs.Count == 0 || double.IsNaN(brgDeg) || double.IsNaN(hdgNowDeg) || double.IsNaN(backHdgDeg)) return false;
            double relNow = Norm180(brgDeg - hdgNowDeg), d = Norm180(backHdgDeg - hdgNowDeg);
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(d) / BackSweepStepDeg));
            foreach (AtwArc a in arcs)
            {
                double width = a.Y > a.X ? a.Y - a.X : a.Y + 360 - a.X;
                double m = width > 4 * marginDeg ? marginDeg : width * 0.25;
                bool all = true;
                for (int k = 0; k <= n && all; k++) if (!InGameArc(relNow - d * k / n, a.X + m, a.Y - m)) all = false;
                if (all) return true;
            }
            return false;
        }

        // T-287 (ADR-0114): salvo at the pursuit point of a frozen torpedo track
        public const double FrozenFireNm = 1.0, FrozenExtraLeadS = 8, FrozenStepS = 0.5, FrozenMaxSimS = 600;
        public const int FrozenMaxSalvos = 2;

        /// <summary>Walks TrackPursuit's rule (the point homes on the ship at PursuitKn, the ship runs OwnHdgDeg at OwnKn): the point at atS (relative to the
        /// ship now, clamped at the intercept) and the first time the point is within TrackPursuit.InterceptNm of the ship (+inf = never in FrozenMaxSimS).</summary>
        private static void FrozenWalk(AtwFrozenIn i, double atS, out double ax, out double ay, out double tti)
        {
            double kn = i.PursuitKn > 0 ? i.PursuitKn : TrackPursuit.DefaultKn;
            double hr = i.OwnHdgDeg * Math.PI / 180.0, own = double.IsNaN(i.OwnKn) ? 0 : i.OwnKn;
            double vx = Math.Sin(hr) * own / 3600.0, vy = Math.Cos(hr) * own / 3600.0, pk = kn / 3600.0;
            double sx = 0, sy = 0, px = i.PX, py = i.PY;
            ax = px; ay = py; tti = double.PositiveInfinity;
            bool got = atS <= 0;
            for (double t = 0; t <= FrozenMaxSimS + 1e-9; t += FrozenStepS)
            {
                if (!got && t >= atS) { ax = px; ay = py; got = true; }
                double dx = sx - px, dy = sy - py, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= TrackPursuit.InterceptNm)
                {
                    tti = t;
                    if (!got) { ax = sx; ay = sy; }   // the aim time is past the intercept: the point is on the ship by then
                    return;
                }
                double step = pk * FrozenStepS;
                if (step >= d) { px = sx; py = sy; } else { px += dx / d * step; py += dy / d * step; }
                sx += vx * FrozenStepS; sy += vy * FrozenStepS;
            }
            if (!got) { ax = px; ay = py; }
        }

        /// <summary>The pursuit point advanced by the lead (reaction + extra + flight, two passes) and the time to intercept of the pursuit model.</summary>
        public static AtwFrozenAim FrozenAim(AtwFrozenIn i)
        {
            var a = new AtwFrozenAim { AimX = double.NaN, AimY = double.NaN, AimNm = double.NaN, AimBrgDeg = double.NaN, LeadS = double.NaN };
            if (i == null || double.IsNaN(i.PX) || double.IsNaN(i.PY) || double.IsNaN(i.OwnHdgDeg)) return a;
            a.PursuitNm = Math.Sqrt(i.PX * i.PX + i.PY * i.PY);
            double salvo = i.SalvoKn > 0 ? i.SalvoKn : AntiTorpedoMath.DefaultSpeedKn;
            double lead = i.ReactionS + i.ExtraLeadS + a.PursuitNm / salvo * 3600.0, x, y, tti;
            FrozenWalk(i, lead, out x, out y, out tti);
            double nm = Math.Sqrt(x * x + y * y);
            lead = i.ReactionS + i.ExtraLeadS + nm / salvo * 3600.0;
            FrozenWalk(i, lead, out x, out y, out tti);
            a.LeadS = lead; a.TtiS = tti; a.AimX = x; a.AimY = y;
            a.AimNm = Math.Sqrt(x * x + y * y);
            a.AimBrgDeg = EvasionMath.Norm360(Math.Atan2(x, y) * 180.0 / Math.PI);
            return a;
        }

        /// <summary>null = fire; else far | expired | salvos | receding | late | min_range | out_of_range | no_arc | turn_back.</summary>
        public static string FrozenGate(AtwFrozenAim a, bool expired, int salvosSoFar, double minNm, double maxNm, double safetyNm,
                                        double hdgNowDeg, IList<AtwArc> arcs, bool backBlock, double backHdgDeg)
        {
            if (a == null || double.IsNaN(a.AimNm)) return "far";
            if (a.PursuitNm > FrozenFireNm) return "far";
            if (expired) return "expired";
            if (salvosSoFar >= FrozenMaxSalvos) return "salvos";
            if (double.IsPositiveInfinity(a.TtiS)) return "receding";
            if (a.TtiS < a.LeadS + HitMarginS) return "late";
            if (a.AimNm < Math.Max(minNm, safetyNm)) return "min_range";
            if (a.AimNm > maxNm) return "out_of_range";
            if (!StaysInArc(a.AimBrgDeg, hdgNowDeg, hdgNowDeg, arcs, BackArcMarginDeg)) return "no_arc";
            if (backBlock && a.TtiS >= AtwTurnState.BackUrgentTtiS && !StaysInArc(a.AimBrgDeg, hdgNowDeg, backHdgDeg, arcs, BackArcMarginDeg)) return "turn_back";
            return null;
        }

        /// <summary>Log fields of the frozen aim.</summary>
        public static string FrozenFields(AtwFrozenAim a, double hdgDeg)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return " pur_nm=" + a.PursuitNm.ToString("F2", c) + " aim_nm=" + (double.IsNaN(a.AimNm) ? "na" : a.AimNm.ToString("F2", c))
                + " aim_brg=" + (double.IsNaN(a.AimBrgDeg) ? "na" : a.AimBrgDeg.ToString("F0", c))
                + " rel=" + (double.IsNaN(a.AimBrgDeg) ? "na" : Norm180(a.AimBrgDeg - hdgDeg).ToString("F0", c))
                + " lead_s=" + (double.IsNaN(a.LeadS) ? "na" : a.LeadS.ToString("F0", c))
                + " tti=" + (double.IsPositiveInfinity(a.TtiS) ? "inf" : a.TtiS.ToString("F0", c));
        }

        public const double TurnExtraFrac = 0.3, MinObservedDegS = 0.3;   // T-237 (ADR-0100)
        public const double TurnTimeoutMinS = 20, ArriveDeg = 5, ArriveWaitS = 15, TurnCapS = MaxTurnS + 30, MaxShipKn = 40;   // T-261

        /// <summary>T-265: nobody is threatened (tti +inf) and the measured closing of the shooter is at most MinClosingKn and plausible (>= -(torpKn + MaxShipKn)).</summary>
        public static bool Receding(double ttiS, double closingKn, double torpKn)
        {
            return double.IsPositiveInfinity(ttiS) && !double.IsNaN(closingKn) && closingKn <= AntiTorpedoMath.MinClosingKn && closingKn >= -(torpKn + MaxShipKn);
        }

        /// <summary>T-261: true when the measured closing can be used for a prediction: MinClosingKn &lt; c &lt;= torpKn + MaxShipKn.</summary>
        public static bool PlausibleClosing(double closingKn, double torpKn)
        {
            return !double.IsNaN(closingKn) && closingKn > AntiTorpedoMath.MinClosingKn && closingKn <= torpKn + MaxShipKn;
        }

        /// <summary>T-261: mean closing over the turn from rel0Deg to rel1Deg: c + shipKn*(cos rel1 - cos rel0)/2 (own-speed component on the line of sight);
        /// shipKn NaN -> c; result >= 0. Implausible c -> 0 (no prediction: the fire distance is the distance now).</summary>
        public static double TurnClosingKn(double closingKn, double torpKn, double shipKn, double rel0Deg, double rel1Deg)
        {
            if (!PlausibleClosing(closingKn, torpKn)) return 0;
            if (double.IsNaN(shipKn)) return closingKn;
            double d = Math.Cos(rel1Deg * Math.PI / 180.0) - Math.Cos(rel0Deg * Math.PI / 180.0);
            return Math.Max(0, closingKn + shipKn * d / 2.0);
        }

        public static double Norm180(double a) { a %= 360; if (a > 180) a -= 360; else if (a <= -180) a += 360; return a; }

        /// <summary>The game's rule (WeaponSystem.RelativeBearingInFiringArcs): relative bearing inside one arc.</summary>
        public static bool InGameArc(double relDeg, double x, double y)
        {
            double r = Norm180(relDeg);
            if (x < y) return x < r && r < y;
            return y >= r || r >= x;
        }

        /// <summary>Smallest turn (deg, + = starboard) that brings the relative bearing into the arc keeping the margin; 0 when it is already inside.</summary>
        public static double TurnInto(double rel0, AtwArc a, double marginDeg, out double edgeFrom, out double edgeTo)
        {
            double from = a.X, to = a.Y < a.X ? a.Y + 360 : a.Y;
            edgeFrom = from; edgeTo = to;
            double width = to - from;
            double m = width > 2 * marginDeg ? marginDeg : width * 0.25;
            double lo = from + m, hi = to - m;
            double r = rel0;
            while (r < lo) r += 360;
            while (r > lo + 360) r -= 360;
            if (r <= hi) return 0;
            // r is outside [lo, hi]: turn so that rel' = rel0 - delta lands on lo or hi
            double da = Norm180(rel0 - lo), db = Norm180(rel0 - hi);
            return Math.Abs(da) <= Math.Abs(db) ? da : db;
        }

        public static AtwTurnPlan Plan(AtwTurnIn i)
        {
            var p = new AtwTurnPlan();
            if (i == null || i.Arcs == null || i.Arcs.Count == 0) { p.Why = "arcs"; return p; }
            if (double.IsNaN(i.BrgDeg) || double.IsNaN(i.HdgDeg) || double.IsNaN(i.DistNm) || double.IsNaN(i.TurnRateDegS)) { p.Why = "arcs"; return p; }   // review T-237: NaN input must never give Ok with a NaN heading
            double rel0 = Norm180(i.BrgDeg - i.HdgDeg);
            double best = double.NaN; double bf = 0, bt = 0;
            foreach (AtwArc a in i.Arcs)
            {
                if (a == null || (a.X == 0 && a.Y == 0)) continue;
                double ef, et;
                double d = TurnInto(rel0, a, MarginDeg, out ef, out et);
                if (double.IsNaN(best) || Math.Abs(d) < Math.Abs(best)) { best = d; bf = a.X; bt = a.Y; }
            }
            if (double.IsNaN(best)) { p.Why = "arcs"; return p; }
            double rate = Math.Max(MinRateDegS, i.TurnRateDegS);
            double turnS = Math.Abs(best) / rate + LagS;
            // the closing grows during the turn from the stern closing (torpedo minus ship) to about the torpedo speed (ship beam-on): the mean of both
            double torp = i.TorpKn > 0 ? i.TorpKn : AntiTorpedoMath.TorpedoAssumedKn;
            // T-261: from the geometry of the turn and a plausible measurement only (a missing / negative / false closing used to count as the torpedo speed: turns started beyond FireNm)
            double shipForClosing = double.IsNaN(i.ShipKn) ? ShipKnFromClosing(i.ClosingKn, torp) : i.ShipKn;
            double closing = TurnClosingKn(i.ClosingKn, torp, shipForClosing, rel0, rel0 - best);
            // T-237: a torpedo behind the beam drifts further astern while the ship turns and moves on; turn that much further (two passes)
            if (!double.IsNaN(i.ShipKn) && i.ShipKn > 0 && Math.Abs(rel0) > 90 && best * rel0 > 0)
            {
                double target = Math.Abs(rel0) - Math.Abs(best);
                for (int k = 0; k < 2; k++)
                {
                    double mid = Math.Max(0.3, i.DistNm - closing * turnS / 7200.0);
                    double drift = i.ShipKn / 3600.0 * Math.Sin(Math.Max(target, 30) * Math.PI / 180.0) / mid * 180.0 / Math.PI * turnS;
                    best = Math.Sign(best) * (Math.Abs(best) + Math.Min(drift, 60));
                    turnS = Math.Abs(best) / rate + LagS;
                    target = Math.Abs(rel0) - Math.Abs(best);
                }
            }
            double dFire = i.DistNm - closing * turnS / 3600.0;
            double speed = i.SpeedKn > 0 ? i.SpeedKn : AntiTorpedoMath.DefaultSpeedKn;
            double hitS = turnS + i.ReactionS + Math.Max(0, dFire) / speed * 3600.0;
            p.TurnDeg = best; p.TurnS = turnS; p.FireDistNm = dFire; p.TtiHitS = hitS; p.ArcFrom = bf; p.ArcTo = bt;
            p.HeadingDeg = EvasionMath.Norm360(i.HdgDeg + best);
            string late = Math.Abs(best) > MaxTurnDeg ? "turn_deg" : turnS > MaxTurnS ? "turn_s" : dFire < i.MinNm + MinFireAfterNm ? "min_range" : hitS + HitMarginS > i.TtiS ? "hit_time" : null;
            if (late != null) { p.Why = "late"; p.Late = late; return p; }
            if (dFire > i.FireNm) { p.Why = "early"; return p; }
            p.Ok = true;
            return p;
        }

        /// <summary>The non-arc gates of Allocate for a track that is only blocked by the arcs: null = the salvo would go once the arc is entered.</summary>
        public static string Gate(AtwTrackIn t, AtwWindows w, double safetyNm, double tHitS)
        {
            if (!t.Fresh && !(t.AgeS <= AntiTorpedoMath.AtwStaleFireMaxS)) return "track_stale";
            string eng = t.EngagedBy ?? (t.GameTask ? "game_task" : null);
            if (eng != null) return eng;
            if (w != null && !w.Allowed(t.TrackId, tHitS)) return "window";
            int blocker;
            if (AntiTorpedoMath.FriendlyNear(t.Near, safetyNm, out blocker)) return "friendly_near";
            return null;
        }

        /// <summary>T-237: even the nearest candidate the pre-filter cannot reject: the torpedo is still farther than FireNm after the longest allowed turn.</summary>
        public static bool PreEarly(double distNm, double fireNm) { return distNm - AntiTorpedoMath.TorpedoAssumedKn * MaxTurnS / 3600.0 > fireNm; }

        /// <summary>T-237: own speed from the stern closing (torpedo speed minus closing); NaN when the closing is unknown.</summary>
        public static double ShipKnFromClosing(double closingKn, double torpKn)
        {
            if (double.IsNaN(closingKn) || closingKn <= AntiTorpedoMath.MinClosingKn) return double.NaN;
            return Math.Min(torpKn, Math.Max(5, torpKn - closingKn));
        }

        /// <summary>T-261: max(TurnTimeoutMinS, plannedS + max(TurnExtraS, TurnExtraFrac*plannedS)).</summary>
        public static double TurnTimeoutS(double plannedS) { return Math.Max(TurnTimeoutMinS, plannedS + Math.Max(TurnExtraS, TurnExtraFrac * plannedS)); }

        private static string F(double v, string fmt) { return double.IsNaN(v) ? "na" : double.IsInfinity(v) ? (v > 0 ? "inf" : "-inf") : v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture); }

        /// <summary>T-248: range / bearing fields shared by the no_arc and arc_or_reload skip lines. Never throws.</summary>
        public static string RangeFields(double distNm, double brgDeg, double hdgDeg, double ttiS)
        {
            try
            {
                double rel = Norm180(brgDeg - hdgDeg);
                return " dist=" + F(distNm, "0.00") + " brg=" + F(EvasionMath.Norm360(brgDeg), "0") + " rel=" + F(rel, "0") + " eta_s=" + F(ttiS, "0");
            }
            catch (Exception) { return ""; }
        }

        /// <summary>T-248: true when a throttled log line may be written now (per key, at most once per gapS); records the time when it may.</summary>
        public static bool LogDue(Dictionary<long, double> last, long key, double nowS, double gapS)
        {
            double prev;
            if (double.IsNaN(nowS)) return true;
            if (last.TryGetValue(key, out prev) && nowS >= prev && nowS - prev < gapS) return false;
            last[key] = nowS;
            return true;
        }

        /// <summary>Fields of the atw no_arc line (T-237). Never throws; p == null: the plan fields are "na".</summary>
        public static string Fields(AtwTurnIn i, AtwTurnPlan p)
        {
            try
            {
                if (i == null) return " dist=na";
                double rel = Norm180(i.BrgDeg - i.HdgDeg);
                string s = RangeFields(i.DistNm, i.BrgDeg, i.HdgDeg, i.TtiS);
                s += " turn=" + (p == null ? "na" : F(p.TurnDeg, "0")) + " turn_s=" + (p == null ? "na" : F(p.TurnS, "0.0"));
                s += " fire_nm=" + (p == null ? "na" : F(p.FireDistNm, "0.00")) + " rate=" + F(i.TurnRateDegS, "0.0") + " closing=" + F(i.ClosingKn, "0.0");
                if (p != null && p.Late != null) s += " late=" + p.Late;
                return s;
            }
            catch (Exception) { return ""; }
        }

        public static double RateFor(double observedDegS, bool slowed)
        {
            double r = double.IsNaN(observedDegS) || observedDegS < MinObservedDegS ? DefaultRateDegS : Math.Max(MinRateDegS, observedDegS * RateSafety);
            return slowed ? Math.Max(MinRateDegS, r * SlowRateFactor) : r;
        }

        public static string SelfTest()
        {
            // T-248: fields of arc_or_reload, throttle, no fire right after a turn back
            string rf = RangeFields(1.5, 199, 95, 161);
            if (rf != " dist=1.50 brg=199 rel=104 eta_s=161") return "turn: range fields: " + rf;
            if (RangeFields(double.NaN, 10, 0, double.PositiveInfinity) != " dist=na brg=10 rel=10 eta_s=inf") return "turn: range fields na";
            var thr = new Dictionary<long, double>();
            if (!LogDue(thr, 1, 100, 10) || LogDue(thr, 1, 105, 10) || !LogDue(thr, 2, 105, 10) || !LogDue(thr, 1, 110.5, 10)) return "turn: throttle";
            if (!LogDue(thr, 1, 50, 10) || !LogDue(thr, 3, double.NaN, 10)) return "turn: throttle clock back / NaN";
            var bs = new AtwTurnState();
            var bp = new AtwTurnPlan { Ok = true, HeadingDeg = 95, TurnS = 68 };
            if (bs.FireBlocked(1, 100)) return "back: idle ship fires";
            bs.Start(1, 7, bp, 100); bs.NoteFire(1, 7, 170);
            if (bs.FireBlocked(1, 171)) return "back: turning ship is not blocked";
            if (bs.Result(1, true, 175) != "launched" || !bs.FireBlocked(1, 175) || !bs.FireBlocked(1, 175 + AtwTurnState.BackHoldMaxS - 1)) return "back: no fire for the next torpedo while turning back";
            if (bs.FireBlocked(1, 175 + AtwTurnState.BackHoldMaxS + 0.1)) return "back: block ends";
            bs.Start(1, 8, bp, 180);
            if (bs.FireBlocked(1, 181)) return "back: a new turn lifts the block";
            bs.Start(2, 7, new AtwTurnPlan { Ok = true, HeadingDeg = 95, TurnS = 4 }, 0);
            bs.Result(2, true, 5);
            if (!bs.FireBlocked(2, 5 + AtwTurnState.BackHoldMinS - 0.1) || bs.FireBlocked(2, 5 + AtwTurnState.BackHoldMinS + 0.1)) return "back: short turn -> min hold";
            if (AtwTurnState.BackBlocks(false, false, 100) || !AtwTurnState.BackBlocks(true, false, 100) || !AtwTurnState.BackBlocks(true, false, double.PositiveInfinity)) return "back: far torpedo out of arc stays blocked";
            if (AtwTurnState.BackBlocks(true, true, 100)) return "back: torpedo in the arc of a ready mount is not blocked";
            if (AtwTurnState.BackBlocks(true, false, AtwTurnState.BackUrgentTtiS - 0.1) || !AtwTurnState.BackBlocks(true, false, AtwTurnState.BackUrgentTtiS)) return "back: close torpedo (tti < 30) is not blocked";
            var th = new AtwTurnState();
            if (th.TurnHeld(9, 100, 100)) return "held: idle ship";
            th.Start(1, 7, bp, 100); th.NoteFire(1, 7, 170);
            if (th.Result(1, true, 175) != "launched" || !th.TurnHeld(1, 175, 100) || !th.TurnHeld(1, 175, double.PositiveInfinity) || !th.TurnHeld(1, 175, double.NaN)) return "held: turn back holds a new turn";
            if (th.TurnHeld(1, 175, AtwTurnState.BackUrgentTtiS - 0.1)) return "held: close torpedo bypass";
            if (th.TurnHeld(1, 175 + AtwTurnState.BackHoldMaxS + 0.1, 100)) return "held: block ends";
            th.Start(2, 7, bp, 0); th.Poll(2, 1, false);
            if (th.TurnHeld(2, 1, 100)) return "held: gone sets no block";
            th.Start(3, 7, bp, 0); th.Result(3, true, 5); th.End(3, "evasion_end");
            if (th.TurnHeld(3, 6, 100)) return "held: end clears";
            bs.Start(3, 7, bp, 0); bs.End(3, "evasion_end");
            if (bs.FireBlocked(3, 1)) return "back: end clears";
            // late by min range: a turn at 1.1 nm with the closing of run 10 does not fit
            var lt = new AtwTurnIn { HdgDeg = 17, BrgDeg = 175, DistNm = 1.11, ClosingKn = 33, TtiS = double.PositiveInfinity, TurnRateDegS = 1.1, ShipKn = 12, MinNm = 0.19, SpeedKn = 600, Arcs = new List<AtwArc> { new AtwArc(-115, -20), new AtwArc(20, 115) } };
            var ltp = Plan(lt);
            if (ltp.Ok || ltp.Why != "late" || ltp.Late != "min_range") return "turn plan: 1.11 nm run 10 -> late/min_range, got " + ltp.Why + "/" + ltp.Late;
            // T-258: StaysInArc and the heading to come back to
            var sa = new List<AtwArc> { new AtwArc(-115, -20), new AtwArc(20, 115) };
            if (StaysInArc(212, 139, 67, sa, 5)) return "stay: N4 run 0.30.15 leaves the arc";
            if (!StaysInArc(180, 120, 100, sa, 5)) return "stay: rel 60 -> 80 stays";
            if (!StaysInArc(212, 139, 139, sa, 5)) return "stay: already on the course";
            if (StaysInArc(130, 20, 20, sa, 5) || !StaysInArc(125, 20, 20, sa, 5)) return "stay: margin";
            if (StaysInArc(100, 50, 10, new List<AtwArc> { new AtwArc(20, 60), new AtwArc(80, 115) }, 5)) return "stay: dead zone between two arcs";
            if (!StaysInArc(170, 0, -20, new List<AtwArc> { new AtwArc(120, -120) }, 5)) return "stay: wrapped arc";
            if (!StaysInArc(90, 350, 10, sa, 5)) return "stay: short way through 0";
            if (StaysInArc(double.NaN, 0, 0, sa, 5) || StaysInArc(0, double.NaN, 0, sa, 5) || StaysInArc(50, 0, double.NaN, sa, 5) || StaysInArc(50, 0, 0, new List<AtwArc>(), 5) || StaysInArc(50, 0, 0, null, 5)) return "stay: NaN / no arcs";
            var narrow = new List<AtwArc> { new AtwArc(20, 32) };
            if (!StaysInArc(24, 0, 0, narrow, 5) || StaysInArc(21, 0, 0, narrow, 5)) return "stay: narrow arc margin is a quarter of the width";
            var hs = new AtwTurnState();
            hs.Start(1, 7, new AtwTurnPlan { Ok = true, HeadingDeg = 139, TurnDeg = 72, TurnS = 68 }, 0); hs.NoteFire(1, 7, 5);
            if (hs.Result(1, true, 10) != "launched" || Math.Abs(hs.BackHdgDeg(1, 10) - 67) > 1e-6) return "back hdg: " + hs.BackHdgDeg(1, 10);
            if (!double.IsNaN(hs.BackHdgDeg(1, 10 + AtwTurnState.BackHoldMaxS + 0.1))) return "back hdg: block ends";
            if (!double.IsNaN(hs.BackHdgDeg(9, 10))) return "back hdg: idle ship";
            hs.Start(2, 7, new AtwTurnPlan { Ok = true, HeadingDeg = 10, TurnDeg = 30, TurnS = 68 }, 0); hs.NoteFire(2, 7, 5); hs.Result(2, true, 10);
            if (Math.Abs(hs.BackHdgDeg(2, 10) - 340) > 1e-6) return "back hdg: wrap " + hs.BackHdgDeg(2, 10);
            hs.End(2, "evasion_end");
            if (!double.IsNaN(hs.BackHdgDeg(2, 10))) return "back hdg: end clears";
            hs.Start(3, 7, new AtwTurnPlan { Ok = true, HeadingDeg = 100, TurnDeg = 20, TurnS = 68 }, 0); hs.Poll(3, 1, false);
            if (!double.IsNaN(hs.BackHdgDeg(3, 1))) return "back hdg: gone sets no block";
            // game arc rule
            if (!InGameArc(-50, -115, -20) || InGameArc(-10, -115, -20) || InGameArc(-115, -115, -20)) return "turn: plain arc";
            if (!InGameArc(180, 120, -120) || InGameArc(0, 120, -120)) return "turn: wrapped arc";
            // Udaloy: -115..-20 and 20..115, torpedo astern (rel 180): nearest edge 115-10 = 105 on starboard side
            var arcs = new List<AtwArc> { new AtwArc(-115, -20), new AtwArc(20, 115) };
            double ef, et;
            double d = TurnInto(180, arcs[1], 10, out ef, out et);
            if (Math.Abs(Math.Abs(d) - 75) > 1e-6) return "turn: stern -> 75 deg, got " + d;
            if (TurnInto(60, arcs[1], 10, out ef, out et) != 0) return "turn: inside the margin needs no turn";
            d = TurnInto(112, arcs[1], 10, out ef, out et);
            if (Math.Abs(d - 7) > 1e-6) return "turn: edge of the arc -> 7 deg towards the middle, got " + d;
            // rel0 = 180 with the turn d: rel' = rel0 - d must be inside the inner range
            double d1 = TurnInto(180, arcs[1], 10, out ef, out et), d2 = TurnInto(180, arcs[0], 10, out ef, out et);
            if (!InGameArc(180 - d1, 20, 115) || !InGameArc(180 - d2, -115, -20)) return "turn: the turned bearing is inside the arc";
            // plan: Udaloy, torpedo astern at 1.6 nm closing 25 kn, 4 deg/s
            var inp = new AtwTurnIn { HdgDeg = 0, BrgDeg = 180, DistNm = 1.6, ClosingKn = 25, TtiS = 230, TurnRateDegS = 4, MinNm = 0.19, SpeedKn = 600, Arcs = arcs };
            AtwTurnPlan pl = Plan(inp);
            if (!pl.Ok) return "turn plan: should be ok, why=" + pl.Why;
            if (Math.Abs(Math.Abs(pl.TurnDeg) - 75) > 1e-6 || Math.Abs(pl.TurnS - (75 / 4.0 + LagS)) > 1e-6) return "turn plan: 75 deg, 20.75 s";
            // too early: the torpedo will still be beyond FireNm when the turn ends
            inp.DistNm = 2.5;
            pl = Plan(inp);
            if (pl.Ok || pl.Why != "early") return "turn plan: 2.5 nm -> early, got " + pl.Why;
            // too late: the torpedo would be inside the minimum range of the RBU when the turn ends
            inp.DistNm = 0.35;
            pl = Plan(inp);
            if (pl.Ok || pl.Why != "late") return "turn plan: 0.35 nm -> late, got " + pl.Why;
            // too late by the hit time
            inp.DistNm = 1.5; inp.TtiS = 20;
            pl = Plan(inp);
            if (pl.Ok || pl.Why != "late") return "turn plan: tti 20 s -> late, got " + pl.Why;
            if (pl.Late != "hit_time") return "turn plan: tti 20 s -> hit_time, got " + pl.Late;
            // slow ship: 0.5 deg/s, 75 deg = 152 s (more than MaxTurnS)
            inp.TtiS = 230; inp.TurnRateDegS = 0.5;
            pl = Plan(inp);
            if (pl.Ok || pl.Why != "late") return "turn plan: 0.5 deg/s -> late (more than MaxTurnS), got " + pl.Why;
            // T-237: the window between early and late exists at 0.8 deg/s (Udaloy, torpedo astern, 13 kn closing)
            var w1 = new AtwTurnIn { HdgDeg = 0, BrgDeg = 180, DistNm = 2.1, ClosingKn = 13, TtiS = 600, TurnRateDegS = 0.8, MinNm = 0.19, SpeedKn = 600, Arcs = arcs };
            pl = Plan(w1);
            if (!pl.Ok || Math.Abs(Math.Abs(pl.TurnDeg) - 75) > 1e-6 || Math.Abs(pl.TurnS - 95.75) > 1e-6) return "turn window: 2.1 nm ok 75 deg 95.75 s, got " + pl.Why + " " + pl.TurnDeg + " " + pl.TurnS;
            string fl = Fields(w1, pl);
            if (!fl.Contains("dist=2.10 brg=180 rel=180 eta_s=600 turn=-75") && !fl.Contains("dist=2.10 brg=180 rel=180 eta_s=600 turn=75")) return "turn fields: " + fl;
            w1.DistNm = 2.8; pl = Plan(w1);
            if (pl.Ok || pl.Why != "early") return "turn window: 2.8 nm -> early, got " + pl.Why;
            w1.DistNm = 0.35; pl = Plan(w1);
            if (pl.Ok || pl.Why != "late" || pl.Late != "min_range") return "turn window: 0.35 nm -> late/min_range, got " + pl.Why + "/" + pl.Late;
            if (!Fields(w1, pl).Contains("late=min_range")) return "turn fields: late=";
            w1.DistNm = 2.1; w1.TurnRateDegS = 0.5; pl = Plan(w1);
            if (pl.Why != "late" || pl.Late != "turn_s") return "turn window: 0.5 deg/s -> late/turn_s, got " + pl.Why + "/" + pl.Late;
            if (!Fields(w1, pl).Contains("late=turn_s")) return "turn fields: late=turn_s";
            string fn = Fields(w1, null);
            if (!fn.Contains("turn_s=na") || fn.Contains(",")) return "turn fields: null plan: " + fn;
            var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
            try { var ru = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone(); ru.NumberFormat.NumberDecimalSeparator = ","; System.Globalization.CultureInfo.CurrentCulture = ru; if (Fields(w1, pl).Contains(",")) return "turn fields: culture"; }
            finally { System.Globalization.CultureInfo.CurrentCulture = oldCulture; }
            w1.DistNm = double.NaN; pl = Plan(w1);
            if (pl.Ok) return "turn plan: NaN distance must not be ok";
            w1.DistNm = 2.1;
            if (!PreEarly(3.1, 1.5) || PreEarly(2.9, 1.5)) return "pre-early: 3.1 true, 2.9 false";
            if (TurnTimeoutS(20) != 26 || Math.Abs(TurnTimeoutS(100) - 130) > 1e-9) return "turn timeout: 20 -> 26, 100 -> 130";
            if (RateFor(0.1, false) != DefaultRateDegS || RateFor(double.NaN, false) != 1 || Math.Abs(RateFor(1.25, false) - 1.0) > 1e-9 || Math.Abs(RateFor(1.25, true) - 0.6) > 1e-9) return "rate: T-237 values";
            inp.TurnRateDegS = 0.5;
            // no arcs known
            inp.Arcs = new List<AtwArc>();
            if (Plan(inp).Why != "arcs") return "turn plan: no arcs";
            // gates
            var t = new AtwTrackIn { TrackId = 5, DistNm = 1.4 };
            if (Gate(t, new AtwWindows(), 0.15, 100) != null) return "turn gate: free";
            t.EngagedBy = "own_order";
            if (Gate(t, new AtwWindows(), 0.15, 100) != "own_order") return "turn gate: own_order";
            t.EngagedBy = null; t.Fresh = false; t.AgeS = 90;
            if (Gate(t, new AtwWindows(), 0.15, 100) != "track_stale") return "turn gate: stale";
            t.Fresh = true;
            var w = new AtwWindows(); w.Note(5, 100);
            if (Gate(t, w, 0.15, 105) != "window") return "turn gate: window";
            // yaw rate
            if (RateFor(double.NaN, false) != DefaultRateDegS) return "rate: default";
            if (Math.Abs(RateFor(5, false) - 4) > 1e-9) return "rate: observed * safety";
            if (Math.Abs(RateFor(5, true) - 2.4) > 1e-9) return "rate: slowed";
            var yr = new AtwYawRate();
            for (int k = 0; k <= 10; k++) yr.Update(1, k * 3.0, k);   // 3 deg/s
            if (Math.Abs(yr.MaxDegS(1, 10) - 3) > 0.05) return "yaw rate: 3 deg/s, got " + yr.MaxDegS(1, 10);
            var yj = new AtwYawRate();
            if (yj.Update(3, 0, 0)) return "yaw jump: first sample";
            if (!yj.Update(3, 102, 3) || Math.Abs(yj.LastJumpDeg - 102) > 0.01 || Math.Abs(yj.LastJumpS - 3) > 0.01) return "yaw jump: 0 -> 102 in 3 s";
            for (int k = 1; k <= 10; k++) if (yj.Update(3, 102 + k, 3 + k)) return "yaw jump: 1 deg/s is no jump";
            if (Math.Abs(yj.PeakDegS(3) - 1) > 0.05 || Math.Abs(yj.MaxDegS(3, 13) - 1) > 0.05) return "yaw jump: peak " + yj.PeakDegS(3);
            var y5 = new AtwYawRate();
            for (int k = 0; k <= 10; k++) if (y5.Update(4, k * 5.0, k)) return "yaw jump: 5 deg/s is no jump";
            if (Math.Abs(y5.PeakDegS(4) - 5) > 0.05) return "yaw jump: 5 deg/s peak " + y5.PeakDegS(4);
            var y6 = new AtwYawRate();
            for (int k = 0; k <= 10; k++) y6.Update(5, k * 6.0, k);
            if (!double.IsNaN(y6.PeakDegS(5)) || AtwTurnMath.RateFor(y6.PeakDegS(5), false) != AtwTurnMath.RateFor(double.NaN, false)) return "yaw jump: 6 deg/s must give no peak";
            var y0 = new AtwYawRate();
            if (y0.Update(6, 359, 0) | y0.Update(6, 2, 3)) return "yaw jump: across 0 deg";
            var ye = new AtwYawRate();
            for (int k = 0; k <= 10; k++) ye.Update(7, k * 1.0, k);
            if (!ye.Update(7, 200, 12)) return "yaw jump: after peak";
            for (int k = 13; k <= 30; k++) ye.Update(7, 200, k);
            if (Math.Abs(ye.PeakDegS(7) - 1) > 0.05) return "yaw jump: peak kept " + ye.PeakDegS(7);
            var yp = new AtwYawRate();
            for (int k = 0; k <= 10; k++) yp.Update(2, k * 1.0, k);
            for (int k = 11; k <= 80; k++) yp.Update(2, 10, k);
            if (yp.MaxDegS(2, 80) > 0.05 || Math.Abs(yp.PeakDegS(2) - 1) > 0.05) return "yaw peak: " + yp.PeakDegS(2);
            yp.Clear();
            if (!double.IsNaN(yp.PeakDegS(2))) return "yaw peak: clear";
            if (!double.IsNaN(yr.MaxDegS(2, 10))) return "yaw rate: unknown uid";
            // state machine
            var st = new AtwTurnState();
            var ok = new AtwTurnPlan { Ok = true, HeadingDeg = 255, TurnS = 20 };
            if (st.Override(1).HasValue) return "state: idle has no override";
            if (!st.CanStart(1, 100)) return "state: can start";
            st.Start(1, 7, ok, 100);
            if (st.Override(1) != 255 || st.CanStart(1, 101)) return "state: turning overrides, no second start";
            if (st.Poll(1, 110, true) != null) return "state: turning keeps";
            st.NoteFire(1, 7, 112);
            if (st.PhaseOf(1) != "firing" || st.Override(1) != 255) return "state: fire keeps the heading until the result";
            if (st.Result(1, true, 114) != "launched" || st.Override(1).HasValue) return "state: result -> back";
            if (!st.CanStart(1, 114)) return "state: repeat allowed after launched";
            st.Start(1, 7, ok, 120);
            st.NoteFire(1, 7, 122);
            if (st.Poll(1, 122 + HoldMaxS, true) != "hold" || st.Override(1).HasValue) return "state: hold limit -> back";
            if (st.CanStart(1, 123)) return "state: cooldown after a hold back";
            if (!st.CanStart(1, 122 + HoldMaxS + CooldownS)) return "state: cooldown ends";
            st.Start(1, 7, ok, 200);
            if (st.Poll(1, 200 + 20 + TurnExtraS, true) != "timeout") return "state: turn without a fire -> timeout";
            var longPlan = new AtwTurnPlan { Ok = true, HeadingDeg = 255, TurnS = 100 };
            st.Start(1, 7, longPlan, 500);
            if (st.Poll(1, 620, true) != null || st.Poll(1, 630, true) != "timeout") return "state: long turn timeout 130 s";
            st.Start(1, 7, ok, 300);
            if (st.Poll(1, 301, false) != "gone") return "state: torpedo gone";
            st.Start(1, 7, ok, 400);
            st.End(1, "evasion_end");
            if (st.Override(1).HasValue) return "state: evasion end clears";
            string e261 = SelfTest261(arcs);
            return e261 ?? SelfTest265(arcs);
        }

        // T-265: salvo arc check until the ship is back on the evasion course; no fire at receding torpedoes
        private static string SelfTest265(List<AtwArc> arcs)
        {
            var n = new AtwTurnState();
            n.Start(1, 7, new AtwTurnPlan { Ok = true, HeadingDeg = 215, TurnDeg = -100, TurnS = 95 }, 0);
            double t0 = 130;
            if (n.Poll(1, t0, true, true, double.NaN) != "timeout") return "t265: N1 timeout expected";
            double cap = AtwTurnState.BackArcCapS;
            if (!n.FireBlocked(1, t0 + 72, 276, 315, cap)) return "t265: N1 blocked at 276 -> 315 after 72 s";
            if (n.FireBlocked(1, t0 + 72, 276, 315)) return "t265: N1 old cap 40 s lifts the block";
            if (n.FireBlocked(1, t0 + 72, 306, 315, cap)) return "t265: N1 within 10 deg";
            if (n.FireBlocked(1, t0 + 121, 276, 315, cap)) return "t265: N1 ceiling 120 s";
            if (Math.Abs(n.BackHdgDeg(1, t0 + 72, 276, 315, cap) - 315) > 1e-9) return "t265: N1 back hdg";
            if (Math.Abs(n.BackHdgDeg(1, t0 + 72, 276, double.NaN, cap) - 315) > 1e-9) return "t265: N1 back hdg without a course";
            if (!n.FireBlocked(1, t0 + 5, double.NaN, 315, cap) || n.FireBlocked(1, t0 + 30, double.NaN, 315, cap)) return "t265: NaN heading -> old window";
            if (StaysInArc(152, 276, 315, arcs, 5)) return "t265: N1 salvo at 7049 must be held (leaves the arc)";
            if (AtwTurnState.BackBlocks(true, false, AtwTurnState.BackUrgentTtiS - 1)) return "t265: T-248 bypass";
            if (n.TurnHeld(1, t0 + 72, 100, 276, 315)) return "t265: turns keep the 40 s cap";
            double inf = double.PositiveInfinity;
            if (!Receding(inf, -16.5, 45) || !Receding(inf, 0.3, 45) || Receding(inf, 0.8, 45)) return "t265: Receding closing";
            if (Receding(inf, double.NaN, 45) || Receding(inf, -91, 45)) return "t265: Receding NaN / implausible";
            if (Receding(600, -16.5, 45) || Receding(20, -16.5, 45)) return "t265: Receding finite tti";
            return null;
        }


        // T-261: closing prediction, turn timeout, frozen track, turn-back block by heading
        private static string SelfTest261(List<AtwArc> arcs)
        {
            if (PlausibleClosing(120.1, 45) || PlausibleClosing(-91, 45) || !PlausibleClosing(19.9, 45) || PlausibleClosing(double.NaN, 45)) return "t261: PlausibleClosing";
            if (TurnClosingKn(13, 45, double.NaN, 180, 105) != 13 || TurnClosingKn(120.1, 45, 30, 180, 105) != 0 || TurnClosingKn(-5, 45, 30, 180, 105) != 0) return "t261: TurnClosingKn NaN ship / implausible";
            if (Math.Abs(TurnClosingKn(13, 45, 32, 180, 105) - 24.86) > 0.05) return "t261: TurnClosingKn w1 " + TurnClosingKn(13, 45, 32, 180, 105);
            // the cases of run 0.30.16: all early (dist > FireNm)
            double[][] cases = { new[] { 1.65, 19.9, 117, 98 }, new[] { 1.83, 15.7, 133, 89 }, new[] { 1.74, 18.1, 123, 95 }, new[] { 1.77, 120.1, 130, 100 }, new[] { 1.94, double.NaN, 130, 100 }, new[] { 1.74, -91, 130, 100 } };
            foreach (double[] c in cases)
            {
                var ci = new AtwTurnIn { HdgDeg = 0, BrgDeg = c[2], DistNm = c[0], ClosingKn = c[1], TtiS = 600, TurnRateDegS = 1, MinNm = 0.19, SpeedKn = 600, Arcs = arcs };
                ci.ShipKn = ShipKnFromClosing(ci.ClosingKn, ci.TorpKn);
                var cp = Plan(ci);
                if (cp.Ok || cp.Why != "early") return "t261: run 0.30.16 dist " + c[0] + " closing " + c[1] + " -> early, got " + cp.Why + " fire_nm=" + cp.FireDistNm;
            }
            var w1 = new AtwTurnIn { HdgDeg = 0, BrgDeg = 180, DistNm = 2.1, ClosingKn = 13, TtiS = 600, TurnRateDegS = 0.8, MinNm = 0.19, SpeedKn = 600, Arcs = arcs };
            var w1p = Plan(w1);
            if (!w1p.Ok || Math.Abs(w1p.FireDistNm - 1.44) > 0.02) return "t261: w1 fire_nm " + w1p.FireDistNm;
            // timeout
            if (TurnTimeoutS(4) != 20 || TurnTimeoutS(20) != 26 || Math.Abs(TurnTimeoutS(100) - 130) > 1e-9) return "t261: TurnTimeoutS";
            var ts = new AtwTurnState();
            var p115 = new AtwTurnPlan { Ok = true, HeadingDeg = 100, TurnDeg = 11, TurnS = 11.5 };
            ts.Start(1, 7, p115, 0);
            if (ts.Poll(1, 10, true, true, 102) != null || !ts.Arrived(1) || Math.Abs(ts.LeftDeg(1, 102) - 2) > 1e-9) return "t261: arrived at 10 s";
            if (ts.Poll(1, 24.9, true, true, 100) != null) return "t261: no timeout before arrived + wait";
            if (ts.Poll(1, 25.1, true, true, 100) != "timeout") return "t261: timeout after arrived + wait";
            var tp = new AtwTurnState();
            tp.Start(1, 7, new AtwTurnPlan { Ok = true, HeadingDeg = 170, TurnDeg = 170, TurnS = 18.5 }, 0);
            string pr = null; double tEnd = -1;
            for (int k = 0; k <= 200 && pr == null; k++) { pr = tp.Poll(1, k, true, true, 0.5 * k); tEnd = k; }
            if (pr != "timeout" || Math.Abs(tEnd - TurnCapS) > 1) return "t261: slow turn runs to the cap, got " + pr + " at " + tEnd;
            var tq = new AtwTurnState();
            tq.Start(1, 7, new AtwTurnPlan { Ok = true, HeadingDeg = 170, TurnDeg = 170, TurnS = 18.5 }, 0);
            string pq = null; double tq1 = -1;
            for (int k = 0; k <= 200 && pq == null; k++) { pq = tq.Poll(1, k, true, true, 0); tq1 = k; }
            if (pq != "timeout" || Math.Abs(tq1 - TurnTimeoutS(18.5)) > 1) return "t261: no progress -> normal timeout, at " + tq1;
            var tf = new AtwTurnState();
            tf.Start(1, 7, p115, 0);
            if (tf.Poll(1, 3, true, false, 100) != "frozen" || tf.PhaseOf(1) != "idle" || tf.CanStart(1, 5) || !tf.CanStart(1, 3 + CooldownS)) return "t261: frozen track";
            if (!tf.FireBlocked(1, 4)) return "t261: frozen sets the block";
            // N2: turn back block by heading
            var tb = new AtwTurnState();
            tb.Start(1, 7, new AtwTurnPlan { Ok = true, HeadingDeg = 132, TurnDeg = 19, TurnS = 18.5 }, 0);
            double t0 = 30;
            if (tb.Poll(1, t0, true, true, 113) != "timeout") return "t261: N2 timeout expected";
            if (!tb.FireBlocked(1, t0 + 15, 105, 80)) return "t261: N2 blocked at 105 -> 80";
            if (Math.Abs(tb.BackHdgDeg(1, t0 + 15, 105, 80) - 80) > 1e-9) return "t261: N2 back hdg";
            if (tb.FireBlocked(1, t0 + 15, 85, 80) || !double.IsNaN(tb.BackHdgDeg(1, t0 + 15, 85, 80))) return "t261: N2 within 10 deg";
            if (tb.FireBlocked(1, t0 + 41, 105, 80)) return "t261: N2 cap 40 s";
            if (!tb.FireBlocked(1, t0 + 5, double.NaN, 80) || tb.FireBlocked(1, t0 + 12, double.NaN, 80)) return "t261: N2 NaN heading -> old window";
            if (Math.Abs(tb.BackHdgDeg(1, t0 + 15, 105, double.NaN) - 113) > 1e-9) return "t261: N2 back to FromHdg without a course";
            if (!tb.TurnHeld(1, t0 + 15, 100, 105, 80) || tb.TurnHeld(1, t0 + 15, 29, 105, 80)) return "t261: TurnHeld tti bypass";
            return null;
        }
    }

    /// <summary>Yaw rate of a ship: the largest |dHdg/dt| over pairs 2..4 s apart in the last RateWindowS (the evasion itself turns the ship hard at its start).</summary>
    internal sealed class AtwYawRate
    {
        private sealed class Series { public readonly List<double> T = new List<double>(), H = new List<double>(); }
        private readonly Dictionary<int, Series> _s = new Dictionary<int, Series>();
        private readonly Dictionary<int, double> _peak = new Dictionary<int, double>();

        /// <summary>Adds a sample. true = the heading jumped faster than MaxYawDegS since the previous sample: the series restarts at this sample (the peak is kept).</summary>
        public bool Update(int uid, double hdgDeg, double nowS)
        {
            Series s;
            if (!_s.TryGetValue(uid, out s)) { s = new Series(); _s[uid] = s; }
            int n = s.T.Count;
            if (n > 0 && nowS < s.T[n - 1]) { s.T.Clear(); s.H.Clear(); n = 0; }
            if (n > 0 && nowS - s.T[n - 1] < 0.5) return false;
            bool jump = false;
            if (n > 0)
            {
                double dt = nowS - s.T[n - 1], d = Math.Abs(AtwTurnMath.Norm180(hdgDeg - s.H[n - 1]));
                if (d / dt > AtwTurnMath.MaxYawDegS) { jump = true; LastJumpDeg = d; LastJumpS = dt; s.T.Clear(); s.H.Clear(); }
            }
            s.T.Add(nowS); s.H.Add(hdgDeg);
            while (s.T.Count > 0 && nowS - s.T[0] > AtwTurnMath.RateWindowS) { s.T.RemoveAt(0); s.H.RemoveAt(0); }
            MaxDegS(uid, nowS);   // T-237: keeps the mission peak current
            return jump;
        }

        public double LastJumpDeg { get; private set; }   // |dHdg| of the last jump, deg
        public double LastJumpS { get; private set; }     // dt of the last jump, s

        /// <summary>The largest MaxDegS seen for the uid in this mission (NaN = never measured); Clear() resets it.</summary>
        public double PeakDegS(int uid) { double v; return _peak.TryGetValue(uid, out v) ? v : double.NaN; }

        /// <summary>NaN = fewer than 3 samples.</summary>
        public double MaxDegS(int uid, double nowS)
        {
            Series s;
            if (!_s.TryGetValue(uid, out s) || s.T.Count < 3) return double.NaN;
            double best = 0;
            for (int i = 0; i < s.T.Count; i++)
                for (int j = i + 1; j < s.T.Count; j++)
                {
                    double dt = s.T[j] - s.T[i];
                    if (dt < 2) continue;
                    if (dt > 4) break;
                    double r = Math.Abs(AtwTurnMath.Norm180(s.H[j] - s.H[i])) / dt;
                    if (r > AtwTurnMath.MaxYawDegS) continue;   // T-253: cannot reach the peak
                    if (r > best) best = r;
                }
            double pk;
            if (!_peak.TryGetValue(uid, out pk) || best > pk) _peak[uid] = best;
            return best;
        }

        public void Clear() { _s.Clear(); _peak.Clear(); }
    }

    /// <summary>Per ship: idle -> turning (heading override, waiting for the fire) -> firing (override kept until the launch result) -> idle.</summary>
    internal sealed class AtwTurnState
    {
        private sealed class Run
        {
            public string Phase = "idle";
            public double HeadingDeg, StartedS, FiredS, PlannedS, CooldownUntilS = double.NegativeInfinity;
            public int TrackId;
            public double BackUntilS = double.NegativeInfinity;
            public double FromHdgDeg = double.NaN;   // T-258: heading at Start: the evasion course the ship comes back to
            public double BackSinceS = double.NaN, ArrivedS = double.NaN, LastLeftDeg = double.NaN, LastPollS = double.NaN, DeadlineS;   // T-261
        }
        private readonly Dictionary<int, Run> _r = new Dictionary<int, Run>();

        public int Count { get { int n = 0; foreach (Run r in _r.Values) if (r.Phase != "idle") n++; return n; } }

        private Run Get(int uid) { Run r; if (!_r.TryGetValue(uid, out r)) { r = new Run(); _r[uid] = r; } return r; }

        /// <summary>T-234: the ships turning onto the salvo now (phase is not idle).</summary>
        public List<int> ActiveUids() { var l = new List<int>(); foreach (var kv in _r) if (kv.Value.Phase != "idle") l.Add(kv.Key); return l; }
        public string PhaseOf(int uid) { Run r; return _r.TryGetValue(uid, out r) ? r.Phase : "idle"; }

        public double? Override(int uid)
        {
            Run r;
            if (!_r.TryGetValue(uid, out r) || r.Phase == "idle") return null;
            return r.HeadingDeg;
        }

        public int TrackOf(int uid) { Run r; return _r.TryGetValue(uid, out r) && r.Phase != "idle" ? r.TrackId : -1; }

        /// <summary>T-248: seconds a ship that turned back gets no salvo at another torpedo (the mount leaves its arc while the ship comes back).</summary>
        public const double BackHoldMinS = 8, BackHoldMaxS = 25;

        /// <summary>T-248: true while the ship turns back to its evasion course after a salvo: a new order now fails with TargetIsNotInFiringArcs.</summary>
        public bool FireBlocked(int uid, double nowS)
        {
            Run r;
            return _r.TryGetValue(uid, out r) && r.Phase == "idle" && nowS < r.BackUntilS;
        }

        /// <summary>T-261: blocked while |hdgNow - back| > BackDoneDeg and nowS &lt; back start + BackHoldCapS; back = courseDeg (evasion course) or FromHdgDeg when NaN.
        /// NaN hdgNow or no back heading -> the old time rule.</summary>
        public bool FireBlocked(int uid, double nowS, double hdgNowDeg, double courseDeg)
        {
            return FireBlocked(uid, nowS, hdgNowDeg, courseDeg, BackHoldCapS);
        }

        /// <summary>T-265: as FireBlocked(uid, nowS, hdg, course) with the cap capS instead of BackHoldCapS.</summary>
        public bool FireBlocked(int uid, double nowS, double hdgNowDeg, double courseDeg, double capS)
        {
            Run r;
            if (!_r.TryGetValue(uid, out r) || r.Phase != "idle") return false;
            double back = double.IsNaN(courseDeg) ? r.FromHdgDeg : courseDeg;
            if (double.IsNaN(hdgNowDeg) || double.IsNaN(back) || double.IsNaN(r.BackSinceS)) return nowS < r.BackUntilS;
            return nowS < r.BackSinceS + capS && Math.Abs(AtwTurnMath.Norm180(hdgNowDeg - back)) > BackDoneDeg;
        }

        public const double BackHoldCapS = 40, BackDoneDeg = 10;   // T-261
        public const double BackArcCapS = 120;   // T-265: the salvo arc check lasts until the ship is back on the evasion course, capped

        /// <summary>T-258: the heading the ship turns back to while FireBlocked; NaN otherwise.</summary>
        public double BackHdgDeg(int uid, double nowS)
        {
            Run r;
            return FireBlocked(uid, nowS) && _r.TryGetValue(uid, out r) ? r.FromHdgDeg : double.NaN;
        }

        /// <summary>T-261: same with the heading now and the evasion course; course ?? FromHdgDeg.</summary>
        public double BackHdgDeg(int uid, double nowS, double hdgNowDeg, double courseDeg)
        {
            return BackHdgDeg(uid, nowS, hdgNowDeg, courseDeg, BackHoldCapS);
        }

        /// <summary>T-265: as BackHdgDeg(uid, nowS, hdg, course) with the cap capS.</summary>
        public double BackHdgDeg(int uid, double nowS, double hdgNowDeg, double courseDeg, double capS)
        {
            Run r;
            if (!FireBlocked(uid, nowS, hdgNowDeg, courseDeg, capS) || !_r.TryGetValue(uid, out r)) return double.NaN;
            return double.IsNaN(courseDeg) ? r.FromHdgDeg : courseDeg;
        }

        /// <summary>T-248 (coordinator): below this time to impact a torpedo is never held back by the turn-back block.</summary>
        public const double BackUrgentTtiS = 30;

        /// <summary>T-248 (coordinator): does the turn-back block hold this torpedo? Not when a ready mount already has it in its arc, nor when it is close (tti below BackUrgentTtiS).</summary>
        public static bool BackBlocks(bool blocked, bool mountReadyInArc, double ttiS)
        {
            if (!blocked || mountReadyInArc) return false;
            return double.IsNaN(ttiS) || ttiS >= BackUrgentTtiS;
        }

        /// <summary>T-253: the turn-back block also holds a new turn onto the salvo. The torpedo of a turn is out of every arc, so only the tti bypass applies.</summary>
        public bool TurnHeld(int uid, double nowS, double ttiS) { return BackBlocks(FireBlocked(uid, nowS), false, ttiS); }

        /// <summary>T-261: the same with the heading now and the evasion course.</summary>
        public bool TurnHeld(int uid, double nowS, double ttiS, double hdgNowDeg, double courseDeg) { return BackBlocks(FireBlocked(uid, nowS, hdgNowDeg, courseDeg), false, ttiS); }

        public bool CanStart(int uid, double nowS)
        {
            Run r = Get(uid);
            return r.Phase == "idle" && nowS >= r.CooldownUntilS;
        }

        public void Start(int uid, int trackId, AtwTurnPlan p, double nowS)
        {
            Run r = Get(uid);
            r.BackUntilS = double.NegativeInfinity; r.Phase = "turning"; r.HeadingDeg = p.HeadingDeg; r.StartedS = nowS; r.PlannedS = p.TurnS; r.TrackId = trackId;
            r.BackSinceS = double.NaN; r.ArrivedS = double.NaN; r.LastLeftDeg = double.NaN; r.LastPollS = double.NaN; r.DeadlineS = nowS + AtwTurnMath.TurnTimeoutS(p.TurnS);
            r.FromHdgDeg = ((p.HeadingDeg - p.TurnDeg) % 360 + 360) % 360;
        }

        /// <summary>A salvo was ordered by this ship (any track): a turning ship goes on to wait for the launch.</summary>
        public bool NoteFire(int uid, int trackId, double nowS)
        {
            Run r = Get(uid);
            if (r.Phase != "turning") return false;
            r.Phase = "firing"; r.FiredS = nowS;
            return true;
        }

        /// <summary>The order of the ship was resolved (launched or failed). Returns the back reason, null when the ship was not turning.</summary>
        public string Result(int uid, bool launched, double nowS)
        {
            Run r = Get(uid);
            if (r.Phase == "idle") return null;
            string why = launched ? "launched" : "fire_failed";
            Back(r, why, nowS);
            return why;
        }

        /// <summary>Timers: null | "gone" (the torpedo is not a threat any more) | "timeout" (no fire after the turn) | "hold" (no result in HoldMaxS).</summary>
        public string Poll(int uid, double nowS, bool trackAlive) { return Poll(uid, nowS, trackAlive, true, double.NaN); }

        /// <summary>T-261: null | "gone" | "frozen" (!trackFresh) | "timeout" | "hold". The turn deadline moves out while the ship arrives / keeps turning, capped at TurnCapS.</summary>
        public string Poll(int uid, double nowS, bool trackAlive, bool trackFresh, double hdgNowDeg)
        {
            Run r;
            if (!_r.TryGetValue(uid, out r) || r.Phase == "idle") return null;
            string why = null;
            if (!trackAlive) why = "gone";
            else if (r.Phase == "turning" && !trackFresh) why = "frozen";
            else if (r.Phase == "turning")
            {
                double left = LeftOf(r, hdgNowDeg);
                if (!double.IsNaN(left))
                {
                    if (left <= AtwTurnMath.ArriveDeg && double.IsNaN(r.ArrivedS)) r.ArrivedS = nowS;
                    if (!double.IsNaN(r.ArrivedS)) r.DeadlineS = Math.Max(r.DeadlineS, r.ArrivedS + AtwTurnMath.ArriveWaitS);
                    else if (!double.IsNaN(r.LastLeftDeg) && nowS - r.LastPollS >= 0.5)
                    {
                        double dt = nowS - r.LastPollS, obs = (r.LastLeftDeg - left) / dt;
                        if (obs >= AtwTurnMath.MinObservedDegS) r.DeadlineS = Math.Max(r.DeadlineS, nowS + left / Math.Max(AtwTurnMath.MinRateDegS, obs) + AtwTurnMath.LagS);
                    }
                    if (double.IsNaN(r.LastPollS) || nowS - r.LastPollS >= 0.5) { r.LastLeftDeg = left; r.LastPollS = nowS; }
                }
                if (nowS >= Math.Min(r.DeadlineS, r.StartedS + AtwTurnMath.TurnCapS)) why = "timeout";
            }
            else if (r.Phase == "firing" && nowS - r.FiredS >= AtwTurnMath.HoldMaxS) why = "hold";
            if (why != null) Back(r, why, nowS);
            return why;
        }

        private static double LeftOf(Run r, double hdgNowDeg) { return double.IsNaN(hdgNowDeg) ? double.NaN : Math.Abs(AtwTurnMath.Norm180(r.HeadingDeg - hdgNowDeg)); }

        /// <summary>T-261 log: |Norm180(HeadingDeg - hdgNow)| of a turning ship, NaN otherwise.</summary>
        public double LeftDeg(int uid, double hdgNowDeg)
        {
            Run r;
            return _r.TryGetValue(uid, out r) && r.Phase != "idle" ? LeftOf(r, hdgNowDeg) : double.NaN;
        }

        /// <summary>T-261 log: the turning ship reached ArriveDeg of the turn heading once.</summary>
        public bool Arrived(int uid) { Run r; return _r.TryGetValue(uid, out r) && !double.IsNaN(r.ArrivedS); }

        /// <summary>The evasion ended, was released or the mission was left.</summary>
        public void End(int uid, string why)
        {
            Run r;
            if (_r.TryGetValue(uid, out r)) { r.Phase = "idle"; r.CooldownUntilS = double.NegativeInfinity; r.BackUntilS = double.NegativeInfinity; r.BackSinceS = double.NaN; }
        }

        private static void Back(Run r, string why, double nowS)
        {
            r.Phase = "idle";
            if (why != "gone") r.BackSinceS = nowS;
            if (why != "gone") r.BackUntilS = nowS + Math.Min(BackHoldMaxS, Math.Max(BackHoldMinS, r.PlannedS * 0.5));
            r.CooldownUntilS = why == "launched" ? double.NegativeInfinity : nowS + AtwTurnMath.CooldownS;
        }

        public void Clear() { _r.Clear(); }
    }
}
