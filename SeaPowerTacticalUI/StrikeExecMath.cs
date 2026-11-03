using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class StrikeExecInput
    {
        public double Now;                        // game seconds
        public StrikeAircraft A;                  // null = the aircraft is gone
        public int AmmoNow;                       // the missiles of the row's ammunition left on the aircraft
        public StrikeShip Target;                 // null = the track is gone
        public double FixT = -1;                  // the mission time of the last live fix of the target (-1 = unknown)
        public bool Live = true;                  // the target is live now
        public bool TargetExcluded;
        public bool ClassChanged;                 // T-342 C: the class of the target is not the one of the plan -> skipped class_changed
        public Func<bool> OverSea;                // evaluated only when an order would be given (the terrain read is not free); null = sea
        public double TofNowS;                    // the time of flight from the aircraft position now (StrikeTofCache.TickTof)
        public bool? ActiveTask;                  // phase D (review of C): the game still holds an attack task of the aircraft on this target (null = not readable)
        public double DistNm = -1, BearingDeg = -1;   // from the aircraft to the lead position of the target; -1 = computed by Step from A and Target
    }

    internal struct StrikeExecAction
    {
        public string Kind;   // none | goto_gate | orbit_gate | run_in | issue | hold | skip | fail | launched
        public GeoPt Pt;
        public double AltFt;
        public double Kn;       // T-342 run_in: the speed to set (0 = do not change)
        public string Why;
        public static StrikeExecAction None { get { return new StrikeExecAction { Kind = "none" }; } }
    }

    // T-341 phase C (ADR-0132 sections 5-6, plan 24.2): the state machine of one execution row. Pure: the clock is the game time of the input, nothing else.
    // States: to_gate | at_gate | run_in | held | issued | launched | skipped | cancelled | failed. The caller applies the returned action and tells Step nothing back
    // (a failed order is reported by the caller through r.Sent = null / r.NextTryT / r.State = held).
    internal static class StrikeExecMath
    {
        /// <summary>T-353: the time after which the result is read even with missiles in flight: the latest TArrive + max(0, LateS) over the rows (a row launched LateS late arrives that much later than the plan). No rows = now.</summary>
        public static double ResultDeadlineT(IList<StrikeExecRow> rows, double now)
        {
            if (rows == null || rows.Count == 0) return now;
            double t = double.NegativeInfinity;
            foreach (var r in rows) t = Math.Max(t, r.TArrive + (double.IsNaN(r.LateS) ? 0 : Math.Max(0, r.LateS)));
            return t;
        }

        public static bool Terminal(string state) { return state == "launched" || state == "skipped" || state == "cancelled" || state == "failed"; }

        /// <summary>The row of the aircraft that is active now: the smallest Order among the not finished ones; -1 = all finished.</summary>
        public static int ActiveIndex(IList<StrikeExecRow> rows)
        {
            int best = -1;
            if (rows == null) return best;
            for (int i = 0; i < rows.Count; i++)
                if (!Terminal(rows[i].State) && (best < 0 || rows[i].Order < rows[best].Order)) best = i;
            return best;
        }

        private const double DescentArriveNm = 3;

        private static StrikeExecAction Act(string kind, string why = null) { return new StrikeExecAction { Kind = kind, Why = why }; }

        private static void Move(StrikeExecRow r, string to) { r.State = to; }

        public static StrikeExecAction Step(StrikeExecRow r, StrikeExecInput x, StrikeParams p)
        {
            if (Terminal(r.State)) return StrikeExecAction.None;
            double now = x.Now;
            if (x.A == null) { Move(r, "failed"); r.Why = "lost"; return Act("fail", "lost"); }
            // the order is out: wait for the missile to leave
            if (r.State == "issued")
            {
                if (x.AmmoNow < r.AmmoBefore)
                {
                    r.LaunchedN = r.AmmoBefore - x.AmmoNow; r.LaunchedT = now; r.DtOrderS = now - r.IssuedT;
                    Move(r, "launched");
                    return Act("launched");
                }
                if (now - r.IssuedT > p.LaunchConfirmS)
                {
                    if (x.ActiveTask == true)
                    {
                        // the game still works on the order (a slow turn): no second order until twice the confirm time
                        if (now - r.IssuedT <= 2 * p.LaunchConfirmS) return StrikeExecAction.None;
                        Move(r, "failed"); r.Why = "no_launch"; return Act("fail", "no_launch");
                    }
                    if (r.Tries < 2) { r.Tries++; r.State = "run_in"; r.NextTryT = now; r.Why = "retry"; }
                    else { Move(r, "failed"); r.Why = "no_launch"; return Act("fail", "no_launch"); }
                }
                else return StrikeExecAction.None;
            }
            if (x.Target == null) { Move(r, "skipped"); r.Why = "target_gone"; return Act("skip", "target_gone"); }
            if (x.TargetExcluded) { Move(r, "skipped"); r.Why = "excluded"; return Act("skip", "excluded"); }
            if (x.ClassChanged) { Move(r, "skipped"); r.Why = "class_changed"; return Act("skip", "class_changed"); }
            var apos = new GeoPt(x.A.Lat, x.A.Lon);
            double dist = x.DistNm >= 0 ? x.DistNm : AswMath.DistNm(apos, new GeoPt(x.Target.Lat, x.Target.Lon));
            double brg = x.BearingDeg >= 0 ? x.BearingDeg : AswMath.BearingDeg(apos, new GeoPt(x.Target.Lat, x.Target.Lon));
            // too late for any launch: the earliest arrival (fly into the range window, turn, the flight from the edge of it) is later than T + LateSkipS
            double rIn = r.LaunchNm > 0 ? Math.Min(r.MaxNm, r.LaunchNm + 2) : 0.95 * r.MaxNm;
            if (dist > rIn)
            {
                double rough = StrikeTotMath.RoughTofS(dist, r.VmaxKn);
                double scale = rough > 0 && x.TofNowS > 0 ? x.TofNowS / rough : 1;
                double kn = x.A.SpeedKn > 0 ? x.A.SpeedKn : 450;
                double earliest = now + (dist - rIn) / kn * 3600.0 + p.TurnBaseS + StrikeTotMath.RoughTofS(rIn, r.VmaxKn) * scale;
                if (earliest > r.TArrive + p.LateSkipS) { Move(r, "skipped"); r.Why = "late_window"; return Act("skip", "late_window"); }
            }
            if (r.State == "held" && now > r.TArrive + p.TotToleranceS) { Move(r, "skipped"); r.Why = "late_window"; return Act("skip", "late_window"); }
            // when to leave: the time the aircraft needs for the rest of the way (through the gate while it has not reached it) before the planned launch
            double ownKn = x.A.SpeedKn > 0 ? x.A.SpeedKn : 450;
            // E1b: a heavy carrier (Tu-22M2: 4.3 deg/s, a circle of 2 nm) first turns onto the launch point: the turn costs up to 43 s over the straight way, so it leaves that much earlier
            double rate = StrikeCalib.TurnRateDps(x.A.Type, ownKn);
            double arcS = rate > 0 && x.A.HeadingDeg >= 0 ? StrikeTotMath.ArcExtraS(AswMath.DistNm(apos, r.Launch), StrikeTotMath.SignedDeltaDeg(x.A.HeadingDeg, AswMath.BearingDeg(apos, r.Launch)), ownKn, rate) : 0;
            // T-342: from the gate to the launch point the aircraft flies at the planned speed (the mod sets the throttle at the run in), not at the loiter speed left by the orbit of the gate
            double legKn = r.LaunchKn > 0 && r.SpeedSent != "failed" ? r.LaunchKn : ownKn;
            double goGate = r.TLaunch - AswMath.DistNm(apos, r.Gate) / ownKn * 3600.0 - AswMath.DistNm(r.Gate, r.Launch) / legKn * 3600.0 - arcS;   // to_gate: the departure from here through the gate
            double goNow = r.TLaunch - AswMath.DistNm(apos, r.Launch) / legKn * 3600.0 - arcS;                                                       // at the gate (orbiting within a few miles of it): straight to the launch point
            // the route phases
            if (r.State == "to_gate")
            {
                var gate = r.Gate;
                if (r.Sent == null && now >= r.NextTryT)
                {
                    r.Sent = r.DescentAtStart ? "gate_lo" : "gate_hi";   // the first route order (or the retry of a refused one): through the descent point on the cruise altitude, or at once low
                    return new StrikeExecAction { Kind = "goto_gate", Pt = gate, AltFt = r.DescentAtStart ? r.LaunchAltFt : 0 };
                }
                if (!r.DescentAtStart && r.Sent == "gate_hi" && AswMath.DistNm(apos, r.Descent) <= DescentArriveNm && now >= r.NextTryT)
                {
                    r.Sent = "gate_lo";
                    return new StrikeExecAction { Kind = "goto_gate", Pt = gate, AltFt = r.LaunchAltFt };
                }
                if (AswMath.DistNm(apos, gate) <= p.WaitArriveNm) { r.GateT = now; Move(r, "at_gate"); }
                else if (now >= goGate - p.RunInReserveS) { r.LateS = Math.Max(0, now - goGate); Move(r, "run_in"); }
            }
            if (r.State == "at_gate")
            {
                if (now >= goNow - p.RunInReserveS) { r.LateS = Math.Max(0, now - goNow); Move(r, "run_in"); }
                else if (r.Sent != "orbit_gate" && now >= r.NextTryT)
                {
                    r.Sent = "orbit_gate";
                    return new StrikeExecAction { Kind = "orbit_gate", Pt = r.Gate, AltFt = r.LaunchAltFt };
                }
            }
            if (r.State == "run_in" || r.State == "held")
            {
                if (r.Sent != "run_in" && now >= r.NextTryT && r.State == "run_in")
                {
                    r.Sent = "run_in";
                    return new StrikeExecAction { Kind = "run_in", Pt = r.Launch, AltFt = r.LaunchAltFt, Kn = r.LaunchKn };
                }
            }
            if (r.State == "held" && r.NextTryT > now && r.Why != "contact_lost" && r.Why != "land") return StrikeExecAction.None;   // the retry of a refused order
            // the order condition
            double turn = StrikeTotMath.TurnLeadS(StrikeTotMath.DeltaPsi(x.A.HeadingDeg, brg), p, rate);
            if (!StrikeTotMath.IssueNow(now, turn, x.TofNowS, r.TArrive, dist, r.MinNm, r.LaunchNm > 0 ? Math.Min(r.MaxNm, r.LaunchNm + 2) : r.MaxNm))
            {
                if (r.State == "held") { r.State = "run_in"; r.Why = null; r.HeldSince = -1; }
                return StrikeExecAction.None;
            }
            // the contact must be there
            bool held = AswStrikeMath.HeldLive(x.Live, x.FixT, now, p.HoldS);
            if (!held) { Move(r, "skipped"); r.Why = "hold_timeout"; return Act("skip", "hold_timeout"); }
            if (!x.Live)
            {
                if (r.State != "held" || r.Why != "contact_lost") { r.HeldSince = now; r.Why = "contact_lost"; }
                Move(r, "held");
                return Act("hold", "contact_lost");
            }
            // over the sea only
            if (x.OverSea != null && !x.OverSea())
            {
                if (now > r.TArrive + p.TotToleranceS) { Move(r, "skipped"); r.Why = "late_window"; return Act("skip", "late_window"); }
                if (r.State != "held" || r.Why != "land") r.HeldSince = now;
                r.Why = "land"; Move(r, "held");
                return Act("hold", "land");
            }
            r.TurnS = turn;
            r.LateS = Math.Max(r.LateS, Math.Max(0, now + turn + x.TofNowS - r.TArrive));
            r.IssuedT = now; r.AmmoBefore = x.AmmoNow; r.Why = null; r.HeldSince = -1;
            Move(r, "issued");
            return Act("issue");
        }

        // ---- self test ----
        // The world of the table: the target at 41N 10E, the launch point 58.5 nm south of it (0.9 of 65 nm), the gate 15 nm behind it, the descent point 100 nm behind the gate.
        private static readonly GeoPt Tgt = new GeoPt(41.0, 10.0);
        private static GeoPt At(double nmFromTarget) { return AswMath.Offset(Tgt, 180, nmFromTarget); }

        private static StrikeExecRow Row(double tgo = 979, double tarr = 1500)
        {
            return new StrikeExecRow
            {
                Uid = 7, TargetId = 100, Ammo = "h", Salvo = 1, Launch = At(58.5), Gate = At(73.5), Descent = At(173.5), LaunchAltFt = 200, CruiseAltFt = 25000,
                MinNm = 3.5, MaxNm = 65, LaunchNm = 58.5, VmaxKn = 540, TGo = tgo, TLaunch = tarr - 401, TArrive = tarr, State = "to_gate", DescentAtStart = true,
            };
        }

        private static StrikeExecInput In(double now, double nmFromTarget, double heading = 0, int ammo = 4, bool live = true)
        {
            var tg = new StrikeShip { TrackId = 100, Lat = Tgt.Lat, Lon = Tgt.Lon, FixAgeS = live ? 0 : 20 };
            var p = At(nmFromTarget);
            var a = new StrikeAircraft { Uid = 7, Lat = p.Lat, Lon = p.Lon, HeadingDeg = heading };
            return new StrikeExecInput { Now = now, A = a, AmmoNow = ammo, Target = tg, Live = live, FixT = live ? now : now - 20, TofNowS = 0 };
        }

        private static StrikeExecInput Tof(StrikeExecInput x, double tof) { x.TofNowS = tof; return x; }

        /// <summary>X1: a target whose class changed between the plan and the order is never fired at.</summary>
        private static string SelfTestClass()
        {
            var p = new StrikeParams();
            var r = Row(); r.State = "run_in"; r.Sent = "run_in";
            var x = Tof(In(1500, 58.5), 400);
            x.ClassChanged = true;
            var act = Step(r, x, p);
            if (act.Kind != "skip" || act.Why != "class_changed" || r.State != "skipped" || r.Why != "class_changed") return "class changed: " + act.Kind + " " + r.State;
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = Tof(In(1500, 58.5), 400);
            act = Step(r, x, p);
            if (act.Kind == "skip") return "regression: without the flag the row is not skipped " + act.Why;
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = Tof(In(1500, 58.5), 400); x.ClassChanged = true; x.TargetExcluded = true;
            act = Step(r, x, p);
            if (act.Why != "excluded") return "excluded first";
            return null;
        }

        public static string SelfTest(string part = "all")
        {
            if (part == "class" || part == "all") { string ce = SelfTestClass(); if (ce != null || part == "class") return ce; }
            var p = new StrikeParams();
            // T-353: the deadline of the result follows the real lateness of the launch
            {
                var dr = new List<StrikeExecRow> { new StrikeExecRow { TArrive = 945.3, LateS = 137.8 }, new StrikeExecRow { TArrive = 945.3, LateS = 144 }, new StrikeExecRow { TArrive = 900, LateS = -5 } };
                if (Math.Abs(ResultDeadlineT(dr, 1000) - 1089.3) > 1e-6) return "result deadline " + ResultDeadlineT(dr, 1000);
                dr = new List<StrikeExecRow> { new StrikeExecRow { TArrive = 945.3 }, new StrikeExecRow { TArrive = 900 } };
                if (Math.Abs(ResultDeadlineT(dr, 1000) - 945.3) > 1e-6) return "result deadline without lateness";
                if (ResultDeadlineT(new List<StrikeExecRow>(), 777) != 777 || ResultDeadlineT(null, 5) != 5) return "result deadline of no rows";
            }
            // m: ActiveIndex
            var rows = new List<StrikeExecRow> { new StrikeExecRow { Order = 1 }, new StrikeExecRow { Order = 0, State = "launched" }, new StrikeExecRow { Order = 2 } };
            if (ActiveIndex(rows) != 0) return "active index " + ActiveIndex(rows);
            rows[0].State = "skipped"; if (ActiveIndex(rows) != 2) return "active index skips";
            rows[2].State = "failed"; if (ActiveIndex(rows) != -1 || ActiveIndex(null) != -1) return "active index done";
            if (!Terminal("launched") || !Terminal("cancelled") || Terminal("issued") || Terminal("held") || Terminal("to_gate")) return "terminal";
            // a0: the first route order: low at once (descent at the start) or on the cruise altitude through the descent point
            var r = Row(tarr: 2500);
            r.DescentAtStart = true;
            var act0 = Step(r, In(900, 180), p);
            if (act0.Kind != "goto_gate" || act0.AltFt != 200 || r.Sent != "gate_lo") return "first order low " + act0.Kind;
            if (Step(r, In(901, 180), p).Kind != "none") return "first order once";
            r = Row(tarr: 4000); r.DescentAtStart = false;
            act0 = Step(r, In(900, 250), p);
            if (act0.Kind != "goto_gate" || act0.AltFt != 0 || r.Sent != "gate_hi") return "first order high " + act0.Kind;
            r.Sent = null; r.NextTryT = 905;
            if (Step(r, In(903, 250), p).Kind != "none" || Step(r, In(905, 250), p).Kind != "goto_gate") return "the retry of a refused order";
            // a: early at the gate -> orbit once
            r = Row(); r.DescentAtStart = true; r.Sent = "gate_lo";
            var act = Step(r, In(900, 73.5), p);
            if (r.State != "at_gate" || act.Kind != "orbit_gate" || r.Sent != "orbit_gate") return "orbit at the gate " + r.State + " " + act.Kind;
            if (Step(r, In(910, 73.5), p).Kind != "none") return "orbit once";
            // b: now >= TGo -> run_in on the launch altitude
            act = Step(r, In(979, 73.5), p);
            if (r.State != "run_in" || act.Kind != "run_in" || act.AltFt != 200 || r.LateS != 0) return "run in " + r.State + " " + act.Kind;
            if (Step(r, In(980, 70), p).Kind != "none") return "run in once";
            // b2: the reserve: the run in starts RunInReserveS (15 s) before the time that the straight way needs
            r = Row(); r.State = "at_gate"; r.Sent = "orbit_gate";
            if (Step(r, In(963, 73.5), p).Kind != "none" || r.State != "at_gate") return "the run in starts at 964, not at 963";
            act = Step(r, In(964, 73.5), p);
            if (act.Kind != "run_in" || r.State != "run_in" || r.LateS != 0) return "the run in starts at 964 " + act.Kind + " " + r.LateS;
            // c: late to the gate: straight to run_in with the lateness
            r = Row(); r.Sent = "gate_lo";
            act = Step(r, Tof(In(1000, 80), 450), p);
            if (r.State != "run_in" || act.Kind != "run_in" || Math.Abs(r.LateS - 73) > 1e-6) return "late to the gate " + r.State + " " + act.Kind + " " + r.LateS;
            // c2: the descent point: the higher route ends there
            r = Row(tarr: 2500); r.DescentAtStart = false; r.Sent = "gate_hi";
            act = Step(r, In(900, 173.0), p);
            if (act.Kind != "goto_gate" || act.AltFt != 200 || r.Sent != "gate_lo") return "descent " + act.Kind;
            // d: the order condition with the turn: dpsi 0 -> TurnBaseS 2.5 + the reserve 2 = 4.5 s; TArrive 1500, tof 400 -> issue at 1095.5, the tick 1096
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            if (Step(r, Tof(In(1095, 60), 400), p).Kind != "none") return "one second before the threshold";
            act = Step(r, Tof(In(1096, 60), 400), p);
            if (act.Kind != "issue" || r.State != "issued" || r.IssuedT != 1096 || r.AmmoBefore != 4 || Math.Abs(r.TurnS - 4.5) > 1e-9) return "issue on the threshold " + act.Kind + " " + r.TurnS;
            // d2: a turn of 180 degrees is ordered earlier: 2.5 + 180 / 11.5 + 2 = 20.15 s, the threshold 1079.85
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            if (Step(r, Tof(In(1079, 60, 180), 400), p).Kind != "none") return "a turn of 180 too early";
            if (Step(r, Tof(In(1080, 60, 180), 400), p).Kind != "issue" || Math.Abs(r.TurnS - (2.5 + 180 / 11.5 + 2)) > 1e-9) return "a turn of 180 on the threshold";
            // e: out of the range window
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            if (Step(r, Tof(In(300, 180), 400), p).Kind != "none") return "out of range";
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            if (Step(r, Tof(In(1400, 0.6), 400), p).Kind != "none") return "inside the minimum range";
            // f: contact lost 30 s -> held; 61 s -> skipped
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            var x = Tof(In(1200, 60, 0, 4, false), 400); x.FixT = 1170;
            act = Step(r, x, p);
            if (act.Kind != "hold" || r.State != "held" || r.Why != "contact_lost") return "contact lost " + act.Kind;
            x = Tof(In(1210, 60, 0, 4, false), 400); x.FixT = 1170;
            if (Step(r, x, p).Kind != "hold") return "still held";
            x = Tof(In(1231, 60, 0, 4, false), 400); x.FixT = 1170;
            act = Step(r, x, p);
            if (act.Kind != "skip" || r.State != "skipped" || r.Why != "hold_timeout") return "hold timeout " + act.Kind + " " + r.State;
            // f2: the contact is back after 20 s -> the order goes
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = Tof(In(1200, 60, 0, 4, false), 400); x.FixT = 1180; Step(r, x, p);
            if (Step(r, Tof(In(1205, 60), 400), p).Kind != "issue") return "the contact came back";
            // g: excluded / gone
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = In(1200, 60); x.TargetExcluded = true;
            if (Step(r, x, p).Kind != "skip" || r.Why != "excluded") return "excluded";
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = In(1200, 60); x.Target = null;
            if (Step(r, x, p).Kind != "skip" || r.Why != "target_gone") return "target gone";
            // h: land at the launch point: held, then late_window after T + tolerance
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = Tof(In(1100, 60), 400); x.OverSea = () => false;
            act = Step(r, x, p);
            if (act.Kind != "hold" || r.Why != "land" || r.State != "held") return "land " + act.Kind;
            x = Tof(In(1505, 60), 400); x.OverSea = () => false;
            if (Step(r, x, p).Kind != "hold") return "land inside T + tolerance";
            x = Tof(In(1511, 60), 400); x.OverSea = () => false;
            act = Step(r, x, p);
            if (act.Kind != "skip" || r.Why != "late_window") return "land late " + act.Kind + " " + r.Why;
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            int seaCalls = 0; x = Tof(In(1100, 60), 400); x.OverSea = () => { seaCalls++; return true; };
            var early = Tof(In(1000, 60), 400); early.OverSea = x.OverSea;
            Step(r, early, p);
            if (seaCalls != 0) return "no sea call before the order";
            Step(r, x, p);
            if (seaCalls != 1 || r.State != "issued") return "sea call once on the order";
            // h2: a refused order (held with a code) is skipped after T + tolerance
            r = Row(); r.State = "held"; r.Why = "neutral_near"; r.Sent = "run_in"; r.NextTryT = 1520;
            act = Step(r, Tof(In(1511, 60), 400), p);
            if (act.Kind != "skip" || r.Why != "late_window") return "held refused order late " + act.Kind;
            r = Row(); r.State = "held"; r.Why = "neutral_near"; r.Sent = "run_in"; r.NextTryT = 1100;
            if (Step(r, Tof(In(1099, 60), 400), p).Kind != "none") return "a refused order waits for the retry time";
            // i: issued -> launched with the fact
            r = Row(); r.State = "issued"; r.IssuedT = 1100; r.AmmoBefore = 4;
            if (Step(r, In(1105, 60, 0, 4), p).Kind != "none") return "issued, no drop yet";
            act = Step(r, In(1109, 60, 0, 3), p);
            if (act.Kind != "launched" || r.State != "launched" || r.LaunchedN != 1 || Math.Abs(r.DtOrderS - 9) > 1e-9) return "launched " + act.Kind;
            if (Step(r, In(1200, 60, 0, 3), p).Kind != "none") return "terminal rows do nothing";
            // j: no drop for 30 s: retry twice, then fail
            r = Row(); r.State = "issued"; r.IssuedT = 1100; r.AmmoBefore = 4; r.Sent = "run_in";
            act = Step(r, Tof(In(1146, 60, 0, 4), 400), p);
            if (act.Kind != "issue" || r.Tries != 1 || r.State != "issued") return "retry 1 " + act.Kind + " " + r.State;
            act = Step(r, Tof(In(1192, 60, 0, 4), 400), p);
            if (act.Kind != "issue" || r.Tries != 2) return "retry 2 " + act.Kind;
            act = Step(r, Tof(In(1238, 60, 0, 4), 400), p);
            if (act.Kind != "fail" || r.State != "failed" || r.Why != "no_launch") return "fail no_launch " + act.Kind;
            // j2: the order is not repeated while the game holds an attack task (until twice the confirm time), then no_launch
            r = Row(); r.State = "issued"; r.IssuedT = 1100; r.AmmoBefore = 4; r.Sent = "run_in";
            x = In(1140, 60, 0, 4); x.ActiveTask = true;
            if (Step(r, x, p).Kind != "none") return "no repeat before the confirm time";
            x = In(1150, 60, 0, 4); x.ActiveTask = true;
            if (Step(r, x, p).Kind != "none" || r.State != "issued") return "an active task is waited for";
            x = In(1192, 60, 0, 4); x.ActiveTask = true;
            act = Step(r, x, p);
            if (act.Kind != "fail" || r.Why != "no_launch") return "no_launch after twice the confirm time " + act.Kind;
            r = Row(); r.State = "issued"; r.IssuedT = 1100; r.AmmoBefore = 4; r.Sent = "run_in";
            x = In(1140, 60, 0, 4); x.ActiveTask = false;
            if (Step(r, x, p).Kind != "none") return "repeat only after the confirm time";
            x = Tof(In(1146, 60, 0, 4), 400); x.ActiveTask = false;
            if (Step(r, x, p).Kind != "issue") return "no active task: the order is repeated";
            // k: the aircraft is gone
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = In(1100, 60); x.A = null;
            if (Step(r, x, p).Kind != "fail" || r.Why != "lost") return "lost";
            // l: the arrival is out of reach: the earliest one is later than T + LateSkipS
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            act = Step(r, Tof(In(1500, 180), 400), p);
            if (act.Kind != "skip" || r.Why != "late_window") return "late window " + act.Kind;
            // l2: late but reachable (60 s late at the gate): goes on
            r = Row(); r.Sent = "gate_lo";
            act = Step(r, Tof(In(1040, 73.5), 502), p);
            if (act.Kind == "skip") return "late by a minute is not skipped: " + r.Why;
            // m: E1b, the Tu-22M2 (4.3 deg/s at 537 kn): the run in starts earlier by the turn onto the launch point (up to 43 s at 180 degrees), an agile type does not.
            // The gate is 73.5 nm south of the target, the launch point 15 nm north of the gate: heading 0 points at it, heading 180 points away; the straight time is 964 (reserve included)
            var hv = In(940, 73.5, 180); hv.A.Type = "wp_tu-22m2"; hv.A.SpeedKn = 450;
            double hvTurn = StrikeTotMath.ArcExtraS(15, 180, 450, StrikeCalib.TurnRateDps("wp_tu-22m2", 450));
            if (hvTurn < 20) return "a turn away from the launch point costs " + hvTurn;
            r = Row(); r.State = "at_gate"; r.Sent = "orbit_gate";
            var ag = In(964 - hvTurn / 2, 73.5, 180); ag.A.SpeedKn = 450;
            Step(r, ag, p);
            if (r.State != "at_gate") return "an agile type does not leave early";
            r = Row(); r.State = "at_gate"; r.Sent = "orbit_gate";
            hv.A.HeadingDeg = 0; hv.Now = 964 - hvTurn / 2;
            Step(r, hv, p);
            if (r.State != "at_gate") return "a heavy type pointing at the launch point leaves on the straight time";
            r = Row(); r.State = "at_gate"; r.Sent = "orbit_gate";
            hv.A.HeadingDeg = 180; hv.Now = 964 - hvTurn / 2;
            Step(r, hv, p);
            if (r.State != "run_in") return "a heavy type leaves " + hvTurn.ToString("0.0") + " s earlier than the straight time (State " + r.State + ")";
            // m2: the order condition of a heavy type counts the slow turn: 52 degrees at 4.3 deg/s + 2.5 + 2 = 16.7 s (an agile type: 9 s); the threshold 1083.3 against 1089.0
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = Tof(In(1084, 60, 52), 400); x.A.Type = "wp_tu-22m2"; x.A.SpeedKn = 537;
            if (Step(r, x, p).Kind != "issue") return "a slow turn is ordered earlier";
            r = Row(); r.State = "run_in"; r.Sent = "run_in";
            x = Tof(In(1084, 60, 52), 400); x.A.SpeedKn = 537;
            if (Step(r, x, p).Kind != "none") return "an agile type at the same time is not ordered yet";
            // n: T-342 the speed of the run in: at the gate with 286 kn from the loiter, the straight 15 nm take 188.8 s at 286 kn and 97.8 s at the planned 552: the run in starts from the planned speed
            // (the row leaves at TLaunch - 97.8 - reserve; with the loiter speed it would leave 91 s earlier)
            r = Row(); r.State = "at_gate"; r.Sent = "orbit_gate"; r.LaunchKn = 552;
            var slow = In(0, 73.5, 0); slow.A.SpeedKn = 286;
            double goPlanned = r.TLaunch - 15 / 552.0 * 3600.0 - p.RunInReserveS, goLoiter = r.TLaunch - 15 / 286.0 * 3600.0 - p.RunInReserveS;
            slow.Now = goPlanned - 1;
            if (Step(r, slow, p).Kind != "none" || r.State != "at_gate") return "T-342: before the time of the planned speed the row stays at the gate " + r.State;
            slow.Now = goPlanned;
            if (Step(r, slow, p).Kind != "run_in" || Math.Abs(r.LateS) > 1e-6) return "T-342: the run in starts from the planned speed " + r.State;
            r = Row(); r.State = "at_gate"; r.Sent = "orbit_gate";   // LaunchKn 0: the own speed (the old behaviour)
            slow = In(goLoiter, 73.5, 0); slow.A.SpeedKn = 286;
            if (Step(r, slow, p).Kind != "run_in") return "T-342: LaunchKn 0 keeps the own speed model";
            r = Row(); r.State = "at_gate"; r.Sent = "orbit_gate"; r.LaunchKn = 552; r.SpeedSent = "failed";   // SetSpeed refused three times: the own speed again
            slow = In(goLoiter - 1, 73.5, 0); slow.A.SpeedKn = 286;
            if (Step(r, slow, p).Kind != "none" || r.State != "at_gate") return "T-342: a refused throttle falls back to the own speed (too early)";
            slow = In(goLoiter, 73.5, 0); slow.A.SpeedKn = 286;
            if (Step(r, slow, p).Kind != "run_in") return "T-342: a refused throttle falls back to the own speed";
            var runIn = Row(); runIn.State = "at_gate"; runIn.Sent = "orbit_gate"; runIn.LaunchKn = 552;
            slow = In(goPlanned, 73.5, 0); slow.A.SpeedKn = 286;
            var ra = Step(runIn, slow, p);
            if (ra.Kind != "run_in" || ra.Kn != 552) return "T-342: the run in carries the speed " + ra.Kn;
            return null;
        }
    }
}
