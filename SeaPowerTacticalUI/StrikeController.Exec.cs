using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase C (ADR-0132 sections 5-7, plan 24.3): the execution of a chosen plan. "Launch" turns the assignments into rows; every game second the tick steps the active row of
    // every aircraft (StrikeExecMath.Step: gate -> run in -> the order at now + turn + flight >= T) and applies the returned order. Only the game clock is used here.
    internal sealed partial class StrikeController
    {
        /// <summary>The launch book shared with the veto of the game layer (GameApi.StrikeBook); the stand has its own.</summary>
        public StrikeLaunchBook Book = new StrikeLaunchBook();

        private const double GateOrbitNm = 3;

        private static bool IsExecStatus(string s) { return s == "executing" || s == "cancelled"; }

        /// <summary>The strike of the group is running: executing, or cancelled while an order that was already given still waits for its missile.</summary>
        private static bool ExecRunning(StrikeGroup g)
        {
            var tl = g.Timeline;
            if (tl == null) return false;
            return tl.Status == "executing" || (tl.Status == "cancelled" && tl.Exec.Any(r => r.State == "issued"));
        }

        /// <summary>True while the aircraft still has a row that has not finished: the wait-point logic leaves it alone.</summary>
        private bool Busy(StrikeGroup g, int uid)
        {
            var tl = g.Timeline;
            return EwBusy(g, uid) || ClearBusy(g, uid) || (tl != null && IsExecStatus(tl.Status) && tl.Exec.Any(r => r.Uid == uid && !StrikeExecMath.Terminal(r.State)));
        }

        private double Diff() { try { double d = _g.Difficulty(); return d > 0 ? d : 1; } catch (Exception) { return 1; } }
        private string DiffSrcOf() { try { return _g.DiffSrc ?? "default"; } catch (Exception) { return "default"; } }
        private int GidOf(int uid) { try { return _g.GameId(uid); } catch (Exception) { return -1; } }
        private static string F3(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }

        /// <summary>Phase E1: one "strike calib" line per target of the launched variant (the inputs of the model; the script joins them with the event log of the game).</summary>
        private void LogCalib(StrikeGroup g, StrikeTimeline tl, StrikeVariant v)
        {
            try
            {
                double diff = Diff();
                bool ew = v.Ew != null && v.Ew.Count > 0;
                foreach (var t in v.PerTarget)
                    _log(StrikeCalibLog.Line(tl.PlanId, g.Name, g.Wave, t, t.Ammo, t.Sup, diff, StrikeCalib.CalibId, ew, t.ClearedBy, _g.Now));
            }
            catch (Exception e) { _log("[Strike] strike calib failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool Flag(Dictionary<string, object> c, string k) { object v; return c != null && c.TryGetValue(k, out v) && v is bool && (bool)v; }

        /// <summary>group, variant? (default: the chosen one), confirm? (needed for a stale plan). Ack {plan_id, rows, roe, t_first_issue, t_arrive}.</summary>
        public string Launch(Dictionary<string, object> cmd)
        {
            string group = Str(cmd, "group");
            if (string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            var g = Find(group);
            if (g == null) return Fail("no_group");
            if (ExecRunning(g) || (g.Timeline != null && g.Timeline.Status == "executing")) return Fail("executing");
            if (g.Timeline == null || g.Variants == null || g.Variants.Count == 0) return Fail("no_plan");
            int vi = g.Chosen;
            object o;
            if (cmd != null && cmd.TryGetValue("variant", out o))
            {
                if (!(o is double) || !IsInt((double)o) || (int)(double)o < 0 || (int)(double)o >= g.Variants.Count) return Fail("bad_args", "variant");
                vi = (int)(double)o;
            }
            return LaunchCore(g, vi, "player", Flag(cmd, "confirm"));
        }

        /// <summary>T-342 C: the launch of a plan of the group; by = player (the button) | auto (the auto mode) | stage2 (the release of the clearing). The red card needs confirm.</summary>
        internal string LaunchCore(StrikeGroup g, int vi, string by, bool confirm)
        {
            if (ExecRunning(g) || (g.Timeline != null && g.Timeline.Status == "executing")) return Fail("executing");
            if (g.Timeline == null || g.Variants == null || g.Variants.Count == 0) return Fail("no_plan");
            if (vi < 0 || vi >= g.Variants.Count) return Fail("no_plan");
            var tl = g.Timeline;
            if (tl.Status == "stale" && !confirm) return Fail("stale_confirm");
            if (tl.Status != "draft" && tl.Status != "stale" && tl.Status != "armed") return Fail("no_plan");
            if (g.WaitWhy == "no_sea" || g.WaitWhy == "no_point") return Fail("wait_no_sea");
            var v = g.Variants[vi];
            if (v.Red && !confirm) return Fail("red_confirm");   // T-342: the threshold of sinking is not reached: only with the confirmation of the window
            foreach (string w in v.Warnings)
            {
                if (w.StartsWith("gate:", StringComparison.Ordinal)) return Fail("gate", w.Substring(5));
                if (w.StartsWith("launch_land:", StringComparison.Ordinal)) return Fail("launch_land", w.Substring(12));
            }
            bool clearPause = v.Kind == "clear_pause";
            var rowsOf = clearPause ? v.Assignments.Where(a => a.Stage != 2).ToList() : v.Assignments.ToList();   // clear_pause: only stage 1 goes now, stage 2 waits at its gate
            if (rowsOf.Count == 0) return Fail("no_rows");
            var air = new Dictionary<int, StrikeAircraft>();
            foreach (int uid in v.Assignments.Select(a => a.Uid).Distinct())
            {
                var a = Air(uid);
                if (a == null || !g.Members.Contains(uid)) return Fail("no_aircraft", I(uid));
                if (!a.Airborne) return Fail("not_airborne", I(uid));
                air[uid] = a;
            }
            // the check of the target before the first order (and again before every row in the tick: Recheck)
            var shipOf = new Dictionary<int, StrikeShip>();
            foreach (int tid in rowsOf.Select(a => a.TargetId).Distinct())
            {
                var s = Ship(tid);
                if (s == null) return Fail("no_target");
                string why = StrikePlanMath.Exclusion(s);
                if (why != null) return Fail("target_excluded", why);
                shipOf[tid] = s;
            }
            if (clearPause)
                foreach (var a in v.Assignments.Where(x => x.Stage == 2))   // the main target of stage 2 is checked at the release; here only that it is still a valid target
                {
                    var s = Ship(a.TargetId);
                    if (s == null) return Fail("no_target");
                    string why = StrikePlanMath.Exclusion(s);
                    if (why != null) return Fail("target_excluded", why);
                }
            // go
            double now = _g.Now;
            bool afterClear = g.Clear != null && (g.Clear.Stage == "timeout" || g.Clear.Stage == "fuel" || g.Clear.Stage == "lost");
            bool stage2Button = g.Clear != null && (g.Clear.Stage == "red_wait" || g.Clear.Stage == "stale_wait");
            if (afterClear) ClearEnd(g, "main_launched");
            if (stage2Button) { g.Clear.Stage = "released"; g.Clear.RedConfirmed = true; by = "stage2"; }
            g.Chosen = vi;
            tl.Status = "executing"; tl.StaleWhy = null; tl.StartT = now; tl.DoneT = -1;
            tl.Exec = new List<StrikeExecRow>(); tl.Results = new List<StrikeTargetResult>();
            tl.HitBase = new Dictionary<int, int>();
            foreach (int tid in rowsOf.Select(x => x.TargetId).Distinct()) { int h0; try { _g.Fate(tid, out h0); } catch (Exception) { h0 = 0; } tl.HitBase[tid] = h0; }
            g.Results = new List<StrikeTargetResult>(); g.LastExec = new List<StrikeExecRow>(); g.ExecStatus = null; g.ResultT = -1;
            _arrT.Clear();
            if (by == "player") g.Auto.Waves = 0;                                        // the manual launch is not a wave of the auto and resets the count
            else if (by == "auto" || (by == "stage2" && g.Auto.On)) g.Auto.Waves++;
            g.Auto.LastLaunchT = now;
            string roe = ApplyRoe(g);
            tl.Roe = roe;
            _log("[Strike] strike launch " + g.Name + " plan=" + I(tl.PlanId) + " variant=" + I(vi) + " rows=" + I(rowsOf.Count) + " roe=" + roe + " by=" + by + (clearPause ? " clear_pause=" + I(v.ClearNeighbour) : "") + " t=" + F1(now));
            LogCalib(g, tl, v);
            foreach (var a in rowsOf.OrderBy(x => x.Uid).ThenBy(x => x.Order))
            {
                var ac = air[a.Uid];
                var m = ac.Missiles.FirstOrDefault(q => q.Ammo == a.Ammo);
                var r = new StrikeExecRow
                {
                    Uid = a.Uid, TargetId = a.TargetId, Salvo = a.Salvo, Order = a.Order, Ammo = a.Ammo, Gate = a.GatePt, Descent = a.DescentPt, Launch = a.LaunchPt, LaunchAltFt = a.LaunchAltFt,
                    CruiseAltFt = a.CruiseAltFt, LaunchNm = a.LaunchNm, MinNm = m == null ? 3.5 : m.MinNm, MaxNm = m == null || m.MaxNm <= 0 ? 65 : m.MaxNm, VmaxKn = m == null || m.SpeedKn <= 0 ? 540 : m.SpeedKn,
                    DescentAtStart = a.DescentAtStart, LaunchKn = a.LaunchKn, TGo = a.TGo, TLaunch = a.TLaunch, TArrive = a.TArrive, State = a.Order == 0 ? "to_gate" : "run_in",
                    InSamNm = a.InSamNm, InSamS = a.InSamS, UnderHorizon = a.UnderHorizon,
                };
                // the run in goes on past the launch point toward the target: an aircraft that is early keeps its heading on the axis (it would turn away at the end of its route, and the turn onto the target costs 13-20 s at 200 ft)
                StrikeShip tship;
                shipOf.TryGetValue(a.TargetId, out tship);
                if (tship == null) tship = Ship(a.TargetId);
                if (tship != null)
                {
                    r.ClassIni = tship.ClassIni ?? tship.ClassName;   // T-342 C: the class the plan was made for; a changed class stops the row (Recheck)
                    double extra = Math.Min(8, a.LaunchNm - (r.MinNm + 6));
                    if (extra >= 1) { r.Beyond = AswMath.Offset(r.Launch, AswMath.BearingDeg(r.Launch, new GeoPt(tship.Lat, tship.Lon)), extra); r.HasBeyond = true; }
                }
                tl.Exec.Add(r);
                _log("[Strike] strike sched " + g.Name + " uid=" + I(r.Uid) + " tgt=" + I(r.TargetId) + " ammo=" + r.Ammo + " salvo=" + I(r.Salvo) + " order=" + I(r.Order) + " t_go=" + F1(r.TGo - now)
                     + " t_issue=" + F1(a.TIssue - now) + " t_launch=" + F1(r.TLaunch - now) + " tof_s=" + F1(a.TofS) + " t_arrive=" + F1(r.TArrive - now) + " gate=" + Pt(r.Gate) + " gate_why=" + (a.GateWhy ?? "ok"));
            }
            foreach (int uid in air.Keys) _ms.Remove(uid);
            if (clearPause) { try { ClearStart(g, v, now, confirm); } catch (Exception e) { _log("[Strike] strike clear start failed: " + e.GetType().Name + ": " + e.Message); } }
            try { EwInit(g, v, now); foreach (var es in g.EwExec) _ms.Remove(es.Uid); } catch (Exception e) { _log("[Strike] strike ew init failed: " + e.GetType().Name + ": " + e.Message); }
            try { ExecGroup(g, now); } catch (Exception e) { _log("[Strike] strike exec failed: " + e.GetType().Name + ": " + e.Message); }
            double first = tl.Exec.Count == 0 ? 0 : tl.Exec.Min(x => x.TLaunch) - StrikeTotMath.TurnLeadS(0, _p) - now;
            return Ack(g.Name, "\"plan_id\":" + I(tl.PlanId) + ",\"rows\":" + I(tl.Exec.Count) + ",\"roe\":" + Q(roe) + ",\"t_first_issue\":" + N(first) + ",\"t_arrive\":" + N(v.T - now));
        }

        private void TickExec(double t)
        {
            foreach (var g in _groups.ToList())
            {
                if (g.Timeline == null || !IsExecStatus(g.Timeline.Status)) continue;   // executing, or cancelled and counting what flies
                try { ExecGroup(g, t); } catch (Exception e) { _log("[Strike] strike exec failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private bool LandSafe(GeoPt p) { try { return _g.IsLand(p); } catch (Exception) { return false; } }

        private void ExecGroup(StrikeGroup g, double now)
        {
            var tl = g.Timeline;
            var ships = _g.Ships() ?? new List<StrikeShip>();
            var air = _g.Aircraft() ?? new List<StrikeAircraft>();
            foreach (var grp in tl.Exec.GroupBy(r => r.Uid).ToList())
            {
                var rows = grp.OrderBy(r => r.Order).ToList();
                int idx = StrikeExecMath.ActiveIndex(rows);
                if (idx < 0) continue;
                var r = rows[idx];
                if (tl.Status == "executing" && r.State != "issued" && !g.Members.Contains(r.Uid))   // an issued row of a released aircraft is stepped on: it must end (launched / failed), else the strike never finishes
                {
                    foreach (var q in rows) if (!StrikeExecMath.Terminal(q.State) && q.State != "issued") { q.State = "cancelled"; q.Why = "released"; }
                    Book.Clear(r.Uid);
                    RestoreUidSpeed(r.Uid);
                    _log("[Strike] strike exec " + g.Name + " uid=" + I(r.Uid) + " state=cancelled why=released");
                    continue;
                }
                var a = air.FirstOrDefault(x => x != null && x.Uid == r.Uid);
                if ((tl.Status == "cancelled" || !g.Members.Contains(r.Uid)) && r.State == "issued" && a != null && a.Missiles.Where(m => m.Ammo == r.Ammo).Sum(m => m.Count) >= r.AmmoBefore && now - r.IssuedT > _p.LaunchConfirmS)
                {
                    // after "Cancel" (or the release of the aircraft) an order that did not produce a launch is never repeated
                    r.State = "failed"; r.Why = tl.Status == "cancelled" ? "cancelled" : "released"; Book.Clear(r.Uid);
                    _log("[Strike] strike exec " + g.Name + " uid=" + I(r.Uid) + " state=issued->failed why=" + (tl.Status == "cancelled" ? "cancelled" : "released") + "");
                    continue;
                }
                var ship = ships.FirstOrDefault(x => x != null && x.TrackId == r.TargetId);
                string rc = Recheck(r, ship);   // T-342 C: before every row the target is checked again (side, identification, civilian / neutral, class)
                var x2 = new StrikeExecInput { Now = now, A = a, Target = ship, TargetExcluded = rc != null && rc.StartsWith("excluded", StringComparison.Ordinal), ClassChanged = rc == "class_changed" };
                if (a != null) x2.AmmoNow = a.Missiles.Where(m => m.Ammo == r.Ammo).Sum(m => m.Count);
                if (ship != null && a != null)
                {
                    var apos = new GeoPt(a.Lat, a.Lon);
                    double d0 = AswMath.DistNm(apos, new GeoPt(ship.Lat, ship.Lon));
                    var lead = StrikeGeoMath.Lead(ship, _tofs.TickTof(r.Ammo, d0, r.VmaxKn));
                    x2.DistNm = AswMath.DistNm(apos, lead);
                    x2.BearingDeg = AswMath.BearingDeg(apos, lead);
                    x2.TofNowS = _tofs.TickTof(r.Ammo, x2.DistNm, r.VmaxKn);
                    r.TofPredS = x2.TofNowS;   // T-350: the flight the mod expects from the position of this tick (the issue line writes it as tof_pred)
                    x2.Live = ship.FixAgeS <= 1;
                    x2.FixT = ship.FixAgeS < 9000 ? now - ship.FixAgeS : -1;
                    var leadPt = lead;
                    x2.OverSea = () => StrikeGeoMath.OverSea(apos, leadPt, _p.LandCheckNm, LandSafe);
                }
                if (r.State == "issued" && now - r.IssuedT > _p.LaunchConfirmS) { try { x2.ActiveTask = _g.HasAttackTask(r.Uid, r.TargetId); } catch (Exception) { x2.ActiveTask = null; } }
                string before = r.State;
                var act = StrikeExecMath.Step(r, x2, _p);
                Apply(g, r, a, act, now, before);
                CheckRewarm(r, a, x2);
                if (r.State == "run_in" && r.SpeedSent == null && act.Kind != "run_in" && r.LaunchKn > 0) TrySpeed(r, now);   // the retry of a refused throttle order
            }
            // a salvo leaves one missile a second: the first drop confirms the launch, the rest of it is counted here (the result says how many really flew)
            foreach (var r in tl.Exec)
            {
                if (r.State != "launched" || r.LaunchedT < 0 || now - r.LaunchedT > r.Salvo + 6) continue;
                if (tl.Exec.Any(q => q != r && q.Uid == r.Uid && q.Ammo == r.Ammo && q.IssuedT >= r.IssuedT)) continue;   // the next row of the same missile has its own count
                var a = air.FirstOrDefault(x => x != null && x.Uid == r.Uid);
                if (a == null) continue;
                int left = a.Missiles.Where(m => m.Ammo == r.Ammo).Sum(m => m.Count);
                int n = r.AmmoBefore - left;
                if (n > r.LaunchedN) { Result(tl, r.TargetId).Fired += n - r.LaunchedN; r.LaunchedN = n; }
            }
            RestoreDone(tl, false);
            UpdateMissiles(g, now);
            TryFinish(g, now);
        }

        // T-342: the speed of the run in (the plan is built on it; the loiter speed left by the orbit of the gate is 40-50 % lower), and the throttle given back after the last row of the aircraft
        private readonly HashSet<int> _speedSet = new HashSet<int>();
        private readonly Dictionary<int, int> _speedTries = new Dictionary<int, int>();
        private readonly Dictionary<int, double> _speedNextT = new Dictionary<int, double>();

        private void TrySpeed(StrikeExecRow r, double now)
        {
            if (r.LaunchKn <= 0 || _speedSet.Contains(r.Uid) || r.SpeedSent == "failed") return;
            int tries; _speedTries.TryGetValue(r.Uid, out tries);
            double next; if (_speedNextT.TryGetValue(r.Uid, out next) && now < next) return;
            string err;
            try { err = _g.SetSpeed(r.Uid, r.LaunchKn); } catch (Exception e) { err = "failed:" + e.GetType().Name; }
            _speedTries[r.Uid] = ++tries; _speedNextT[r.Uid] = now + _p.IssueRetryS;
            _log("[Strike] strike speed uid=" + I(r.Uid) + " kn=" + F1(r.LaunchKn) + " -> " + (err ?? "ok"));
            if (err == null) { _speedSet.Add(r.Uid); r.SpeedSent = "set"; }
            else if (tries >= 3) { r.SpeedSent = "failed"; _log("[Strike] strike speed uid=" + I(r.Uid) + " gave up: the plan keeps the own speed"); }
        }

        /// <summary>One aircraft leaves the strike (released, removed, group disbanded): the throttle it had before comes back at once, not at the end of the whole strike.</summary>
        private void RestoreUidSpeed(int uid)
        {
            if (_speedSet.Remove(uid))
            {
                string err;
                try { err = _g.RestoreSpeed(uid); } catch (Exception e) { err = "failed:" + e.GetType().Name; }
                _log("[Strike] strike speed uid=" + I(uid) + " restore -> " + (err ?? "ok"));
            }
            _speedTries.Remove(uid); _speedNextT.Remove(uid);
        }

        /// <summary>The aircraft whose rows are all finished (force: whatever the rows) get the throttle they had before.</summary>
        private void RestoreDone(StrikeTimeline tl, bool force)
        {
            foreach (int uid in tl.Exec.Select(r => r.Uid).Distinct().ToList())
            {
                bool had = _speedSet.Contains(uid) || tl.Exec.Any(r => r.Uid == uid && r.SpeedSent == "failed");
                if (!had) continue;
                var rows = tl.Exec.Where(r => r.Uid == uid).ToList();
                if (!force && rows.Any(r => !StrikeExecMath.Terminal(r.State))) continue;
                if (_speedSet.Remove(uid))
                {
                    string err;
                    try { err = _g.RestoreSpeed(uid); } catch (Exception e) { err = "failed:" + e.GetType().Name; }
                    _log("[Strike] strike speed uid=" + I(uid) + " restore -> " + (err ?? "ok"));
                    foreach (var r in rows) if (r.SpeedSent == "set") r.SpeedSent = "restored";
                }
                _speedTries.Remove(uid); _speedNextT.Remove(uid);
            }
        }

        /// <summary>A row that waits (held) while its target went away from the planned range gets a new warm-up at the range it is at (the queue is served by Warm; the step never calls the game for the time of flight).</summary>
        private void CheckRewarm(StrikeExecRow r, StrikeAircraft a, StrikeExecInput x)
        {
            if (a == null || x.DistNm < 0 || r.State != "held") return;
            if (r.LaunchNm > 0 && Math.Abs(x.DistNm - r.LaunchNm) <= _p.TofRewarmNm) return;
            double alt = r.LaunchAltFt > 0 ? r.LaunchAltFt : a.CruiseAltFt;
            RequestRewarm(r.Uid, r.Ammo, x.DistNm, alt, r.LaunchKn > 0 ? r.LaunchKn : a.CruiseKn, r.TargetId);
        }

        /// <summary>null = the target is as planned; excluded:&lt;why&gt; (side, identification, civilian, neutral); class_changed (the class of the track is not the one of the plan).</summary>
        private string Recheck(StrikeExecRow r, StrikeShip s)
        {
            if (s == null) return null;
            string why = StrikePlanMath.Exclusion(s);
            if (why != null) return "excluded:" + why;
            string cls = s.ClassIni ?? s.ClassName;
            if (r.ClassIni != null && cls != null && !string.Equals(r.ClassIni, cls, StringComparison.Ordinal)) return "class_changed";
            return null;
        }

        private void LogState(StrikeGroup g, StrikeExecRow r, string before, string note = null)
        {
            if (before == r.State && note == null) return;
            _log("[Strike] strike exec " + g.Name + " uid=" + I(r.Uid) + " tgt=" + I(r.TargetId) + " state=" + before + "->" + r.State + (r.Why == null ? "" : " why=" + r.Why) + (note == null ? "" : " " + note));
        }

        private void RowFailedOrder(StrikeExecRow r, double now)
        {
            r.Sent = null; r.NextTryT = now + _p.IssueRetryS;
        }

        private void Apply(StrikeGroup g, StrikeExecRow r, StrikeAircraft a, StrikeExecAction act, double now, string before)
        {
            switch (act.Kind)
            {
                case "goto_gate":
                {
                    var pts = r.Sent == "gate_hi" ? new List<GeoPt> { r.Descent, r.Gate } : new List<GeoPt> { r.Gate };
                    string err = _g.Goto(r.Uid, pts, act.AltFt);
                    _log("[Strike] strike route " + g.Name + " uid=" + I(r.Uid) + " kind=" + r.Sent + " alt_ft=" + F1(act.AltFt) + " -> " + (err ?? "ok"));
                    if (err != null) RowFailedOrder(r, now);
                    break;
                }
                case "orbit_gate":
                {
                    string err = _g.Orbit(r.Uid, r.Gate, GateOrbitNm);
                    _log("[Strike] strike route " + g.Name + " uid=" + I(r.Uid) + " kind=orbit_gate -> " + (err ?? "ok"));
                    if (err != null) RowFailedOrder(r, now);
                    break;
                }
                case "run_in":
                {
                    var runPts = r.HasBeyond ? new List<GeoPt> { r.Launch, r.Beyond } : new List<GeoPt> { r.Launch };
                    string err = _g.Goto(r.Uid, runPts, act.AltFt);
                    _log("[Strike] strike route " + g.Name + " uid=" + I(r.Uid) + " kind=run_in alt_ft=" + F1(act.AltFt) + " late_s=" + F1(r.LateS) + " -> " + (err ?? "ok"));
                    if (err != null) RowFailedOrder(r, now);
                    else if (act.Kn > 0) TrySpeed(r, now);   // after the order: the game may put the aircraft back to the loiter speed on a new route
                    break;
                }
                case "issue":
                {
                    Book.Expect(r.Uid, r.TargetId, r.Ammo, now, _p.BookWinS);
                    string err = _g.Issue(r.Uid, r.TargetId, r.Ammo, r.Salvo);
                    double tofNow = r.TArrive - now - r.TurnS;
                    string hd = "hdg_unknown", dpsi = "";
                    var ts = Ship(r.TargetId);
                    if (a != null && a.HeadingDeg >= 0)
                    {
                        hd = "hdg=" + F1(a.HeadingDeg);
                        if (ts != null) dpsi = " dpsi=" + F1(StrikeTotMath.DeltaPsi(a.HeadingDeg, AswMath.BearingDeg(new GeoPt(a.Lat, a.Lon), new GeoPt(ts.Lat, ts.Lon))));
                    }
                    _log("[Strike] strike issue uid=" + I(r.Uid) + " tgt=" + I(r.TargetId) + " ammo=" + r.Ammo + " salvo=" + I(r.Salvo) + " " + hd + dpsi + " alt_ft=" + F1(a == null ? 0 : a.AltFt) + " kn=" + F1(a == null ? 0 : a.SpeedKn)
                         + " turn_s=" + F1(r.TurnS) + " tof_s=" + F1(tofNow) + " tof_pred=" + F1(a == null ? -1 : r.TofPredS) + " -> " + (err ?? "ok") + " late_s=" + F1(r.LateS) + " diff=" + F3(Diff()) + " gid=" + I(GidOf(r.Uid)) + " t=" + F1(now));
                    if (err != null)
                    {
                        Book.Clear(r.Uid);
                        r.State = "held"; r.Why = err; r.IssuedT = -1; r.HeldSince = now; r.NextTryT = now + _p.IssueRetryS; r.Logged = err;
                        _log("[Strike] strike held uid=" + I(r.Uid) + " why=" + err);
                    }
                    LogState(g, r, before);
                    break;
                }
                case "hold":
                    if (r.Logged != act.Why) { r.Logged = act.Why; _log("[Strike] strike held uid=" + I(r.Uid) + " why=" + act.Why); LogState(g, r, before); }
                    break;
                case "skip":
                    Book.Clear(r.Uid);
                    _log("[Strike] strike skip uid=" + I(r.Uid) + " tgt=" + I(r.TargetId) + " why=" + act.Why);
                    LogState(g, r, before);
                    if (act.Why == "excluded" || act.Why == "class_changed")
                    {
                        _log("[Strike] strike recheck grp=" + g.Name + " uid=" + I(r.Uid) + " tgt=" + I(r.TargetId) + " why=" + act.Why);
                        if (g.Auto.On) AutoStop(g, "recheck");
                    }
                    break;
                case "fail":
                    Book.Clear(r.Uid);
                    LogState(g, r, before);
                    break;
                case "launched":
                {
                    if (!g.Timeline.Exec.Any(q => q.Uid == r.Uid && q.State == "issued")) Book.Clear(r.Uid);
                    var tr = Result(g.Timeline, r.TargetId);
                    tr.Fired += r.LaunchedN;
                    _log("[Strike] strike launched uid=" + I(r.Uid) + " n=" + I(r.LaunchedN) + " dt_order_s=" + F1(r.DtOrderS) + " turn_s=" + F1(r.TurnS) + " late_s=" + F1(r.LateS));
                    LogState(g, r, before);
                    break;
                }
                default:
                    LogState(g, r, before);
                    if (before != r.State) r.Logged = null;
                    break;
            }
            if (act.Kind != "hold" && act.Kind != "issue" && r.State != "held") r.Logged = null;
        }
    }
}
