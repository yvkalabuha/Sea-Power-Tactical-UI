using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    // T-341 phase D (ADR-0132 section 8, plan 34.3): the jammer aircraft of a strike on the game clock. After "Launch" every jammer of the chosen variant gets a state
    // (wait -> to_station -> on_station -> jamming -> off -> back, plus guard / retreat / no_station / lost): it leaves at TDepart, orbits at the station, is switched on at TOn (or when the
    // strike gets within the detection range of the radars of the covered targets + a margin), switched off EwHoldS after the last arrival, and never enters a SAM zone (guard). Only the game clock.
    internal sealed partial class StrikeController
    {
        /// <summary>The air threat service (set by AirGuardFeature): true = stage "retreat" for this aircraft of this group; the steer of a retreating jammer; the release of that steer.</summary>
        public Func<int, string, bool> AirRetreatNow;
        public Func<GeoPt, string, bool> AirThreatAtStation;   // T-349: (station, group) -> the station point has an AirGuard threat (stage >= Warn), null = not wired (v1.1 behaviour)
        public Func<int, string, bool> AirThreatNow;   // T-342 (AirGuardFeature): the unit has an air threat at any stage (Warn or Retreat): LastThreatT = now
        public Func<int, double, double, string> AirSteerEw;   // (uid, reserve nm, full range nm) -> error or null
        public Action<int> AirReleaseEw;

        private readonly Dictionary<int, double> _clearLogT = new Dictionary<int, double>();
        private readonly List<int> _ewOffPending = new List<int>();   // jammers the saved game says the mod switched on: switched off in the first tick after the load

        private static bool EwActive(string st) { return st == "to_station" || st == "on_station" || st == "jamming" || st == "guard" || st == "retreat"; }

        private StrikeEwState EwOf(StrikeGroup g, int uid) { return g.EwExec.FirstOrDefault(x => x.Uid == uid); }

        /// <summary>The aircraft is flown by the jammer logic (the wait-point logic leaves it alone).</summary>
        private bool EwBusy(StrikeGroup g, int uid) { var s = EwOf(g, uid); return s != null && EwActive(s.State); }

        public bool EwRetreating(int uid) { foreach (var g in _groups) { var s = EwOf(g, uid); if (s != null && s.State == "retreat") return true; } return false; }

        private string EwOn(StrikeGroup g, StrikeEwState s, StrikeEwPlan pl)
        {
            string err = null;
            try { err = _g.Jam(s.Uid, pl.AimPt, true); } catch (Exception e) { err = "failed:" + e.GetType().Name; }
            _log("[Strike] strike ew on uid=" + I(s.Uid) + " pt=" + Pt(pl.AimPt) + " -> " + (err ?? "ok") + " why=" + (s.Why ?? "plan"));
            if (err == null) { s.On = true; s.JamPt = pl.AimPt; s.JamKind = "point"; }
            else { try { _g.Jam(s.Uid, pl.AimPt, false); } catch (Exception) { } }   // review of D: a refused order may have left the stations switched on (IsOn is set before the task): switch them off again
            return err;
        }

        private string EwOff(StrikeGroup g, StrikeEwState s, string why)
        {
            if (!s.On) return null;
            if (s.JamKind == "object") { EwFighterOff(g, s, why); return null; }   // T-342 D: the object jam is switched off by the same cease
            string err = null;
            try { err = _g.Jam(s.Uid, s.JamPt, false); } catch (Exception e) { err = "failed:" + e.GetType().Name; }
            _log("[Strike] strike ew off uid=" + I(s.Uid) + " pt=" + Pt(s.JamPt) + " -> " + (err ?? "ok") + " why=" + why);
            s.On = false; s.JamKind = null; s.OffT = _g.Now;   // a refused switch off is not retried here: the game layer repeats it on its own when asked again (Release)
            return err;
        }

        /// <summary>The aircraft leaves the group (removed, released, lost, the group is disbanded): the stations are switched off first.</summary>
        private void EwDrop(StrikeGroup g, int uid)
        {
            var s = EwOf(g, uid);
            if (s == null) return;
            if (s.On) EwOff(g, s, "left_group");
            g.EwExec.Remove(s);
            try { if (AirReleaseEw != null) AirReleaseEw(uid); } catch (Exception) { }
        }

        private void EwOffAll(StrikeGroup g, string why)
        {
            foreach (var s in g.EwExec)
            {
                if (s.On) EwOff(g, s, why);
                if (s.State == "retreat") { try { if (AirReleaseEw != null) AirReleaseEw(s.Uid); } catch (Exception) { } }
                if (EwActive(s.State) || s.State == "wait") { s.State = "back"; s.Sent = null; }
            }
        }

        private void LogEwPlan(StrikeGroup g, StrikeEwPlan e, double now)
        {
            _log("[Strike] strike ew plan " + g.Name + " uid=" + I(e.Uid) + " station=" + Pt(e.StationPt) + " aim=" + Pt(e.AimPt) + " cone=" + F1(e.ConeDeg) + " cover=[" + string.Join(",", e.CoverIds.Select(I)) + "] uncovered=[" + string.Join(",", e.Uncovered.Select(I))
                 + "] t_depart=" + F1(e.TDepart - now) + " t_on=" + F1(e.TOn - now) + " t_off=" + F1(e.TOff - now) + " late_s=" + F1(e.LateS) + " why=" + e.Why);
            foreach (string b in e.BandsMissing) _log("[Strike] strike ew band " + g.Name + " " + b);
        }

        private static string EwPlanJson(StrikeEwPlan e, double now)
        {
            return "{\"uid\":" + I(e.Uid) + ",\"station\":{\"lat\":" + N(e.StationPt.Lat) + ",\"lon\":" + N(e.StationPt.Lon) + "},\"aim\":{\"lat\":" + N(e.AimPt.Lat) + ",\"lon\":" + N(e.AimPt.Lon) + "},\"cone_deg\":" + N(e.ConeDeg)
                   + ",\"bearing_deg\":" + N(e.BearingDeg) + ",\"cover\":[" + string.Join(",", e.CoverIds.Select(I)) + "],\"uncovered\":[" + string.Join(",", e.Uncovered.Select(I)) + "],\"bands_missing\":[" + string.Join(",", e.BandsMissing.Select(Q))
                   + "],\"t_depart\":" + N(e.TDepart) + ",\"t_on\":" + N(e.TOn) + ",\"t_off\":" + N(e.TOff) + ",\"late_s\":" + N(e.LateS) + ",\"shift_s\":" + N(e.ShiftS) + ",\"why\":" + Q(e.Why) + "}";
        }

        private string EwStateJson(StrikeGroup g, StrikeEwState s, StrikeAircraft a)
        {
            var e = s.Plan ?? new StrikeEwPlan();
            double now = _g.Now;
            return "{\"uid\":" + I(s.Uid) + ",\"type\":" + Q(a == null || a.Ew == null ? null : a.Ew.Type) + ",\"state\":" + Q(s.State) + ",\"on\":" + Json.Bool(s.On)
                   + ",\"station\":{\"lat\":" + N(e.StationPt.Lat) + ",\"lon\":" + N(e.StationPt.Lon) + "},\"aim\":{\"lat\":" + N(e.AimPt.Lat) + ",\"lon\":" + N(e.AimPt.Lon) + "},\"cone_deg\":" + N(e.ConeDeg) + ",\"bearing_deg\":" + N(e.BearingDeg)
                   + ",\"cover\":[" + string.Join(",", e.CoverIds.Select(I)) + "],\"uncovered\":[" + string.Join(",", e.Uncovered.Select(I)) + "],\"bands_missing\":[" + string.Join(",", e.BandsMissing.Select(Q))
                   + "],\"t_depart\":" + N(e.TDepart) + ",\"t_on\":" + N(e.TOn) + ",\"t_off\":" + N(e.TOff) + ",\"late_s\":" + N(e.LateS) + ",\"on_t\":" + (s.OnT < 0 ? "null" : N(s.OnT)) + ",\"off_t\":" + (s.OffT < 0 ? "null" : N(s.OffT))
                   + ",\"why\":" + Q(s.Why ?? e.Why) + FighterStateJson(s) + "}";
        }

        /// <summary>"Launch": every jammer of the chosen variant gets its state; one that has no station stays in the wait point.</summary>
        private void EwInit(StrikeGroup g, StrikeVariant v, double now)
        {
            foreach (var old in g.EwExec.ToList()) EwDrop(g, old.Uid);
            g.EwExec = new List<StrikeEwState>();
            foreach (var pl in v.Ew)
            {
                if (!g.Members.Contains(pl.Uid)) continue;
                var s = new StrikeEwState { Uid = pl.Uid, Plan = pl, State = pl.Why == "ok" ? "wait" : "no_station", Why = pl.Why == "ok" ? null : pl.Why };
                s.Role = pl.Role ?? "ships";
                g.EwExec.Add(s);
                if (s.Role == "fighters") _log("[Strike] strike ew role grp=" + g.Name + " uid=" + I(pl.Uid) + " role=fighters");
                _log("[Strike] strike ew " + s.State + " uid=" + I(pl.Uid) + " t_depart=" + F1(pl.TDepart - now) + " why=" + (s.Why ?? "plan"));
                if (pl.TDepart < now && pl.Why == "ok") _log("[Strike] strike ew late uid=" + I(pl.Uid) + " late_s=" + F1(now - pl.TDepart));
            }
        }

        private double LastArriveOf(StrikeGroup g)
        {
            double last = -1;
            var res = g.Timeline != null && IsExecStatus(g.Timeline.Status) ? g.Timeline.Results : g.Results;
            foreach (var r in res) last = Math.Max(last, r.LastArriveT);
            var rows = g.Timeline != null ? g.Timeline.Exec : g.LastExec;
            foreach (var r in rows) last = Math.Max(last, r.LaunchedT);
            if (g.Timeline == null) last = Math.Max(last, g.ResultT >= 0 ? g.ResultT - _p.ResultDelayS : -1);
            return last;
        }

        private void TickEw(double now)
        {
            if (_ewOffPending.Count > 0)
            {
                foreach (int uid in _ewOffPending.ToList())
                {
                    var a = Air(uid);
                    string err = null;
                    try { err = _g.Jam(uid, a == null ? default(GeoPt) : new GeoPt(a.Lat, a.Lon), false); } catch (Exception e) { err = e.GetType().Name; }
                    _log("[Strike] strike ew off uid=" + I(uid) + " -> " + (err ?? "ok") + " why=loaded");
                }
                _ewOffPending.Clear();
            }
            foreach (var g in _groups.ToList())
            {
                if (g.EwExec.Count == 0) { if (g.FighterThreats.Count > 0 || g.FighterUncovered.Count > 0) { g.FighterThreats = new List<StrikeFighterThreat>(); g.FighterUncovered = new List<int>(); } continue; }
                try { EwGroup(g, now); } catch (Exception e) { _log("[Strike] strike ew tick failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void EwGroup(StrikeGroup g, double now)
        {
            var tl = g.Timeline;
            var rows = tl != null && IsExecStatus(tl.Status) ? tl.Exec : new List<StrikeExecRow>();
            bool allTerminal = rows.All(r => StrikeExecMath.Terminal(r.State));
            var uids = new HashSet<int>(rows.Select(r => r.Uid));
            int inFlight = _msl.Values.Count(m => uids.Contains(m.Uid));
            double lastArrive = LastArriveOf(g);
            var hostiles = Hostiles();
            var fctx = FighterPrep(g, now);
            foreach (var s in g.EwExec.ToList())
            {
                var a = Air(s.Uid);
                if (a == null) { if (s.State != "lost") { s.On = false; s.JamKind = null; s.FighterId = 0; s.State = "lost"; _log("[Strike] strike ew lost uid=" + I(s.Uid)); } continue; }
                var pl = s.Plan;
                if (pl == null || s.State == "back" || s.State == "lost" || s.State == "no_station") continue;
                var pos = new GeoPt(a.Lat, a.Lon);
                bool moving = EwActive(s.State);
                // the guard: never closer than SamMaxNm + a part of the standoff to any hostile ship
                bool sam = false; string samShip = null; double samDist = 0;
                if (moving)
                    foreach (var h in hostiles)
                    {
                        double d = AswMath.DistNm(pos, new GeoPt(h.Lat, h.Lon));
                        if (d < Math.Max(0, h.SamMaxNm) + _p.StandoffNm * _p.EwGuardFrac) { sam = true; samShip = I(h.TrackId); samDist = d; break; }
                    }
                bool retreat = false;
                if (moving && s.State != "guard") { try { retreat = AirRetreatNow != null && AirRetreatNow(s.Uid, g.Name); } catch (Exception) { } }
                else if (s.State == "retreat") { try { retreat = AirRetreatNow != null && AirRetreatNow(s.Uid, g.Name); } catch (Exception) { } }
                // T-342: the last time an air threat of any stage was seen for this aircraft: the return to the station waits EwReturnClearS after it
                bool threatNow = retreat;
                if (!threatNow && (moving || s.State == "retreat")) { try { threatNow = AirThreatNow != null && AirThreatNow(s.Uid, g.Name); } catch (Exception) { } }
                if (threatNow) s.LastThreatT = now;
                double threatClearS = s.LastThreatT < 0 ? double.PositiveInfinity : Math.Max(0, now - s.LastThreatT);
                // the strike against the detection range of the covered targets: the nearest striker / missile
                double minD = -1, detNm = 0, best = double.MaxValue;
                foreach (int cid in pl.CoverIds)
                {
                    var cs = Ship(cid);
                    if (cs == null) continue;
                    var cp = new GeoPt(cs.Lat, cs.Lon);
                    foreach (int u in rows.Where(r => r.State == "run_in" || r.State == "issued" || r.State == "launched" || r.State == "held").Select(r => r.Uid).Distinct())   // only the aircraft that are on the attack run (the ones that wait or fly to the gate are outside the zones)
                    {
                        var sa = Air(u);
                        if (sa == null) continue;
                        double d = AswMath.DistNm(new GeoPt(sa.Lat, sa.Lon), cp), dn = StrikeEwMath.DetNm(cs, sa.AltFt > 0 ? sa.AltFt : 25000);
                        if (d - dn < best) { best = d - dn; minD = d; detNm = dn; }
                    }
                    foreach (var m in _msl.Values)
                    {
                        if (!uids.Contains(m.Uid)) continue;
                        double d = AswMath.DistNm(new GeoPt(m.Lat, m.Lon), cp), dn = StrikeEwMath.DetNm(cs, 50);
                        if (d - dn < best) { best = d - dn; minD = d; detNm = dn; }
                    }
                }
                double distSt = AswMath.DistNm(pos, pl.StationPt);
                if (s.State == "on_station" && now < s.NextTryT) continue;   // the last switch on was refused: wait before the retry
                bool fUnsafe, fFar;
                FighterPre(g, s, a, pos, fctx, hostiles, now, out fUnsafe, out fFar);
                if (fUnsafe) { s.LastThreatT = now; threatClearS = 0; }   // T-342 D: a fighter we cannot keep away from counts as a threat for the clear time of the return
                // T-349: the retreat is held while the station point itself is under an air threat (checked only when the timer alone would release the jammer)
                bool stationThreat = false;
                if (s.State == "retreat" && !retreat && threatClearS >= _p.EwReturnClearS && fFar && AirThreatAtStation != null)
                    try { stationThreat = AirThreatAtStation(pl.StationPt, g.Name); } catch (Exception) { }
                string next = StrikeEwMath.Decide(s, pl, now, distSt, minD, detNm, allTerminal, inFlight, lastArrive, sam, retreat, _p, threatClearS, fUnsafe, fFar, stationThreat);
                string prev = s.State;
                if (next == "retreat" && prev == "retreat" && !retreat && stationThreat)
                {
                    s.Why = "station_threat";
                    double lastLog;
                    if (!_clearLogT.TryGetValue(s.Uid, out lastLog) || now - lastLog >= 10 || now < lastLog) { _clearLogT[s.Uid] = now; _log("[Strike] strike ew station_hold uid=" + I(s.Uid) + " left=- track=-"); }
                }
                else if (next == "retreat" && prev == "retreat" && !retreat)
                {
                    // the retreat stage is gone, the clear time is not over: the jammer keeps away from the threat (the steer goes on while there is a cover)
                    s.Why = "clear_wait";
                    double left = Math.Max(0, _p.EwReturnClearS - threatClearS), lastLog;
                    if (!_clearLogT.TryGetValue(s.Uid, out lastLog) || now - lastLog >= 10 || now < lastLog) { _clearLogT[s.Uid] = now; _log("[Strike] strike ew clear_wait uid=" + I(s.Uid) + " left_s=" + F1(left)); }
                }
                else if (s.Why == "clear_wait" || s.Why == "station_threat") { s.Why = null; _clearLogT.Remove(s.Uid); }
                if (next != prev) EwEnter(g, s, a, next, now, sam, samShip, samDist);
                else EwKeep(g, s, a, now);
            }
            AssignFighters(g, fctx, now);
        }

        /// <summary>The orders that go with entering a state.</summary>
        private void EwEnter(StrikeGroup g, StrikeEwState s, StrikeAircraft a, string next, double now, bool sam, string samShip, double samDist)
        {
            var pl = s.Plan;
            string prev = s.State;
            s.State = next;
            switch (next)
            {
                case "to_station":
                    s.Sent = null;
                    if (prev == "retreat") { try { if (AirReleaseEw != null) AirReleaseEw(s.Uid); } catch (Exception) { } }
                    _log("[Strike] strike ew to_station uid=" + I(s.Uid) + " station=" + Pt(pl.StationPt) + " from=" + prev + (prev == "retreat" ? " clear_s=" + (s.LastThreatT < 0 ? "-" : F1(Math.Max(0, now - s.LastThreatT))) : ""));
                    EwKeep(g, s, a, now);
                    break;
                case "on_station":
                {
                    s.ArriveT = now;
                    string err = _g.Orbit(s.Uid, pl.StationPt, _p.EwOrbitNm);
                    _log("[Strike] strike ew on_station uid=" + I(s.Uid) + " -> " + (err ?? "ok"));
                    break;
                }
                case "jamming":
                {
                    if (s.JamKind == "object")
                    {
                        if (s.Role == "fighters") { s.OnT = now; _log("[Strike] strike ew jamming uid=" + I(s.Uid) + " t_on_plan=" + F1(pl.TOn - now) + " fighter_kept=" + I(s.FighterId)); break; }   // the reserve stays on the fighter; AssignFighters returns it to the ships
                        EwFighterOff(g, s, "attack_ships");   // the attack is for the ships only
                    }
                    string err = EwOn(g, s, pl);
                    if (err != null)
                    {
                        s.State = "on_station"; s.Tries++; s.Why = err; s.NextTryT = now + _p.EwRetryS;
                        if (s.Tries >= 3) { s.State = "no_station"; _log("[Strike] strike ew no_station uid=" + I(s.Uid) + " why=" + err); }
                    }
                    else { s.OnT = now; _log("[Strike] strike ew jamming uid=" + I(s.Uid) + " t_on_plan=" + F1(pl.TOn - now)); }
                    break;
                }
                case "off":
                    EwOff(g, s, "strike_over");
                    s.State = "back"; s.Sent = null;
                    _log("[Strike] strike ew back uid=" + I(s.Uid));
                    EwAfterBack(g, s, now);
                    break;
                case "guard":
                {
                    if (s.On) EwOff(g, s, "guard");
                    _log("[Strike] strike ew guard uid=" + I(s.Uid) + " why=sam_zone ship=" + (samShip ?? "-") + " dist=" + F1(samDist));
                    if (g.WaitPt.HasValue) { string err = _g.Goto(s.Uid, new List<GeoPt> { g.WaitPt.Value }, 0); _log("[Strike] strike ew guard route uid=" + I(s.Uid) + " -> wait " + (err ?? "ok")); }
                    EwReplan(g, s, a, now);
                    break;
                }
                case "retreat":
                    if (s.On) EwOff(g, s, "air_threat");
                    _log("[Strike] strike ew retreat uid=" + I(s.Uid));
                    EwKeep(g, s, a, now);
                    break;
                case "back":
                    if (s.On) EwOff(g, s, "strike_over");
                    s.Sent = null;
                    _log("[Strike] strike ew back uid=" + I(s.Uid));
                    EwAfterBack(g, s, now);
                    break;
            }
        }

        /// <summary>A jammer that has done its job goes to the wait point with the group while the group has missiles, else home.</summary>
        private void EwAfterBack(StrikeGroup g, StrikeEwState s, double now)
        {
            bool missiles = false;
            foreach (int uid in g.Members) { var m = Air(uid); if (m != null && StrikeAirMath.RoleOf(m) == "striker" && StrikeAirMath.UsableCount(m) > 0) { missiles = true; break; } }
            if (!missiles) Leave(g, s.Uid, "ew_done", now, true);
        }

        private void EwKeep(StrikeGroup g, StrikeEwState s, StrikeAircraft a, double now)
        {
            var pl = s.Plan;
            switch (s.State)
            {
                case "to_station":
                    if (s.Sent == "route" || s.Sent == "standoff" || now < s.NextTryT) break;
                    {
                        string err = _g.Goto(s.Uid, pl.Route.Count > 0 ? pl.Route : new List<GeoPt> { pl.StationPt }, pl.AltFt > 0 ? pl.AltFt : _p.EwAltFt);
                        _log("[Strike] strike ew route uid=" + I(s.Uid) + " pts=" + I(Math.Max(1, pl.Route.Count)) + " alt_ft=" + F1(_p.EwAltFt) + " -> " + (err ?? "ok"));
                        if (err == null) s.Sent = "route"; else s.NextTryT = now + _p.EwRetryS;
                    }
                    break;
                case "retreat":
                {
                    string err = null;
                    try { err = AirSteerEw == null ? "no_steer" : AirSteerEw(s.Uid, a.ReserveNm >= 0 ? a.ReserveNm : 9999, a.MaxRangeNm > 0 ? a.MaxRangeNm : 9999); } catch (Exception e) { err = e.GetType().Name; }
                    if (err != null && s.Sent != "steer_err" && s.Sent != "wait_goto") { s.Sent = "steer_err"; _log("[Strike] strike ew retreat steer uid=" + I(s.Uid) + " -> " + err); }
                    // T-342: no cover left while the clear time runs: one order to the wait point of the group (not a flight back into the threat)
                    if (err == "no_cover" && (s.Why == "clear_wait" || s.Why == "station_threat") && g.WaitPt.HasValue && s.Sent != "wait_goto")
                    {
                        string ge = _g.Goto(s.Uid, new List<GeoPt> { g.WaitPt.Value }, 0);
                        s.Sent = "wait_goto";
                        _log("[Strike] strike ew clear_wait route uid=" + I(s.Uid) + " -> wait " + (ge ?? "ok"));
                    }
                    break;
                }
            }
        }

        /// <summary>After a guard: the station and the route are counted again from the place the jammer is in now (the times stay); no solution = no_station.</summary>
        private void EwReplan(StrikeGroup g, StrikeEwState s, StrikeAircraft a, double now)
        {
            var pl = s.Plan;
            if (g.Variants == null || g.Chosen < 0 || g.Chosen >= g.Variants.Count) return;
            var v = g.Variants[g.Chosen];
            var sector = new List<StrikeShip>();
            foreach (int id in pl.CoverIds) { var sh = Ship(id); if (sh != null) sector.Add(sh); }
            if (sector.Count == 0) return;
            var airMap = new Dictionary<int, StrikeAircraft>();
            foreach (int uid in g.Members) { var m = Air(uid); if (m != null) airMap[uid] = m; }
            var np = StrikeEwMath.PlanOne(a, sector, Hostiles(), v, g.WaitPt, now, _p, p => LandSafe(p), airMap);
            if (np.Why != "ok")
            {
                s.State = "no_station"; s.Why = np.Why;
                _log("[Strike] strike ew no_station uid=" + I(s.Uid) + " why=" + np.Why);
                return;
            }
            pl.StationPt = np.StationPt; pl.AimPt = np.AimPt; pl.Route = np.Route; pl.BearingDeg = np.BearingDeg; pl.StationNm = np.StationNm; pl.BandsMissing = np.BandsMissing; pl.Why = "ok";
            s.Sent = null;
            _log("[Strike] strike ew replan uid=" + I(s.Uid) + " station=" + Pt(pl.StationPt));
        }
    }
}
