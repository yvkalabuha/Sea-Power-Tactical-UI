using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-173 / ADR-0074 + ADR-0072 s.1.2 (without "Proposed"): the attack automaton of one target:
    // approach (or the MAD check) -> engaged -> in the water -> assessment -> repeat until the track is destroyed.
    // The strike group (AswController.Strike.cs) picks the targets and the shooters; this file runs one target with one shooter (and a second number).
    internal sealed partial class AswController
    {
        /// <summary>One submarine track under attack. State: idle | verify | approach | engaged | in_water | assess | wait | hold | reacquire | done.</summary>
        private sealed class TargetRec
        {
            public int Id, Shooter, Second, Torps, Hits, MissStreak, TorpBefore, TorpId, HitsBefore = -1;
            public string Name, Group, Role = "shooter", State = "idle", Hold, HoldArg, Done, FixKind = "live", Unit = "";
            public bool Unknown, Retried, Verified, WantFix, NotTarget, StaleLogged, InWaterNoted;
            public double Lat, Lon, LastLiveS = -1, LiveSinceS = -1, StateS, EndS = -1e9, DropS, EngagedS, GotoS = -1e9, GLat, GLon, NextTryS, VerifyS, DoneS, HoldS, AssignedS;
            public OwnSubLatch Latch;
            public string OwnSubName; public double OwnSubNm;
            public HashSet<int> KnownTorps = new HashSet<int>();
            public string PrevState = "approach";
        }

        private readonly Dictionary<int, TargetRec> _targets = new Dictionary<int, TargetRec>();
        private readonly Dictionary<int, double> _badUntil = new Dictionary<int, double>();   // shooter uid -> not picked again until (after an approach / drop timeout)
        private readonly Dictionary<int, double[]> _waitAt = new Dictionary<int, double[]>();  // uid -> {time, lat, lon, bearing, datumLat, datumLon} of the last waiting-point order (T-331: the anchor)
        private List<AswContact> _subsList = new List<AswContact>();
        private Dictionary<int, AswContact> _subsById = new Dictionary<int, AswContact>();
        private int _tickNo, _subsStamp = -1;

        internal const double AttackRetryS = 5, AttackRegotoNm = 0.5, AttackRegotoS = 60, DoneKeepS = 120, StaleFixGiveUpS = 300, WaitRegotoS = 60;

        /// <summary>The submarine tracks of this tick (one game read for every group).</summary>
        private List<AswContact> SubsTick()
        {
            if (_subsStamp != _tickNo)
            {
                _subsStamp = _tickNo;
                _subsList = new List<AswContact>();
                _subsById = new Dictionary<int, AswContact>();
                var l = _game.SubContacts();
                if (l != null) foreach (var c in l) { _subsList.Add(c); _subsById[c.Id] = c; }
            }
            return _subsList;
        }

        /// <summary>The aircraft is on an attack run (shooter or second number of a target that is not finished): tactics and holding leave it alone.</summary>
        private bool Attacking(int uid)
        {
            if (uid == 0) return false;
            foreach (var r in _targets.Values) if (r.State != "done" && (r.Shooter == uid || r.Second == uid)) return true;
            return false;
        }

        /// <summary>State of the attack run on a track (stands and diagnostics); null = none.</summary>
        internal string RunState(int contactId) { TargetRec r; return _targets.TryGetValue(contactId, out r) ? r.State : null; }

        /// <summary>Why the run waits (own_sub | neutral | air_threat | stale_fix | disabled), null = not held.</summary>
        internal string RunHold(int contactId) { TargetRec r; return _targets.TryGetValue(contactId, out r) && r.State == "hold" ? r.Hold : null; }

        /// <summary>How the run ended (killed | limit | lost | surfaced | skipped | not_target | stopped | no_torpedo), null = not finished.</summary>
        internal string RunDone(int contactId) { TargetRec r; return _targets.TryGetValue(contactId, out r) ? r.Done : null; }

        /// <summary>T-214: the shooter uid of the run on a track (0 = none).</summary>
        internal int RunShooter(int contactId) { TargetRec r; return _targets.TryGetValue(contactId, out r) ? r.Shooter : 0; }

        internal int RunTorpedoes(int contactId) { TargetRec r; return _targets.TryGetValue(contactId, out r) ? r.Torps : 0; }

        /// <summary>The inner entry of the strike group (and of the stands): the aircraft attacks the track. role: shooter | second (waits at the spook radius).</summary>
        internal bool StartStrikeRun(string group, int contactId, int uid, string role)
        {
            AswMember m;
            if (_now < _game.Now) _now = _game.Now;   // a command can come before the first tick of this mission
            if (!_live.TryGetValue(uid, out m) || m.Where != "air") return false;
            AswContact c;
            SubsTick();
            _subsById.TryGetValue(contactId, out c);
            TargetRec r;
            if (!_targets.TryGetValue(contactId, out r) || r.State == "done")
            {
                r = new TargetRec { Id = contactId, Name = c != null ? c.Name : "Sub #" + I(contactId), Group = group, StateS = _now, AssignedS = _now };
                if (c != null) { r.Lat = c.Lat; r.Lon = c.Lon; r.Unknown = c.Side == null || !c.Side.Equals("hostile", StringComparison.OrdinalIgnoreCase) && !AttackHostility.MapHostile(c.MapPosture, c.VmPosture); r.LastLiveS = c.Live ? _now : -1; r.LiveSinceS = c.Live ? _now : -1; }
                _targets[contactId] = r;
            }
            r.Group = group;
            if (role == "second") { if (r.Shooter != uid) r.Second = uid; return true; }
            if (r.Shooter == uid && r.State != "idle") return true;
            ReleaseRuns(null, uid, "reassigned", contactId, true);   // T-214: one shooter, one run
            r.Shooter = uid; r.Role = role;
            r.AssignedS = _now; r.Retried = false;
            SetState(r, "approach", "assigned");
            return true;
        }

        private void SetState(TargetRec r, string st, string why)
        {
            if (r.State == st && st != "approach") return;
            string old = r.State;
            if (st == "approach" && old != "approach") r.NextTryS = _now;
            r.State = st; r.StateS = _now;
            _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " uid=" + I(r.Shooter) + " state " + old + " -> " + st + (why == null ? "" : " reason=" + why));
        }

        // ---- the tick ------------------------------------------------------------------------------------------------

        private void TickAttacks()
        {
            foreach (var r in new List<TargetRec>(_targets.Values))
            {
                try { AttackStep(r); }
                catch (Exception e) { _game.Log("asw attack error contact=" + I(r.Id) + ": " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private AswMember LiveOf(int uid) { AswMember m; return uid != 0 && _live.TryGetValue(uid, out m) ? m : null; }

        private bool Armed(AswMember m) { return m != null && m.Where == "air" && !Excluded(m) && !AswMath.IsLeaving(m) && m.Torpedoes > 0; }

        private double FixAge(TargetRec r, AswContact c)
        {
            if (c != null && c.FixS >= 0) return Math.Max(0, _now - c.FixS);
            return r.LastLiveS >= 0 ? Math.Max(0, _now - r.LastLiveS) : -1;
        }

        private void Done(TargetRec r, string why)
        {
            if (r.State == "done") return;
            if (why == "killed") SettleHits(r);   // T-231: a kill while the torpedo is in the water: the ledger of hits is read now
            SetState(r, "done", why);
            r.Done = why; r.DoneS = _now;
            if (why == "killed") _killedIds.Add(r.Id);
            _game.Log("asw attack " + r.Group + " done contact=" + I(r.Id) + " reason=" + why + " torpedoes=" + I(r.Torps) + " hits=" + I(r.Hits));
            _game.Event("asw_attack_done", Obj("group", r.Group, "contact", r.Id, "track", r.Name, "reason", why, "torpedoes", r.Torps, "hits", r.Hits));
            NoteAlert(r.Id, why);
        }

        /// <summary>T-231: takes the hits of the ledger (HitsOn) that Assess has not counted yet; a second call adds nothing (HitsBefore moves).</summary>
        private void SettleHits(TargetRec r)
        {
            if (r == null) return;
            int now = _game.HitsOn(r.Id);
            if (now >= 0 && r.HitsBefore >= 0 && now > r.HitsBefore) { r.Hits += now - r.HitsBefore; r.HitsBefore = now; }
        }

        private void AttackStep(TargetRec r)
        {
            if (r.State == "done") { if (_now - r.DoneS > DoneKeepS && r.Done != "limit") _targets.Remove(r.Id); return; }
            AswContact c; _subsById.TryGetValue(r.Id, out c);
            var g = Find(r.Group);
            bool strike = g != null && g.Order == "strike";
            if (c != null && c.Killed) { Done(r, "killed"); return; }
            if (c != null) { r.Lat = c.Lat; r.Lon = c.Lon; }
            bool live = c != null && StrikeLive(c);
            if (c != null && c.Live) { r.LastLiveS = _now; if (r.LiveSinceS < 0) r.LiveSinceS = _now; } else if (!live) r.LiveSinceS = -1;
            if (strike && r.NotTarget) { Done(r, "not_target"); return; }
            if (c != null && c.Surfaced) { Done(r, "surfaced"); return; }
            // a track that vanished right after a weapon was released at it (no destroyed flag is readable) is taken as destroyed
            double hit;
            if (c == null && _hitAt.TryGetValue(r.Id, out hit) && _now - r.LastLiveS >= HitVanishS && r.LastLiveS >= hit - 5 && _now - hit < 400)
            {
                SettleHits(r);
                if (_killedEmitted.Add(r.Id)) { _game.Log("asw contact " + r.Group + " killed contact=" + I(r.Id) + " track=" + r.Name + " (gone after a hit) torpedoes=" + I(r.Torps) + " hits=" + I(r.Hits)); _game.Event("asw_contact_killed", Obj("group", r.Group, "contact", r.Id, "track", r.Name, "lat", r.Lat, "lon", r.Lon, "torpedoes", r.Torps, "hits", r.Hits)); }
                Done(r, "killed"); return;
            }
            // the shooter is gone or empty: the second number takes over, else the group is asked for another one
            if (r.State != "in_water" && r.State != "assess" && r.State != "reacquire" && r.Shooter != 0 && !ShooterValid(r, LiveOf(r.Shooter)) && !(r.State == "engaged"))
            {
                _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " shooter uid=" + I(r.Shooter) + " cannot shoot: " + ShooterWhy(r, LiveOf(r.Shooter)));
                LoseShooter(r, "shooter_gone", false);
            }
            else if (r.State == "reacquire" && r.Shooter != 0 && !ShooterValid(r, LiveOf(r.Shooter)))   // T-214: the state stays, the shooter goes
            {
                _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " shooter uid=" + I(r.Shooter) + " released in reacquire: " + ShooterWhy(r, LiveOf(r.Shooter)));
                DropShooterKeepState(r);
            }
            if (!live && r.State != "in_water" && r.State != "assess")
            {
                if (r.State != "reacquire") { r.PrevState = r.State == "hold" ? "approach" : r.State; SetState(r, "reacquire", "track_not_live"); }
                if (_now - r.StateS >= _cfg.Loc.LostHoldS && !RingActive(r.Id)) { Done(r, "lost"); return; }   // T-176 C11: the lost-contact ring works: the run waits for it
                WaitShooters(r);
                return;
            }
            if (live && r.State == "reacquire") { SetState(r, r.PrevState == "engaged" || r.PrevState == "verify" ? "approach" : r.PrevState, "live_again"); }
            var m = LiveOf(r.Shooter);
            switch (r.State)
            {
                case "idle": WaitShooters(r); break;
                case "approach":
                case "hold":
                case "wait":
                    if (m == null) { SetState(r, "idle", "no_shooter"); break; }
                    if (r.State == "wait")
                    {
                        if (_now - r.EndS < _cfg.Strike.ReattackGapS) { WaitShooters(r); break; }
                        SetState(r, "approach", "reattack");
                    }
                    Approach(r, c, m, g);
                    break;
                case "verify": Verify(r, c, m); break;
                case "engaged": Engaged(r, c, m); break;
                case "in_water": InWater(r, c, m); break;
                case "assess": Assess(r, c); break;
            }
        }

        private static string ShooterWhy(AswMember m)
        {
            if (m == null) return "gone";
            if (m.Where != "air") return "not_airborne";
            if (m.Torpedoes <= 0) return "no_torpedo";
            return "leaving";
        }

        private string ShooterWhy(TargetRec r, AswMember m)
        {
            if (m != null && Armed(m)) return GotoGated(m.Uid) ? "not_controllable" : "not_member";
            return ShooterWhy(m);
        }

        /// <summary>T-214: armed, a member of the run's group, and not held by a not_controllable error.</summary>
        private bool ShooterValid(TargetRec r, AswMember m)
        {
            if (!Armed(m)) return false;
            var g = Find(r.Group);
            if (g != null && !g.Members.Contains(m.Uid)) return false;
            GotoFail gf;
            return !(_gotoFail.TryGetValue(m.Uid, out gf) && gf.Err == "not_controllable" && _now - gf.At < GotoFailGapS());
        }

        /// <summary>The shooter leaves a run that keeps its state (reacquire): the second number takes the place, else nobody.</summary>
        private void DropShooterKeepState(TargetRec r)
        {
            if (r.Second != 0 && Armed(LiveOf(r.Second))) { r.Shooter = r.Second; r.Second = 0; r.Role = "shooter"; r.AssignedS = _now; }
            else { r.Shooter = 0; r.Second = 0; }
            r.Retried = false;
        }

        /// <summary>T-214: takes uid off every unfinished run (shooter / second); group null = all groups; except = a contact id that keeps it.
        /// engaged / in_water / assess keep the id (the torpedo is tracked by it; the shooter check after assess changes it); reacquire keeps its state.
        /// quiet = the old run goes idle without asking the group for a shooter (the aircraft is moved to another target).</summary>
        private void ReleaseRuns(string group, int uid, string why, int except = 0, bool quiet = false)
        {
            if (uid == 0) return;
            foreach (var r in new List<TargetRec>(_targets.Values))
            {
                if (r.State == "done" || r.Id == except || (group != null && r.Group != group)) continue;
                if (r.Second == uid) r.Second = 0;
                if (r.Shooter != uid) continue;
                if (r.State == "engaged" || r.State == "in_water" || r.State == "assess") continue;
                _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " shooter uid=" + I(uid) + " released: " + why);
                if (r.State == "reacquire") { DropShooterKeepState(r); continue; }
                if (quiet) { r.Shooter = 0; r.Second = 0; SetState(r, "idle", why); continue; }
                LoseShooter(r, why, false);
            }
        }

        // ---- T-214: the one goto path of the attack automaton ------------------------------------------------------------

        private sealed class GotoFail { public double At; public string Err; public int Repeats; }
        private readonly Dictionary<int, GotoFail> _gotoFail = new Dictionary<int, GotoFail>();
        internal const double GotoFailRealS = 60;
        /// <summary>Mission seconds that cover at least 60 real seconds (the time scale is an upper estimate of the real speed).</summary>
        private double GotoFailGapS() { return GotoFailRealS * Math.Max(1, _game.TimeScale); }

        private bool GotoGated(int uid) { GotoFail gf; return _gotoFail.TryGetValue(uid, out gf) && _now - gf.At < GotoFailGapS(); }

        /// <summary>null = sent; "gated" = held after an error of this aircraft; else the game error (logged once per gap; not_controllable also frees the aircraft).</summary>
        private string AttackGoto(TargetRec r, int uid, List<GeoPt> pts, int alt)
        {
            GotoFail gf;
            _gotoFail.TryGetValue(uid, out gf);
            if (gf != null && _now - gf.At < GotoFailGapS()) return "gated";
            string err = _game.Goto(uid, pts, alt);
            if (err == null) { _gotoFail.Remove(uid); return null; }
            if (gf == null) _gotoFail[uid] = gf = new GotoFail();
            else gf.Repeats++;
            gf.At = _now; gf.Err = err;
            _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " uid=" + I(uid) + " goto failed: " + err + (gf.Repeats > 0 ? " repeats=" + I(gf.Repeats) : ""));
            if (err == "not_controllable") { _badUntil[uid] = _now + GotoFailGapS(); ReleaseRuns(null, uid, "not_controllable"); }
            return err;
        }

        /// <summary>The shooter cannot go on: the second number takes the target, else it waits for a new assignment (the strike group asks "need_shooter").</summary>
        private void LoseShooter(TargetRec r, string why, bool penalty)
        {
            int old = r.Shooter;
            if (penalty && old != 0) _badUntil[old] = _now + 120;
            r.Retried = false;
            if (r.Second != 0 && Armed(LiveOf(r.Second))) { r.Shooter = r.Second; r.Second = 0; r.Role = "shooter"; r.AssignedS = _now; _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " second uid=" + I(r.Shooter) + " takes over (" + why + ")"); SetState(r, "approach", why); return; }
            r.Shooter = 0; r.Second = 0;
            SetState(r, "idle", why);
            _game.Event("asw_strike_need_shooter", Obj("group", r.Group, "contact", r.Id, "reason", why));
        }

        // ---- positions -----------------------------------------------------------------------------------------------

        private double DropRange(int uid) { return AswLaneMath.DropRangeNm(_cfg.Loc.DropNm, _game.DropSpec(uid)); }

        private int DropAlt(int uid) { return AswLaneMath.DropAltFt(_game.DropSpec(uid)); }

        private static double SpookR(AswLocParams p) { return AswLocalizeMath.SpookR(p.SubSpookNm, p.SpookMarginNm); }

        /// <summary>A point at distance nm from the datum on the side of the aircraft (the datum itself when the aircraft position is unknown).</summary>
        private static GeoPt PointToward(GeoPt datum, AswMember m, double nm)
        {
            if (m.Lat == 0 && m.Lon == 0) return datum;
            double brg = AswMath.BearingDeg(datum, new GeoPt(m.Lat, m.Lon));
            return AswMath.Offset(datum, brg, nm);
        }

        /// <summary>T-331: the waiting point of an aircraft is anchored on the first bearing (AswHoldMath): it is ordered again only when the datum moved,
        /// or after a minute when the aircraft drifted away; a circling aircraft is not a reason (each reached point is a "We're on position" message of the game).</summary>
        private void GotoThrottled(TargetRec r, int uid, GeoPt datum, AswMember m, double nm, int alt)
        {
            double[] last;
            bool first = !_waitAt.TryGetValue(uid, out last) || last.Length < 6;
            bool known = !(m.Lat == 0 && m.Lon == 0);
            var air = known ? new GeoPt(m.Lat, m.Lon) : datum;
            double moved = first ? 0 : AswMath.DistNm(new GeoPt(last[4], last[5]), datum);
            double toPoint = first || !known ? 0 : AswMath.DistNm(air, new GeoPt(last[1], last[2]));
            if (!AswHoldMath.NeedRegoto(first, first ? 0 : _now - last[0], moved, toPoint, AttackRegotoNm, WaitRegotoS)) return;
            double brg = known ? AswHoldMath.AnchorBrg(first ? (double?)null : last[3], first ? datum : new GeoPt(last[4], last[5]), datum, air, AttackRegotoNm) : 0;
            var p = known ? AswMath.Offset(datum, brg, nm) : datum;
            double kd = known && !first && moved < AttackRegotoNm ? last[4] : datum.Lat;
            double kl = known && !first && moved < AttackRegotoNm ? last[5] : datum.Lon;
            _waitAt[uid] = new[] { _now, p.Lat, p.Lon, brg, kd, kl };
            AttackGoto(r, uid, new List<GeoPt> { p }, alt);
        }

        /// <summary>The second number (and a held shooter) waits outside the spook radius of the datum.</summary>
        private void WaitShooters(TargetRec r)
        {
            var datum = new GeoPt(r.Lat, r.Lon);
            double w = SpookR(_cfg.Loc) + 0.5;
            foreach (int u in new[] { r.Second, r.State == "hold" || r.State == "reacquire" ? r.Shooter : 0 })
            {
                var m = LiveOf(u);
                if (m == null || m.Where != "air" || AirRetreatOf(u)) continue;
                if (AswMath.IsLeaving(m) || GotoGated(u)) continue;   // T-214
                var wg = Find(r.Group);
                if (wg != null && !wg.Members.Contains(u)) continue;
                if (u == r.Second && NearWaitPoint(u, m, datum, w)) continue;
                GotoThrottled(r, u, datum, m, w, DropAlt(u));
            }
        }

        /// <summary>T-331: the second number is on its waiting point (within AswHoldMath.LoiterNm of the anchor) or, before the first order, inside the ring.</summary>
        private bool NearWaitPoint(int uid, AswMember m, GeoPt datum, double w)
        {
            double[] last;
            var air = new GeoPt(m.Lat, m.Lon);
            if (_waitAt.TryGetValue(uid, out last) && last.Length >= 6)
                return AswMath.DistNm(new GeoPt(last[4], last[5]), datum) < AttackRegotoNm && AswMath.DistNm(air, new GeoPt(last[1], last[2])) <= AswHoldMath.LoiterNm;
            return AswMath.DistNm(air, datum) <= w + 0.3;
        }

        // ---- holds ---------------------------------------------------------------------------------------------------

        private double NearestOwnSubNm(TargetRec r, out string name)
        {
            name = null;
            var subs = _game.OwnSubs();
            if (subs == null) return double.NaN;
            double best = double.MaxValue;
            var d = new GeoPt(r.Lat, r.Lon);
            foreach (var s in subs) { double nm = AswMath.DistNm(new GeoPt(s.Lat, s.Lon), d); if (nm < best) { best = nm; name = s.Name; } }
            return best;
        }

        /// <summary>The reason the shot waits now (disabled | air_threat | own_sub | stale_fix) or null. The neutral guard is asked by the attack itself.</summary>
        private string HoldReason(TargetRec r, AswContact c, Grp g, AswMember m)
        {
            if (!_game.AllowAttack) return "disabled";
            var ag = AirGuard;
            if ((g != null && ag != null && ag.P.Mode == "retreat" && ag.GroupStage("asw", g.Name) == AirStage.Retreat) || AirRetreatOf(m.Uid)) return "air_threat";
            string nm; double near = NearestOwnSubNm(r, out nm);
            r.OwnSubName = nm; r.OwnSubNm = near;
            if (AswStrikeMath.OwnSubHold(ref r.Latch, near, _cfg.Strike.OwnSubSafeNm, _cfg.Strike.OwnSubClearS, _now)) return "own_sub";
            if (AswStrikeMath.NeedFreshFix(r.MissStreak, _cfg.Strike.MissStreak, FixAge(r, c), _cfg.Strike.FreshFixS) && _now - r.EndS < StaleFixGiveUpS + _cfg.Strike.ReattackGapS) return "stale_fix";
            return null;
        }

        private void EnterHold(TargetRec r, string why, string arg = null)
        {
            if (r.State == "hold" && r.Hold == why) return;
            bool first = r.Hold != why || r.State != "hold";
            r.Hold = why; r.HoldArg = arg; r.HoldS = _now;
            if (r.State != "hold") { r.PrevState = r.State; SetState(r, "hold", why); }
            if (why == "stale_fix") r.WantFix = true;
            if (!first) return;
            _game.Log("asw strike " + r.Group + " hold contact=" + I(r.Id) + " reason=" + why + (why == "own_sub" ? " sub=" + (r.OwnSubName ?? "-") + " nm=" + N(Math.Round(r.OwnSubNm, 1)) : ""));
            _game.Event("asw_strike_hold", Obj("group", r.Group, "contact", r.Id, "reason", why, "sub", why == "own_sub" ? r.OwnSubName : null, "nm", why == "own_sub" && !double.IsNaN(r.OwnSubNm) ? (object)Math.Round(r.OwnSubNm, 1) : null));
            NoteAlert(r.Id, "hold_" + why);
        }

        private void LeaveHold(TargetRec r)
        {
            if (r.State != "hold") return;
            _game.Log("asw strike " + r.Group + " resume contact=" + I(r.Id) + " after_s=" + N(Math.Round(_now - r.HoldS)));
            _game.Event("asw_strike_resume", Obj("group", r.Group, "contact", r.Id, "after_s", Math.Round(_now - r.HoldS)));
            r.WantFix = false; r.Hold = null;
            if (r.Shooter != 0) _waitAt.Remove(r.Shooter);   // T-331
            SetState(r, "approach", "resume");
        }

        // ---- approach ------------------------------------------------------------------------------------------------

        private bool UsesCharge(AswMember m) { var sp = _game.DropSpec(m.Uid); return sp != null && sp.Charge; }

        private void Approach(TargetRec r, AswContact c, AswMember m, Grp g)
        {
            // the weapon: a depth charge only on a fresh MAD fix (ADR-0072 s.4.5)
            if (UsesCharge(m))
            {
                var sp = _game.DropSpec(m.Uid);
                bool groupTorp = false;
                if (g != null) foreach (int u in g.Members) { var o = LiveOf(u); if (o != null && o.Uid != m.Uid && Armed(o) && !UsesCharge(o)) groupTorp = true; }
                double madAge = c != null && c.MadFixS >= 0 ? _now - c.MadFixS : -1;
                if (!AswLaneMath.DepthChargeOk(_cfg.Loc.DepthCharges, groupTorp, madAge, c == null ? double.NaN : c.DepthFt, sp.MaxDepthFt)) { Done(r, "no_torpedo"); return; }
            }
            string hold = HoldReason(r, c, g, m);
            if (hold != null)
            {
                EnterHold(r, hold, null);
                if (hold != "air_threat") GotoThrottled(r, m.Uid, new GeoPt(r.Lat, r.Lon), m, SpookR(_cfg.Loc) + 0.5, DropAlt(m.Uid));
                WaitShooters(r);
                return;
            }
            if (r.State == "hold" && r.Hold != "neutral") LeaveHold(r);
            // the MAD check first (once per target)
            if (r.State == "approach" && !r.Verified && NeedsVerify(r, c, m)) { StartVerify(r, c, m); return; }
            r.Verified = true;
            // the approach flight: to the drop point on the aircraft's side of the estimate, repeated when the estimate moves
            var datum = new GeoPt(r.Lat, r.Lon);
            double dist = m.Lat == 0 && m.Lon == 0 ? 0 : AswMath.DistNm(new GeoPt(m.Lat, m.Lon), datum);
            double drop = DropRange(m.Uid);
            if (_now - r.StateS > _cfg.Loc.ApproachMaxS && r.State == "approach" && _now - r.AssignedS > _cfg.Loc.ApproachMaxS)
            {
                _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " uid=" + I(m.Uid) + " state approach reason=approach_timeout");
                LoseShooter(r, "approach_timeout", true); return;
            }
            if (dist > drop + 0.3)
            {
                if (AswMath.DistNm(new GeoPt(r.GLat, r.GLon), datum) > AttackRegotoNm || _now - r.GotoS >= AttackRegotoS)
                {
                    var pt = PointToward(datum, m, drop);
                    _waitAt.Remove(m.Uid);   // T-331: the next wait takes a fresh bearing
                    AttackGoto(r, m.Uid, new List<GeoPt> { pt }, DropAlt(m.Uid));
                    r.GotoS = _now; r.GLat = r.Lat; r.GLon = r.Lon;
                }
                WaitShooters(r);
                return;
            }
            WaitShooters(r);
            if (_now < r.NextTryS) return;
            if (TorpOnTarget(r)) { if (!r.InWaterNoted) { r.InWaterNoted = true; _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " waits: a torpedo is already in the water"); } return; }
            r.InWaterNoted = false;
            if (r.State == "hold") { /* neutral hold: the attack below is the retry */ }
            r.NextTryS = _now + AttackRetryS;
            var cap = _cfg.Strike.MaxTorpedoes;
            if (cap > 0 && r.Torps >= cap) { Done(r, "limit"); return; }
            if (r.Unknown && !_cfg.Strike.Targets.Equals("hostile_unknown")) { Done(r, "not_target"); return; }
            var ks = new HashSet<int>();
            var tw = _game.TorpedoesInWater();
            if (tw != null) foreach (var t in tw) ks.Add(t.Id);
            int before = m.Torpedoes;
            string err = _game.AttackTrack(m.Uid, r.Id, r.Unknown);
            if (err == null)
            {
                r.KnownTorps = ks; r.TorpBefore = before; r.EngagedS = _now;
                if (r.State == "hold") { LeaveHold(r); }
                SetState(r, "engaged", null);
                return;
            }
            if (err == "out_of_range") return;   // keep flying: the next tick moves closer
            if (err == "neutral_near") { EnterHold(r, "neutral"); return; }
            _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " uid=" + I(m.Uid) + " attack refused: " + err);
            _game.Event("asw_attack_result", Obj("group", r.Group, "contact", r.Id, "ok", false, "error", err, "track", r.Name, "aircraft", m.Name));
            LoseShooter(r, err, true);
        }

        /// <summary>One torpedo in the water per target: the group's own, or any torpedo of the player that runs at this track.</summary>
        private bool TorpOnTarget(TargetRec r)
        {
            var tw = _game.TorpedoesInWater();
            if (tw == null) return false;
            foreach (var t in tw) if (t.TargetId == r.Id || (r.TorpId != 0 && t.Id == r.TorpId)) return true;
            return false;
        }

        // ---- MAD check -----------------------------------------------------------------------------------------------

        private bool NeedsVerify(TargetRec r, AswContact c, AswMember m)
        {
            if (!_cfg.Loc.MadVerify || c == null || !m.Mad) return false;
            if (c.MadFixS == AswContact.MadNotRead) { if (!r.StaleLogged) { r.StaleLogged = true; _game.Log("asw mad " + r.Group + " contact=" + I(r.Id) + " pass result=skipped (no MAD detection reading)"); } return false; }
            if (c.MadFixS >= 0 && _now - c.MadFixS < 60) return false;   // a fresh MAD fix is there already
            return true;
        }

        private void StartVerify(TargetRec r, AswContact c, AswMember m)
        {
            var datum = new GeoPt(r.Lat, r.Lon);
            var legs = AswLaneMath.MadVerifyLegs(new NmPt(0, 0), c.CourseDeg, 1.0, 0.15);
            var pts = ToGeoList(datum, 0, legs);
            r.VerifyS = _now;
            SetState(r, "verify", null);
            string err = AttackGoto(r, m.Uid, pts, _cfg.Loc.MadVerify ? AswLaneMath.MadPassFt(_cfg.Loc.MadPassFt, 0) : 0);   // T-214: a real error is logged by AttackGoto
            _game.Log("asw mad " + r.Group + " contact=" + I(r.Id) + " uid=" + I(m.Uid) + " pass start legs=" + I(pts.Count) + " alt_ft=" + N(_cfg.Loc.MadPassFt) + " limit_s=" + N(_cfg.Loc.MadVerifyS) + (err == "gated" ? " goto gated" : ""));
        }

        private void Verify(TargetRec r, AswContact c, AswMember m)
        {
            if (m == null) { LoseShooter(r, "shooter_gone", false); return; }
            if (c != null && c.MadFixS >= r.VerifyS)
            {
                _game.Log("asw mad " + r.Group + " contact=" + I(r.Id) + " uid=" + I(m.Uid) + " pass result=fix elapsed_s=" + N(Math.Round(_now - r.VerifyS)));
                r.FixKind = "mad"; r.Verified = true; SetState(r, "approach", "mad_fix"); return;
            }
            if (_now - r.VerifyS >= _cfg.Loc.MadVerifyS)
            {
                _game.Log("asw mad " + r.Group + " contact=" + I(r.Id) + " uid=" + I(m.Uid) + " pass result=none elapsed_s=" + N(Math.Round(_now - r.VerifyS)));
                r.Verified = true; SetState(r, "approach", "mad_none");
            }
        }

        // ---- engaged -> in the water -> assessment -----------------------------------------------------------------

        private void Engaged(TargetRec r, AswContact c, AswMember m)
        {
            bool dropped = m != null && m.Torpedoes < r.TorpBefore;
            int tid = 0;
            var tw = _game.TorpedoesInWater();
            if (tw != null) foreach (var t in tw) if (t.LauncherUid == r.Shooter && !r.KnownTorps.Contains(t.Id)) { dropped = true; tid = t.Id; }
            if (dropped)
            {
                r.Torps++; r.TorpId = tid; r.DropS = _now; r.HitsBefore = _game.HitsOn(r.Id); r.Retried = false;
                _hitAt[r.Id] = _now;
                double d = m == null ? 0 : AswMath.DistNm(new GeoPt(m.Lat, m.Lon), new GeoPt(r.Lat, r.Lon));
                _game.Log("asw attack " + r.Group + " drop contact=" + I(r.Id) + " uid=" + I(r.Shooter) + " n=" + I(r.Torps) + (_cfg.Strike.MaxTorpedoes > 0 ? "/" + I(_cfg.Strike.MaxTorpedoes) : "")
                    + " range_nm=" + N(Math.Round(d, 2)) + " alt_ft=" + I(DropAlt(r.Shooter)) + " fix=" + r.FixKind + " age_s=" + N(Math.Round(Math.Max(0, FixAge(r, c)))));
                _game.Event("asw_attack_drop", Obj("group", r.Group, "contact", r.Id, "uid", r.Shooter, "n", r.Torps, "range_nm", Math.Round(d, 2), "fix", r.FixKind));
                r.FixKind = "live";
                SetState(r, "in_water", null);
                return;
            }
            if (_now - r.EngagedS < _cfg.Loc.DropTimeoutS) return;
            _game.Log("asw attack " + r.Group + " contact=" + I(r.Id) + " uid=" + I(r.Shooter) + " no drop in " + N(_cfg.Loc.DropTimeoutS) + " s");
            _game.Event("asw_attack_result", Obj("group", r.Group, "contact", r.Id, "ok", false, "error", "not_released", "track", r.Name));
            if (!r.Retried) { r.Retried = true; SetState(r, "approach", "retry"); return; }
            LoseShooter(r, "not_released", true);
        }

        private void InWater(TargetRec r, AswContact c, AswMember m)
        {
            var tw = _game.TorpedoesInWater();
            if (tw != null && r.TorpId == 0 && _now - r.DropS < 15)
                foreach (var t in tw) if (t.LauncherUid == r.Shooter && !r.KnownTorps.Contains(t.Id)) r.TorpId = t.Id;
            var sp = _game.DropSpec(r.Shooter);
            double run = sp != null && sp.RunS > 0 ? sp.RunS : 600;
            bool gone = false;
            if (tw != null && r.TorpId != 0) { gone = true; foreach (var t in tw) if (t.Id == r.TorpId) gone = false; }
            if (!gone && _now - r.DropS < run) { WaitShooters(r); return; }
            r.EndS = _now;
            SetState(r, "assess", null);
        }

        private void Assess(TargetRec r, AswContact c)
        {
            int now = _game.HitsOn(r.Id);
            string res = now < 0 || r.HitsBefore < 0 ? "unknown" : now > r.HitsBefore ? "hit" : "miss";
            if (res == "hit") { r.Hits += now - r.HitsBefore; r.MissStreak = 0; } else r.MissStreak++;
            _game.Log("asw attack " + r.Group + " result contact=" + I(r.Id) + " n=" + I(r.Torps) + " result=" + res + " hits=" + I(r.Hits));
            _game.Event("asw_attack_result", Obj("group", r.Group, "contact", r.Id, "ok", true, "n", r.Torps, "result", res, "track", r.Name));
            if (_cfg.Strike.MaxTorpedoes > 0 && r.Torps >= _cfg.Strike.MaxTorpedoes) { Done(r, "limit"); return; }
            // alive: wait the gap (the point refreshes), then the same shooter again
            _game.Log("asw attack " + r.Group + " reattack contact=" + I(r.Id) + " in_s=" + N(_cfg.Strike.ReattackGapS) + " miss_streak=" + I(r.MissStreak));
            r.Verified = true;
            SetState(r, "wait", "reattack_gap");
        }

        /// <summary>Restart after "Attack again" / the limit was lifted.</summary>
        internal void ResumeRun(int contactId) { TargetRec r; if (_targets.TryGetValue(contactId, out r) && r.Done == "limit") _targets.Remove(contactId); }
    }
}
