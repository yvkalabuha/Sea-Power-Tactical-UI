using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-230 stand (ADR-0095): "turn onto the salvo". One ship (Udaloy RBU-6000 arcs -115..-20 and 20..115) runs from a pure-pursuit torpedo that sits
    /// astern, so no mount has it in the arc. Every second: AntiTorpedoMath.Allocate -> no_arc -> AtwTurnMath.Plan / AtwTurnState (the same code
    /// as the mod) -> heading override -> the salvo once the arc is entered -> back to the evasion course at the launch.
    /// </summary>
    internal static class AtwTurnSim
    {
        private const double TorpKn = 45, TrackId = 9;

        private sealed class Result
        {
            public int Starts, Fires, Backs, NoArc, Late, Dup, Timeouts, BlockedFar, FreeFar, FreeNear, ArcBlockedFar, ArcFreeFar, OldCapFreeFar;   // T-265: the same over the window BackArcCapS; // T-261: BlockedFar = ticks blocked while > 10 deg off the course; FreeFar/FreeNear the misses
            public string BackWhy;
            public double BackDoneS = double.NaN;   // T-265: seconds from the launch until the heading is within BackDoneDeg of the evasion course
            public double FireDist = double.NaN, PlanRate = double.NaN, FireRel = double.NaN, StartDist = double.NaN, FireT = double.NaN, EndHdg = double.NaN, MinDist = double.PositiveInfinity;
            public bool Hit, OverrideEnd;
        }

        private static Result Run(double dist0Nm, double rel0Deg, double rateDegS, double durationS)
        {
            return Run(dist0Nm, rel0Deg, 20, rateDegS, rateDegS, 0, 0, false, durationS);
        }

        /// <summary>shipKn, trueRateDegS (the ship), planRateDegS (what the plan assumes; NaN with useYaw = RateFor(peak), as the mod), startHdg, evasionCourse; the torpedo starts at the TRUE bearing rel0Deg when startHdg != 0 or relative to a north heading otherwise (same thing).</summary>
        private static bool _runOn;   // T-265: do not stop 40 s after the launch
        private static double _spikeFromS = double.NaN, _spikeToS = double.NaN, _spikeKn = double.NaN;   // T-261: a false closing measurement over [from, to]

        private static Result Run(double dist0Nm, double rel0Deg, double shipKn, double trueRateDegS, double planRateDegS, double startHdg, double evasionCourse, bool useYaw, double durationS)
        {
            var res = new Result();
            var arcs = new List<AtwArc> { new AtwArc(-115, -20), new AtwArc(20, 115) };
            var windows = new AtwWindows(); var mounts = new AtwMounts(); var closing = new AtwClosing(); var st = new AtwTurnState();
            double sx = 0, sy = 0, hdg = startHdg;
            var yaw = new AtwYawRate();
                        double tx = dist0Nm * Math.Sin((rel0Deg) * Math.PI / 180.0), ty = dist0Nm * Math.Cos((rel0Deg) * Math.PI / 180.0);
            AtwOrder open = null; double launchAtS = double.NaN;
            var lastReason = new Dictionary<long, string>();
            for (int t = 0; t <= (int)durationS; t++)
            {
                yaw.Update(1, hdg, t);
                double rateDegS = useYaw ? AtwTurnMath.RateFor(yaw.PeakDegS(1), false) : planRateDegS;
                double d = Math.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy));
                res.MinDist = Math.Min(res.MinDist, d);
                if (d < 0.03) { res.Hit = true; break; }
                closing.Update(1, (int)TrackId, d, t);
                bool spike = t >= _spikeFromS && t <= _spikeToS;
                double? cl = spike ? _spikeKn : closing.ClosingKn(1, (int)TrackId);
                double brg = EvasionMath.Norm360(Math.Atan2(tx - sx, ty - sy) * 180.0 / Math.PI);
                double rel = AtwTurnMath.Norm180(brg - hdg);

                // the launch of the order (2 s after it)
                if (open != null && t >= launchAtS)
                {
                    open = null;
                    string back = st.Result(1, true, t);
                    if (back != null) { res.Backs++; res.BackWhy = back; }
                }
                if (useYaw && !_runOn && res.Backs >= 1 && t >= launchAtS + 40) break;   // the salvo is out; no kill model here, the torpedo would go on to hit
                string pb = st.Poll(1, t, true, true, hdg);
                if (pb != null) { res.Backs++; res.BackWhy = pb; if (pb == "timeout") res.Timeouts++; }
                if (res.Backs >= 1 && st.PhaseOf(1) == "idle" && res.FireT + 2 + AtwTurnState.BackHoldCapS > t && t >= res.FireT + 2)   // T-261: the block follows the heading
                {
                    bool far = Math.Abs(AtwTurnMath.Norm180(hdg - evasionCourse)) > AtwTurnState.BackDoneDeg, blocked = st.FireBlocked(1, t, hdg, evasionCourse);
                    if (far && blocked) res.BlockedFar++;
                    if (far && !blocked) res.FreeFar++;
                    if (!far && !blocked) res.FreeNear++;
                }

                if (res.Backs >= 1 && st.PhaseOf(1) == "idle" && res.FireT + 2 + AtwTurnState.BackArcCapS > t && t >= res.FireT + 2)   // T-265: the salvo arc check lasts until the ship is back on the course
                {
                    bool far = Math.Abs(AtwTurnMath.Norm180(hdg - evasionCourse)) > AtwTurnState.BackDoneDeg, blocked = st.FireBlocked(1, t, hdg, evasionCourse, AtwTurnState.BackArcCapS);
                    if (far && blocked) res.ArcBlockedFar++;
                    if (far && !blocked) res.ArcFreeFar++;
                    if (far && !st.FireBlocked(1, t, hdg, evasionCourse)) res.OldCapFreeFar++;   // the 40 s cap of T-261: the proof of N1
                    if (!far && double.IsNaN(res.BackDoneS)) res.BackDoneS = t - res.FireT - 2;
                }

                var opt = new AtwOption { File = "rbu6000", Display = "rbu6000", Kill = 0.25, MinNm = 0.19, MaxNm = 3, SpeedKn = 600, Count = 96 };
                for (int k = 0; k < arcs.Count; k++)
                    if (AtwTurnMath.InGameArc(rel, arcs[k].X, arcs[k].Y)) opt.MountKeys.Add(k + 1);
                opt.MountReady = opt.MountKeys.Count > 0; opt.ArcBlocked = !opt.MountReady;
                var tr = new AtwTrackIn { TrackId = (int)TrackId, DistNm = d };
                tr.Cands.Add(new AtwThreatCand { Uid = 1, IsShooter = true, DistNm = d, ClosingKn = closing.ClosingKn(1, (int)TrackId), HeldTtiS = closing.HeldTtiS(1, (int)TrackId, t, d) });
                tr.Options.Add(opt);
                if (open != null) tr.EngagedBy = "own_order";
                var round = new AtwRoundIn { ShooterUid = 1, Role = "evade", NowS = t, ReactionS = 3, SafetyNm = 0.15, MaxTracks = 4, FireNm = 1.5 };
                round.Tracks.Add(tr);
                if (t < 4) round.Tracks.Clear();   // the closing history needs 3 s of samples, as in the game (the track is followed from 14 nm)
                if (useYaw && res.Backs >= 1) round.Tracks.Clear();   // the first cycle is what is checked; no kill model, so no second salvo
                foreach (AtwAssign a in AntiTorpedoMath.Allocate(round, windows, mounts))
                {
                    if (a.Fire)
                    {
                        if (open != null) res.Dup++;
                        windows.Note(a.TrackId, a.THitS);
                        mounts.Take(1, a.MountKey, a.Ammo.File, t + 5);
                        open = new AtwOrder { Uid = 1, TrackId = a.TrackId, File = a.Ammo.File, OrderedS = t }; launchAtS = t + 2;
                        res.Fires++; res.FireRel = rel; res.FireT = t; res.FireDist = d;
                        st.NoteFire(1, a.TrackId, t);
                        continue;
                    }
                    if (a.Reason != "no_arc") continue;
                    res.NoArc++;
                    string why = "turn_off";
                    if (open != null) why = "wait_result";
                    else if (!st.CanStart(1, t)) why = st.PhaseOf(1) == "idle" ? "cooldown" : "turning";
                    else if (AtwTurnMath.Gate(tr, windows, 0.15, a.THitS > 0 ? a.THitS : t + 10) != null) why = "gate";
                    else
                    {
                        var plan = AtwTurnMath.Plan(new AtwTurnIn { HdgDeg = hdg, BrgDeg = brg, DistNm = d, ClosingKn = cl ?? double.NaN, TtiS = a.TtiS, TurnRateDegS = rateDegS, MinNm = opt.MinNm, SpeedKn = opt.SpeedKn, ReactionS = 3, FireNm = 1.5, Arcs = arcs, ShipKn = AtwTurnMath.ShipKnFromClosing(cl ?? double.NaN, TorpKn) });
                        if (plan.Ok) { st.Start(1, a.TrackId, plan, t); res.Starts++; if (double.IsNaN(res.StartDist)) { res.StartDist = d; res.PlanRate = rateDegS; } why = "start"; }
                        else { why = plan.Why; if (plan.Why == "late") res.Late++; }
                    }
                }
                double? ov = st.Override(1);
                double cmd = ov ?? evasionCourse;
                double dh = AtwTurnMath.Norm180(cmd - hdg), step = Math.Min(Math.Abs(dh), trueRateDegS);
                hdg = EvasionMath.Norm360(hdg + Math.Sign(dh) * step);
                res.EndHdg = hdg; res.OverrideEnd = ov.HasValue;
                double hr = hdg * Math.PI / 180.0, vs = shipKn / 3600.0;
                sx += Math.Sin(hr) * vs; sy += Math.Cos(hr) * vs;
                double dd = Math.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy)), vt = TorpKn / 3600.0;
                if (dd > 1e-9) { tx += (sx - tx) / dd * vt; ty += (sy - ty) / dd * vt; }
            }
            return res;
        }

        public static string SelfTest()
        {
            // astern at 1.75 nm: no arc, no early turn, one turn, one salvo with the torpedo inside the arc, back to the evasion course at the launch
            Result r = Run(1.75, 180, 4, 36);
            if (r.Starts != 1 || r.Fires != 1 || r.Backs != 1 || r.BackWhy != "launched" || r.Dup != 0) return "turn stern: start/fire/back = " + r.Starts + "/" + r.Fires + "/" + r.Backs + " why=" + r.BackWhy + " dup=" + r.Dup;
            if (r.NoArc <= 0) return "turn stern: no_arc expected before the turn";
            if (!(r.StartDist <= 1.75 && r.StartDist >= 1.45)) return "turn stern: the turn starts with the pre-warning, dist=" + r.StartDist;
            if (!(Math.Abs(r.FireRel) > 20 && Math.Abs(r.FireRel) < 115)) return "turn stern: fired outside the arc, rel=" + r.FireRel;
            if (r.OverrideEnd || Math.Abs(AtwTurnMath.Norm180(r.EndHdg)) > 80) return "turn stern: the override is released and the ship comes back to the evasion course, hdg=" + r.EndHdg;
            Console.WriteLine("atw turn stern start=" + r.Starts + " fire=" + r.Fires + " back=" + r.Backs + " dup=" + r.Dup + " start_nm=" + Math.Round(r.StartDist, 2) + " fire_rel=" + Math.Round(r.FireRel));
            // T-261: stern pursuit at 1.8 nm, own 25 kn (closing ~20): no turn beyond FireNm, no timeout, one salvo
            r = Run(1.8, 180, 25, 4, 4, 0, 0, false, 60);
            if (r.Starts != 1 || r.Fires < 1 || r.Timeouts != 0) return "t261 stern 25 kn: start/fire/timeout/start_nm = " + r.Starts + "/" + r.Fires + "/" + r.Timeouts + "/" + r.StartDist;
            Console.WriteLine("atw turn t261 stern25 start_nm=" + Math.Round(r.StartDist, 2) + " fire=" + r.Fires + " timeout=" + r.Timeouts);
            // T-261: a false closing jump of 120 kn at 1.77 nm: no start on it
            _spikeFromS = 0; _spikeToS = 12; _spikeKn = 120;
            r = Run(1.95, 180, 25, 4, 4, 0, 0, false, 8);
            _spikeFromS = _spikeToS = _spikeKn = double.NaN;
            if (r.Starts != 0 || r.Late != 0) return "t261 false closing: start=" + r.Starts + " late=" + r.Late;
            Console.WriteLine("atw turn t261 false_closing start=0");
            // T-261 (N2): after the launch the block holds while the ship is more than 10 deg off the evasion course (80), and only then
            r = Run(3.73, 170, 30, 1, double.NaN, 145, 20, true, 900);
            if (r.Backs != 1 || r.BlockedFar <= 0 || r.FreeFar != 0 || r.FreeNear != 0) return "t261 N2: back/blocked_far/free_far/free_near = " + r.Backs + "/" + r.BlockedFar + "/" + r.FreeFar + "/" + r.FreeNear;
            Console.WriteLine("atw turn t261 n2 blocked_far=" + r.BlockedFar);
            // T-265 (N2 again): the same check over the whole way back
            _runOn = true;
            r = Run(3.73, 170, 30, 1, double.NaN, 145, 20, true, 900);
            if (r.Backs != 1 || r.ArcFreeFar != 0 || r.ArcBlockedFar <= 0 || !(r.BackDoneS > AtwTurnState.BackHoldCapS)) return "t265 N2: back/arc_free_far/arc_blocked_far/back_s = " + r.Backs + "/" + r.ArcFreeFar + "/" + r.ArcBlockedFar + "/" + r.BackDoneS;
            Console.WriteLine("atw turn t265 n2 back_s=" + r.BackDoneS + " arc_blocked_far=" + r.ArcBlockedFar + " arc_free_far=" + r.ArcFreeFar + " free_far=" + r.FreeFar);
            // T-265 (N1): slow turn back (1.1 deg/s), the way back takes more than 40 s
            r = Run(3.73, 170, 30, 1.1, double.NaN, 20, 20, true, 900);
            _runOn = false;
            if (r.Backs != 1 || r.OldCapFreeFar <= 0 || r.ArcFreeFar != 0 || r.ArcBlockedFar <= 0 || !(r.BackDoneS > AtwTurnState.BackHoldCapS)) return "t265 N1: back/old_cap_free_far/arc_free_far/arc_blocked_far/back_s = " + r.Backs + "/" + r.OldCapFreeFar + "/" + r.ArcFreeFar + "/" + r.ArcBlockedFar + "/" + r.BackDoneS;
            Console.WriteLine("atw turn t265 n1 old_cap_free_far=" + r.OldCapFreeFar + " back_s=" + r.BackDoneS + " arc_blocked_far=" + r.ArcBlockedFar + " arc_free_far=" + r.ArcFreeFar + " free_far=" + r.FreeFar + " backs=" + r.Backs);
            // too close astern (0.35 nm): not in time, no turn, no salvo
            r = Run(0.35, 180, 4, 10);
            if (r.Starts != 0 || r.Fires != 0 || r.Late <= 0 || r.NoArc <= 0) return "turn late: start=" + r.Starts + " fire=" + r.Fires + " late=" + r.Late;
            Console.WriteLine("atw turn late start=0 fire=0 late>0");
            // slow turning ship (0.5 deg/s): 75 degrees take 152 s, more than MaxTurnS
            r = Run(1.5, 180, 0.5, 10);
            if (r.Starts != 0 || r.Fires != 0 || r.Late <= 0) return "turn slow: start=" + r.Starts + " fire=" + r.Fires + " late=" + r.Late;
            Console.WriteLine("atw turn slow start=0 fire=0 late>0");
            // abeam inside the arc: the salvo goes without a turn
            r = Run(1.4, 60, 4, 6);
            if (r.Starts != 0 || r.Fires < 1) return "turn beam: start=" + r.Starts + " fire=" + r.Fires;
            Console.WriteLine("atw turn beam start=0 fire>=1");
            // T-237: the run 0.30.12 (Udaloy 30 kn, 145 -> evasion course 20, true rate 1 deg/s, torpedo 45 kn at 170, 3.73 nm): turn start -> fire -> back
            r = Run(3.73, 170, 30, 1, double.NaN, 145, 20, true, 900);
            string err = CheckLive("live12", r);
            if (err != null) return err;
            // the same on the evasion course from the start, the torpedo at the relative bearing 150 (no measured turn rate yet: the default is used)
            r = Run(3.73, 170, 30, 1, double.NaN, 20, 20, true, 900);
            err = CheckLive("live12_rel150", r);
            if (err != null) return err;
            // T-237 (C2): the game flag at 7.03 nm (the 0.30.12 start, closing ~63 kn)
            var far = new EvTorp { TrackId = 1, DistNm = 7.03, ClosingKn = 63 };
            if (!EvasionMath.GameFlagStarts(far) || !EvasionMath.ShouldStart(true, far)) return "start7: closing 63 kn must start";
            far.ClosingKn = null;
            if (EvasionMath.GameFlagStarts(far)) return "start7: unknown closing must not start";
            far.ClosingKn = 4.9;
            if (EvasionMath.GameFlagStarts(far)) return "start7: closing 4.9 kn must not start";
            Console.WriteLine("atw t237 start7 ok");
            return null;
        }

        private static string CheckLive(string name, Result r)
        {
            if (r.Starts != 1 || r.Fires != 1 || r.Backs != 1 || r.BackWhy != "launched" || r.Dup != 0) return name + ": start/fire/back = " + r.Starts + "/" + r.Fires + "/" + r.Backs + " why=" + r.BackWhy + " dup=" + r.Dup + " no_arc=" + r.NoArc + " late=" + r.Late + " start_nm=" + r.StartDist + " hit=" + r.Hit;
            if (!(r.StartDist >= 1.8 && r.StartDist <= 2.6)) return name + ": start dist " + r.StartDist;
            if (!(r.FireDist >= 1.1 && r.FireDist <= 1.5)) return name + ": fire dist " + r.FireDist;
            if (!(Math.Abs(r.FireRel) > 20 && Math.Abs(r.FireRel) < 115)) return name + ": fired outside the arc, rel=" + r.FireRel;
            if (r.OverrideEnd || Math.Abs(AtwTurnMath.Norm180(r.EndHdg - 20)) > 80) return name + ": no return to the evasion course, hdg=" + r.EndHdg;
            Console.WriteLine("atw turn " + name + " start=" + r.Starts + " fire=" + r.Fires + " back=" + r.Backs + " dup=" + r.Dup + " start_nm=" + Math.Round(r.StartDist, 2) + " fire_nm=" + Math.Round(r.FireDist, 2) + " rate=" + Math.Round(r.PlanRate, 2));
            return null;
        }
    }
}
