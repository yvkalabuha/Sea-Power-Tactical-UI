using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-279 (ADR-0112): the evasion helm. The game state PerformingAirOps steers by CourseCommand only (waypoints are ignored), so while it lasts the evasion writes its own
    // SetCourse (the course of the tick, already terrain-guarded by ADR-0108). After the evasion a follower leads the ship to its route the same way until the Air Ops end.
    // Never touches the flight deck or Common/. Main thread only. No Harmony and no Unity types.
    internal sealed partial class GameApi
    {
        private sealed class EvHelm
        {
            public object Cmd;                 // our SetCourse instance (ReferenceEquals = "ours")
            public double CmdDeg = double.NaN;
            public string Mode;                // airops | course_cmd | null
            public readonly EvHelmWatch Watch = new EvHelmWatch();
            public int Writes; public double StartS = double.NaN;
            public bool StartLogged;
            public bool AirSeen;               // T-293: Air Ops seen during this evasion
        }

        private sealed class EvHelmFollow { public object Cmd; public double CmdDeg, StartS, NextS; public int Passed; public int Uid; public bool Passive; public int? Tg, TgSeen; public double TLat = double.NaN, TLon = double.NaN; }   // T*: last target by coordinates (the T-278 detour tick removes reached points, indices shift)

        private readonly Dictionary<object, EvHelm> _evHelm = new Dictionary<object, EvHelm>(new RefEq());
        private readonly Dictionary<object, EvHelmFollow> _evHelmFollow = new Dictionary<object, EvHelmFollow>(new RefEq());
        private bool _evHelmOff, _evHelmOffLogged;   // a game member is missing: the helm is off for the session
        private bool CourseCmdOff;                   // R2 guard: SetCourse did not turn the ship outside Air Ops, the course_cmd mode is off for the session

        private static string HF(double v) { return EvF(v, "000"); }

        private void HelmLog(string line) { try { Log("[Evasion] " + line); } catch { } }

        private void HelmOffOnce(string why)
        {
            _evHelmOff = true;
            if (_evHelmOffLogged) return;
            _evHelmOffLogged = true;
            HelmLog("evasion helm off why=" + why);
        }

        // ---- game access (all try/catch) ----

        private bool HelmAirOps(object u, out string state)
        {
            state = null;
            try
            {
                state = StateName(u, "CurrentStateName");
                bool? flag = null;
                try { var f = Get(u, EvasionSpecs.ObjPerformingAirOps); if (f is bool) flag = (bool)f; } catch { }
                return EvasionHelmMath.AirOps(state, flag);
            }
            catch { return false; }
        }

        private object HelmCourseCmd(object u, out double? deg)
        {
            deg = null;
            try
            {
                double? d;
                var cc = GridCourseCmdTrue(u, out d);   // T-308: the demanded course comes back as a true course
                if (d.HasValue) deg = EvasionMath.Norm360(d.Value);
                return cc;
            }
            catch { return null; }
        }

        private bool HelmWrite(object u, double deg, out object cmd)
        {
            cmd = null;
            try
            {
                cmd = GridWriteCourseTrue(u, EvasionMath.Norm360(deg));   // T-308: true -> grid inside the layer
                return true;
            }
            catch (MissingMemberException) { cmd = null; HelmOffOnce("no_member"); return false; }
            catch (Exception) { cmd = null; return false; }
        }

        /// <summary>Gives the course back (null) only if the current command is ours.</summary>
        private void HelmNull(object u, object ours)
        {
            try
            {
                if (ours == null) return;
                double? d;
                var cur = HelmCourseCmd(u, out d);
                if (ReferenceEquals(cur, ours)) RxSet(u, EvasionSpecs.ObjCourseCommand, null);   // grid-ok: null, no number
            }
            catch { }
        }

        // ---- the helm of a run ----

        /// <summary>Once per evasion tick, after the course is chosen (cmdDeg = run.LastCourseSent, already terrain-guarded / ATW turn). Never throws.</summary>
        private void EvasionHelmTick(object u, double now, double cmdDeg, double hdg)
        {
            if (_evHelmOff) return;
            try
            {
                EvHelm h;
                if (!_evHelm.TryGetValue(u, out h)) { h = new EvHelm(); _evHelm[u] = h; }
                string state;
                bool air = HelmAirOps(u, out state);
                if (air) h.AirSeen = true;
                double? curDeg;
                object cur = HelmCourseCmd(u, out curDeg);
                bool ours = h.Cmd != null && ReferenceEquals(cur, h.Cmd);
                if (h.Cmd != null && !ours) h.Cmd = null;   // the game (or someone) replaced our command
                string by = ManeuverBy(state, false, false, false);
                bool diverged = EvasionHelmMath.Diverged(h.Watch, now, cmdDeg, hdg);
                int uid = UidOf(u);

                string mode;
                if (!air && h.Mode == "course_cmd" && ours)
                {
                    // R2 guard: our SetCourse outside Air Ops did not turn the ship within the Diverged window -> waypoint only, for the session
                    if (diverged)
                    {
                        CourseCmdOff = true;
                        HelmLog("evasion helm stuck uid=" + uid + " by=" + by + " state=" + state + " mode=course_cmd cmd=" + HF(cmdDeg) + " hdg=" + HF(hdg) + " fallback=waypoint");
                        HelmNull(u, h.Cmd); h.Cmd = null; h.Mode = null;
                        return;
                    }
                    mode = "course_cmd";
                }
                else mode = EvasionHelmMath.Mode(air, by, cur != null && !ours, diverged);
                if (mode == "course_cmd" && CourseCmdOff) mode = null;

                if (mode == null)
                {
                    if (diverged && !h.Watch.Logged)
                    {
                        h.Watch.Logged = true;
                        HelmLog("evasion helm stuck uid=" + uid + " by=" + by + " state=" + state + " cmd=" + HF(cmdDeg) + " hdg=" + HF(hdg) + " err=" + EvF(Math.Abs(EvasionMath.Diff(hdg, cmdDeg)), "0") + " s=" + EvF(now - h.Watch.SinceS, "0"));
                    }
                    if (ours) { HelmNull(u, h.Cmd); h.Cmd = null; }   // R3: the Air Ops ended, our course must not block the waypoints
                    h.Mode = null;
                    return;
                }

                if (EvasionHelmMath.NeedWrite(ours, curDeg, cmdDeg))
                {
                    object cmd;
                    if (!HelmWrite(u, cmdDeg, out cmd)) return;
                    bool first = !h.StartLogged || h.Mode != mode;
                    h.Cmd = cmd; h.CmdDeg = cmdDeg; h.Mode = mode; h.Writes++;
                    if (double.IsNaN(h.StartS)) h.StartS = now;
                    if (first)
                    {
                        h.StartLogged = true;
                        HelmLog("evasion helm start uid=" + uid + " mode=" + mode + " state=" + state + " course=" + HF(cmdDeg) + " hdg=" + HF(hdg) + " err=" + EvF(Math.Abs(EvasionMath.Diff(hdg, cmdDeg)), "0"));
                        if (mode == "airops" && !h.Watch.Logged)
                        {
                            h.Watch.Logged = true;
                            HelmLog("evasion helm stuck uid=" + uid + " by=" + by + " state=" + state + " cmd=" + HF(cmdDeg) + " hdg=" + HF(hdg) + " err=" + EvF(Math.Abs(EvasionMath.Diff(hdg, cmdDeg)), "0") + " s=0");
                        }
                    }
                    else if (EvasionDebug) HelmLog("evasion helm set uid=" + uid + " course=" + HF(cmdDeg));
                    if (mode == "course_cmd" && first) { h.Watch.SinceS = double.NaN; h.Watch.BestErr = double.NaN; h.Watch.Logged = false; }   // the window counts from our write
                }
                h.Mode = mode;
            }
            catch (Exception e) { HelmLog("evasion helm error uid=" + SafeUid(u) + " " + e.GetType().Name); }
        }

        private int SafeUid(object u) { try { return UidOf(u); } catch { return -1; } }

        /// <summary>Removes and returns the helm of u (EvasionFinish, before restore_control).</summary>
        private EvHelm EvasionHelmDetach(object u)
        {
            EvHelm h;
            if (u == null || !_evHelm.TryGetValue(u, out h)) return null;
            _evHelm.Remove(u);
            return h;
        }

        /// <summary>After restore_control and the T-278 detour: EvasionHelmMath.After -> follow | game | clear | none; logs "evasion helm end".</summary>
        private void EvasionHelmAfter(object u, EvHelm h, double now, string reason, int? tgPlayer = null)
        {
            if (h == null || (h.Writes == 0 && h.Cmd == null)) return;
            try
            {
                string state;
                bool air = HelmAirOps(u, out state);
                double? curDeg;
                object cur = HelmCourseCmd(u, out curDeg);
                // a null command is nobody's: the follower may take it (restore_control may have cleared ours)
                bool ours = (h.Cmd != null && ReferenceEquals(cur, h.Cmd)) || (cur == null && air);
                List<double[]> route = reason == "immobile" ? new List<double[]>() : Waypoints(u);
                string next = EvasionHelmMath.After(air, ours, route.Count);
                var f = (EvHelmFollow)null;
                if (next == "follow") f = new EvHelmFollow { Cmd = ours && cur != null ? cur : null, CmdDeg = curDeg ?? double.NaN, StartS = now, NextS = now, Uid = UidOf(u) };
                else if (next == "game" || next == "clear") HelmNull(u, cur != null && ReferenceEquals(cur, h.Cmd) ? h.Cmd : null);
                HelmLog("evasion helm end uid=" + UidOf(u) + " reason=" + reason + " airops=" + (air ? "1" : "0") + " next=" + next + " writes=" + h.Writes);
                // T-283 (ADR-0112 section 7): Air Ops still on -> the game puts back the telegraph of its entry on exit; a passive follower (no steering) remembers the player's one
                if (f == null && air && tgPlayer.HasValue) f = new EvHelmFollow { StartS = now, NextS = now, Uid = UidOf(u), Passive = true };
                if (f != null && air) { f.Tg = tgPlayer ?? Telegraph(u); f.TgSeen = Telegraph(u); }
                if (f != null) { _evHelmFollow[u] = f; EvasionHelmFollowTick(u, now); }
            }
            catch (Exception e) { HelmLog("evasion helm error uid=" + SafeUid(u) + " " + e.GetType().Name); }
        }

        /// <summary>T-293: before EvasionPlayerTelegraph/restore_control. Air Ops seen in this evasion and over now, the live telegraph is the evasion's one
        /// (5 or the slow step) and not the noted one -> NoteFieldSet(u, "telegraph", live) so restore_control gives the player's back. Logs once. Never throws.</summary>
        private void EvasionTelegraphExitNote(object u, EvHelm h, int? slowTg)
        {
            try
            {
                if (h == null || !h.AirSeen) return;
                string st;
                bool air = HelmAirOps(u, out st);
                int? live = Telegraph(u);
                var e = _ctlMem.Peek(u);
                FieldMem fm;
                int nv;
                int? noted = e != null && e.Fields.TryGetValue("telegraph", out fm) && fm != null && int.TryParse(fm.SetByMod, out nv) ? nv : (int?)null;
                if (!EvasionHelmMath.ExitInsideIsMod(true, air, live, noted, slowTg)) return;
                _ctlMem.NoteFieldSet(u, "telegraph", live.Value.ToString());
                HelmLog("evasion helm telegraph uid=" + SafeUid(u) + " from=" + noted + " to=" + live + " reason=airops_exit_inside");
            }
            catch { }
        }

        /// <summary>T-283: the player's telegraph from the restore snapshot (when the player has not changed it since the mod set 5), else the current one. Never throws.</summary>
        private int? EvasionPlayerTelegraph(object u)
        {
            try
            {
                int? cur = Telegraph(u);
                var e = _ctlMem.Peek(u);
                int v;
                if (e != null && cur.HasValue && ControlMemory.DecideField(e, "telegraph", cur.Value.ToString()) == "restore"
                    && int.TryParse(e.Fields["telegraph"].Saved, out v)) return v;
                return cur;
            }
            catch { return null; }
        }

        /// <summary>T-283: the game's PerformingAirOps.onExit set the telegraph of its entry (the evasion's 5); the player's one goes back once.</summary>
        private void HelmTelegraphExit(object u, EvHelmFollow f, int? tgNow)
        {
            try
            {
                int? fix = EvasionHelmMath.ExitFix(f.Tg, tgNow);
                if (!fix.HasValue) return;
                string r = TelegraphCmd("0", new Dictionary<string, object> { { "uid", (double)UidOf(u) }, { "telegraph", (double)fix.Value } });
                HelmLog("evasion helm telegraph uid=" + f.Uid + " from=" + tgNow + " to=" + fix.Value + " reason=airops_off ok=" + (r.Contains("\"ok\":true") ? "1" : "0"));
            }
            catch { }
        }

        private void HelmFollowEnd(object u, EvHelmFollow f, string reason, double now, bool nullIt)
        {
            _evHelmFollow.Remove(u);
            if (nullIt) HelmNull(u, f.Cmd);
            HelmLog("evasion helm follow end uid=" + f.Uid + " reason=" + reason + " after=" + EvF(now - f.StartS, "0"));
        }

        /// <summary>T-283 review: the follower ends while Air Ops go on (foreign / timeout / no_route): steering stops, but the player's telegraph must still be put back on the Air Ops exit, so the entry stays as a passive one.</summary>
        private void HelmFollowDemote(object u, EvHelmFollow f, string reason, double now, bool nullIt)
        {
            if (nullIt) HelmNull(u, f.Cmd);
            HelmLog("evasion helm follow end uid=" + f.Uid + " reason=" + reason + " after=" + EvF(now - f.StartS, "0"));
            f.Cmd = null; f.Passive = true;
        }

        /// <summary>From AutoEvadeHook (ships, before EvasionTick): steers the follower; ends on Air Ops off / foreign command / no route / FollowMaxS.</summary>
        private void EvasionHelmFollowTick(object u, double now0)
        {
            EvHelmFollow f;
            if (u == null || _evHelmFollow.Count == 0 || !_evHelmFollow.TryGetValue(u, out f)) return;
            try
            {
                double? mt = Frame.MissionTimeS;
                if (!mt.HasValue) return;
                double now = mt.Value;
                // T-283: Air Ops over first (N2): the game's onExit clears CourseCommand, which would look like a foreign command; and puts back its own telegraph
                string state;
                bool air = HelmAirOps(u, out state);
                int? tgNow = Telegraph(u);
                if (!air) { HelmTelegraphExit(u, f, tgNow); HelmFollowEnd(u, f, "airops_off", now, true); return; }
                f.Tg = EvasionHelmMath.TgObserve(f.Tg, ref f.TgSeen, tgNow);
                // review fix: a foreign command (the player's course) ends the follower at once, not only on the 5 s recheck
                double? curDeg;
                object cur = HelmCourseCmd(u, out curDeg);
                bool ours = f.Cmd != null && ReferenceEquals(cur, f.Cmd);
                if (f.Cmd != null && !ours) { if (!f.Passive) HelmFollowDemote(u, f, "foreign", now, false); return; }
                if (now < f.NextS) return;
                f.NextS = now + EvasionHelmMath.FollowRecheckS;
                if (f.Passive) return;   // ends with the Air Ops (or Clear/Release)
                if (now - f.StartS >= EvasionHelmMath.FollowMaxS) { HelmFollowDemote(u, f, "timeout", now, true); return; }
                double lat, lon, hd;
                if (!OwnPos(u, out lat, out lon, out hd)) return;
                var route = Waypoints(u);
                var dist = new List<double>();
                foreach (var w in route) dist.Add(WorldDistanceNm(lat, lon, w[0], w[1]));
                int start = 0;   // review fix: re-find the last target by coordinates; a restack of the detour shifts the indices (an index would skip a detour point = a line over land)
                if (!double.IsNaN(f.TLat))
                    for (int i = 0; i < route.Count; i++)
                        if (Math.Abs(route[i][0] - f.TLat) < 1e-5 && Math.Abs(route[i][1] - f.TLon) < 1e-5) { start = i; break; }
                f.Passed = start;
                int idx = EvasionHelmMath.FollowIndex(dist, f.Passed);
                if (idx < 0) { HelmFollowDemote(u, f, "no_route", now, true); return; }
                f.Passed = idx; f.TLat = route[idx][0]; f.TLon = route[idx][1];
                double course = WorldBearing(lat, lon, route[idx][0], route[idx][1]);
                if (f.Cmd == null || EvasionHelmMath.NeedWrite(true, curDeg, course))
                {
                    object cmd;
                    if (!HelmWrite(u, course, out cmd)) return;
                    f.Cmd = cmd; f.CmdDeg = course;
                    if (EvasionDebug) HelmLog("evasion helm set uid=" + f.Uid + " course=" + HF(course));
                }
            }
            catch (Exception e) { HelmLog("evasion helm error uid=" + SafeUid(u) + " " + e.GetType().Name); }
        }

        /// <summary>A new evasion of the same ship: the follower of the previous one ends (the command is not nulled, the helm takes over).</summary>
        private void EvasionHelmFollowDrop(object u)
        {
            EvHelmFollow f;
            if (u == null || !_evHelmFollow.TryGetValue(u, out f)) return;
            string st;
            if (!HelmAirOps(u, out st)) HelmTelegraphExit(u, f, Telegraph(u));   // T-283: the snapshot of the new evasion must not be the telegraph the game set on the Air Ops exit
            HelmFollowEnd(u, f, "evasion", Frame.MissionTimeS ?? f.StartS, false);
        }

        /// <summary>Give the course back (null only if ours is current) and forget u; any path that drops a run.</summary>
        private void EvasionHelmRelease(object u, string reason)
        {
            if (u == null) return;
            try
            {
                EvHelm h;
                if (_evHelm.TryGetValue(u, out h))
                {
                    _evHelm.Remove(u);
                    HelmNull(u, h.Cmd);
                    if (h.Writes > 0) HelmLog("evasion helm end uid=" + SafeUid(u) + " reason=" + reason + " airops=na next=none writes=" + h.Writes);
                }
                EvHelmFollow f;
                if (_evHelmFollow.TryGetValue(u, out f)) HelmFollowEnd(u, f, "released", Frame.MissionTimeS ?? f.StartS, true);
            }
            catch { }
        }

        internal void EvasionHelmReleaseUid(int uid, string reason)
        {
            if (_evHelm.Count == 0 && _evHelmFollow.Count == 0) return;
            try
            {
                var keys = new List<object>(_evHelm.Keys);
                foreach (var k in _evHelmFollow.Keys) if (!keys.Contains(k)) keys.Add(k);
                foreach (var k in keys) if (SafeUid(k) == uid) EvasionHelmRelease(k, reason);
            }
            catch { }
        }

        /// <summary>Mission left / save loaded: the objects are gone, nothing is written.</summary>
        private void EvasionHelmClear() { _evHelm.Clear(); _evHelmFollow.Clear(); }
    }
}
