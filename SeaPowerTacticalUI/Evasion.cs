using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-107 E: own torpedo evasion of the interface mod (customer decision: default; native TorpedoEvasion is the fallback flag
    // [AutoEvade] Mode=native). Implements AutoEvadeHook declared in Common/ (ReleaseEvasionUnit is a release participant). The voice mod has none of this.
    // Decisions use only what the player knows: torpedo tracks of the plotting table or the torpedo the game announced.
    // Free of HarmonyLib / UnityEngine. The config binding (EvasionMode, EvasionDebug) is done by the integrator (UiPlugin).
    internal sealed partial class GameApi
    {
        /// <summary>own (default) | native.</summary>
        internal string EvasionMode = "own";
        internal bool EvasionDebug;
        private const double EvasionLegNm = 20, EvasionTickS = 1.0;

        private sealed class EvRun
        {
            public EvState S = new EvState();
            public double NextTick, LastCourseSent = -999;
            public double[] PrevTorp;           // lat, lon of the torpedo at the previous tick
            public double LastDist = double.MaxValue;
            public bool WasFlagOn;
            public bool Holding;                // T-203: only frozen live tracks are known, the evasion holds the last course (logged once)
            public double LostNearLoggedS = double.NaN;   // T-280: NearSeenS for which the "hold reason=lost_near" line was written
            public bool NearLogged;             // T-268: the "hold reason=near" line was written
            public readonly HashSet<int> ExpiredLogged = new HashSet<int>();   // T-268 R1: tracks whose "hold end reason=pursuit_expired" line was written
            public bool Slowed; public int SlowTg;   // T-224: slow run for the sonar (only frozen tracks for SonarSlowAfterS)
            public readonly EvGuardState Guard = new EvGuardState();   // T-272 (ADR-0108): terrain guard of the course
            public int GuardChecks, GuardProbes;      // T-272: cost counters for the log
            public bool DecoyHoldLogged, DecoyEmptyLogged, DecoyClosingLogged, DecoyFrozenLogged, DecoyWakeLogged;   // T-286: once-per-run log flags of the decoy rule; T-301: wake
        }

        private readonly Dictionary<object, EvRun> _evRuns = new Dictionary<object, EvRun>(new RefEq());
        // T-215: per unit, the live tracks at the end of the last evasion (a track may start it again only 0.5 nm closer or within HoldNm)
        private readonly Dictionary<object, EvRearm> _evRearm = new Dictionary<object, EvRearm>(new RefEq());
        private readonly HashSet<string> _evRearmLogged = new HashSet<string>();
        private readonly HashSet<int> _evImmLogged = new HashSet<int>();   // T-266: uids whose "skip immobile" line was written

        /// <summary>T-130: forget evasion runs of the mission that was left (they hold its units).</summary>
        internal void EvasionLeaveMission()
        {
            _evRuns.Clear(); _evRearm.Clear(); _evRearmLogged.Clear();
            _evClosing.Clear(); _evFresh.Clear(); _evWake.Clear(); _evPursuit.Clear(); _evLastT = double.NaN;
            EvasionHeightsClear();   // T-272
            EvasionReturnClear();   // T-278
            EvasionHelmClear();   // T-279
            EvasionDecoyClear();   // T-286
            EvasionSaveLeave();   // T-256
            _evImmLogged.Clear(); ShipMobilityLeave();   // T-266
            var sub = SubEvasion;
            if (sub != null) { try { sub.LeaveMission(); } catch { } }   // T-101
        }

        internal bool EvasionActive(object u)
        {
            if (u == null) return false;
            if (_evRuns.ContainsKey(u)) return true;
            var sub = SubEvasion;   // T-101: a submarine run
            return sub != null && sub.Holds(UidOf(u));
        }

        partial void AutoEvadeHook(object u, double now, ref bool handled)
        {
            // T-101 / ADR-0071: the submarine is always taken here (any [AutoEvade] Mode), so Common never sets the native state for it without a restore
            if (UnitKind4(u) == "sub")
            {
                handled = true;
                try { SubEvasionTick(u); }
                catch (Exception e) { try { Log("[Evasion] sub tick failed uid=" + UidOf(u) + ": " + e.GetType().Name + ": " + e.Message); } catch { } }
                return;
            }
            if ((EvasionMode ?? "own").ToLowerInvariant() == "native") return;
            handled = true;
            try { EvasionResumeTick(u, now); } catch { }   // T-256
            try { EvasionReturnTick(u, now); } catch { }   // T-278 (ADR-0111): follows the detour round the coast after an evasion
            try { EvasionHelmFollowTick(u, now); } catch { }   // T-279 (ADR-0112): leads the ship to its route while the game's Air Ops last
            try { EvasionTick(u, now); }
            catch (Exception e) { try { Log("[Evasion] tick failed uid=" + UidOf(u) + ": " + e.GetType().Name + ": " + e.Message); } catch { } }
            try { AtwCoverTick(u, now); } catch { }
        }

        /// <summary>"Return control" participant (T-107): releases the unit from the own torpedo evasion.</summary>
        internal void ReleaseEvasionUid(int uid)
        {
            var sub = SubEvasion;
            if (sub != null && sub.Holds(uid))
            {
                try { sub.Release(uid); } catch (Exception e) { try { Log("[Evasion] sub release failed uid=" + uid + ": " + e.GetType().Name); } catch { } }
            }
            object key = null;
            foreach (var k in _evRuns.Keys)
            {
                int ku = 0;
                try { ku = UidOf(k); } catch { }
                if (ku == uid) { key = k; break; }
            }
            foreach (var k in new List<object>(_evRearm.Keys))
            {
                int ku = 0;
                try { ku = UidOf(k); } catch { }
                if (ku == uid) _evRearm.Remove(k);
            }
            AtwTurnEnd(uid, "released");   // T-230
            EvasionHelmReleaseUid(uid, "released");   // T-279
            if (key == null) return;
            _evRuns.Remove(key);
            try { Log("[Evasion] evasion released uid=" + uid + " (restore_control)"); } catch { }
        }

        // self-check: EvasionSpecs (evasion_own: members + auto_evade + Mode=own; evasion_atw: T-141)

        /// <summary>T-136 E4: " ttg=~65s" for the log line when the contact_info module knows the torpedo's time to go (log only; the evasion logic never reads it).</summary>
        private string TtgTag(int trackId)
        {
            try
            {
                var row = trackId < 0 || ContactInfo == null ? null : ContactInfo.Get(trackId);
                return row == null || double.IsNaN(row.TtgS) ? "" : " ttg=~" + Math.Round(row.TtgS) + "s";
            }
            catch (Exception) { return ""; }
        }

        // T-193 (ADR-0083): robust closing and frozen-track state of every torpedo track, per own unit with auto_evade on
        private readonly TrackClosing _evClosing = new TrackClosing();
        private readonly TrackFreshness _evFresh = new TrackFreshness();
        private readonly TrackPursuit _evPursuit = new TrackPursuit();   // T-268 (ADR-0107): where a torpedo with a frozen track may be now
        private readonly Dictionary<int, bool> _evWake = new Dictionary<int, bool>();
        private double _evLastT = double.NaN;

        // T-272 (ADR-0108): heights for the course guard. Own cache (the intercept one is cleared only while that module is on); main thread only (ADR-0092).
        private TerrainHeightCache _evHeights;

        private void EvasionHeightsClear()
        {
            if (_evHeights != null) _evHeights.Clear();
            _evHeights = null;
        }

        private double EvasionHeightM(GeoPt g)
        {
            if (_evHeights == null)
            {
                var sampler = TerrainSampler(false);
                if (sampler == null) return TerrainHeightCache.OpenSeaM;   // no terrain data yet: open sea, tried again at the next call
                _evHeights = new TerrainHeightCache(sampler, null, m => Log("[Evasion] " + m));   // T-275 integration
            }
            return _evHeights.HeightM(g);
        }

        /// <summary>
        /// T-272 (ADR-0108): the course want is checked 5 nm ahead for land and shoals (EvasionCourseGuard); a blocked one is replaced by the nearest clear course
        /// with the torpedo astern. Terrain is read only when a check is due. Sets run.S.Side / CourseDeg for a replaced course; returns the course to steer.
        /// </summary>
        private double EvasionGuardCourse(object u, EvRun run, double now, double lat0, double lon0, double want, double hdg, EvTorp t, double? tc, IList<EvTorp> others)
        {
            try
            {
                List<double> ob = null;
                if (others != null)
                    foreach (var o in others)
                        if (o != null && o.TrackId != t.TrackId && !o.Stale && o.DistNm <= EvasionMath.OtherNm) { if (ob == null) ob = new List<double>(); ob.Add(o.BrgDeg); }
                double? prevGuard = run.Guard.GuardDeg;
                int missBefore = EvasionSamples();   // T-278 (N3)
                EvGuardPick p = EvasionCourseGuard.Steer(run.Guard, now, new GeoPt(lat0, lon0), want, hdg, t.BrgDeg, tc, ob, EvasionHeightM);
                int samples = EvasionSamples() - missBefore;
                if (p.Checked) { run.GuardChecks++; run.GuardProbes += p.Probes; }
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (p.Why != "clear")
                {
                    run.S.Side = EvasionMath.Diff(t.BrgDeg, p.CourseDeg) >= 0 ? 1 : -1;
                    run.S.CourseDeg = p.CourseDeg;
                    if (p.Changed)
                        Log("[Evasion] evasion course land uid=" + UidOf(u) + " torp=" + t.TrackId + " want=" + Math.Round(want).ToString("000", inv) + " course=" + Math.Round(p.CourseDeg).ToString("000", inv) +
                            " why=" + p.Why + " block_nm=" + p.BlockNm.ToString("0.00", inv) + " alt=" + (double.IsNaN(p.BlockAltM) ? "na" : Math.Round(p.BlockAltM).ToString("0", inv)) +
                            " rel=" + Math.Round(p.RelDeg).ToString("0", inv) + " probes=" + p.Probes + " samples=" + samples + " checks=" + run.GuardChecks + " total_probes=" + run.GuardProbes);
                }
                else if (p.Changed && prevGuard.HasValue && Math.Abs(EvasionMath.Diff(prevGuard.Value, want)) >= 3)
                    Log("[Evasion] evasion course clear uid=" + UidOf(u) + " want=" + Math.Round(want).ToString("000", inv) + " probes=" + p.Probes + " checks=" + run.GuardChecks + " total_probes=" + run.GuardProbes);
                else if (EvasionDebug && p.Checked)
                    Log("[Evasion] evasion course check uid=" + UidOf(u) + " clear probes=" + p.Probes + " checks=" + run.GuardChecks + " total_probes=" + run.GuardProbes);
                return p.CourseDeg;
            }
            catch (Exception ex) { Log("[Evasion] evasion course guard error uid=" + UidOf(u) + " " + ex.Message); return want; }
        }

        private void EvasionTick(object u, double realNow)
        {
            // T-193 A1: every timer of the evasion runs on mission time (the pump is real time, x2..x8 would shorten 300 s to 40 s of the game)
            double? mt = Frame.MissionTimeS;
            if (!mt.HasValue) return;
            double now = mt.Value;
            if (!double.IsNaN(_evLastT) && now < _evLastT - 0.5) { _evRuns.Clear(); _evRearm.Clear(); _evRearmLogged.Clear(); _evClosing.Clear(); _evFresh.Clear(); _evWake.Clear(); _evPursuit.Clear(); EvasionHeightsClear(); EvasionReturnClear(); EvasionHelmClear(); EvasionDecoyClear(); }   // a save was loaded
            _evLastT = now;

            EvRun run;
            bool active = _evRuns.TryGetValue(u, out run);
            if (active && now < run.NextTick) return;
            if (!active && !ResolveFlag(u, "auto_evade")) return;

            // T-266 (ADR-0106): a ship that cannot move does not start an evasion; a running one ends
            string imm = ShipImmobileWhy(u);
            if (imm == null) _evImmLogged.Remove(UidOf(u));
            else if (!active)
            {
                if (_evImmLogged.Add(UidOf(u))) Log("[Evasion] evasion skip uid=" + UidOf(u) + " reason=immobile why=" + imm);
                return;
            }

            double lat0, lon0, h;
            if (!OwnPos(u, out lat0, out lon0, out h)) { if (active) { _evRuns.Remove(u); AtwTurnEnd(UidOf(u), "evasion_end"); EvasionHelmRelease(u, "off"); } return; }
            double hdg = GridHeadingTrue(u, EvasionSpecs.ObjHeading) ?? 0;

            Dictionary<int, double[]> poses;
            List<EvTorp> torps = KnownTorpedoes(u, lat0, lon0, now, out poses);
            if (active && imm != null)
            {
                Log("[Evasion] evasion end uid=" + UidOf(u) + " reason=immobile why=" + imm);
                EvasionFinish(u, now, torps, "immobile");
                return;
            }
            var fresh = new List<EvTorp>();
            EvTorp nearest = null, nearestAny = null;
            double nearestLiveNm = double.PositiveInfinity;   // T-203: every live track counts, frozen ones too (last known position)
            double nearestFrozenNm = double.PositiveInfinity;   // T-224: the nearest frozen live track (T-268: its ThreatDist, mark or pursuit point)
            double nearestThreatNm = double.PositiveInfinity;   // T-268: the smallest ThreatDist of all live tracks
            EvTorp nearestThreat = null;                        // T-286: the track behind nearestThreatNm
            bool anyWake = false;                               // T-268: a live (not expired) track is wake homing
            foreach (var q in torps)
            {
                if (q.DistNm < nearestLiveNm) { nearestLiveNm = q.DistNm; nearestAny = q; }
                double thr268 = EvasionMath.ThreatDist(q);
                if (q.Stale && thr268 < nearestFrozenNm) nearestFrozenNm = thr268;
                if (thr268 < nearestThreatNm) { nearestThreatNm = thr268; nearestThreat = q; }
                if (q.WakeHoming && !double.IsPositiveInfinity(thr268)) anyWake = true;
                if (!q.Stale) { fresh.Add(q); if (nearest == null || q.DistNm < nearest.DistNm) nearest = q; }
            }

            if (!active)
            {
                var thr = Get(u, EvasionSpecs.ObjThreatingTorpedo);
                bool gameThreat = thr != null && IsAlive(thr);
                EvRearm rearm;
                _evRearm.TryGetValue(u, out rearm);
                EvasionMath.RearmForget(rearm, torps);
                var st = EvasionMath.StartTrack(gameThreat, fresh, rearm);
                if (st == null)
                {
                    // T-215: log once per (unit, track) when only the re-arm rule held the start back
                    if (rearm != null && EvasionMath.ShouldStart(gameThreat, nearest) && _evRearmLogged.Add(UidOf(u) + ":" + nearest.TrackId))
                    {
                        double endNm; rearm.EndDistNm.TryGetValue(nearest.TrackId, out endNm);
                        Log("[Evasion] evasion rearm wait uid=" + UidOf(u) + " torp=" + nearest.TrackId + " dist=" + nearest.DistNm.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                            " end_nm=" + endNm.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    return;
                }
                // T-221: why this start happened (by=game: only the game's flag, the own rule alone would not start it)
                Log("[Evasion] evasion start uid=" + UidOf(u) + " torp=" + st.TrackId + " dist=" + st.DistNm.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                    " by=" + (gameThreat && !EvasionMath.ShouldStart(false, st) ? "game" : "own") + " rearm=" + EvasionMath.RearmTag(rearm, st) +
                    " closing=" + (st.ClosingKn.HasValue ? st.ClosingKn.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "na"));
                _evRearm.Remove(u);
                _evRearmLogged.Clear();
                EvasionHelmFollowDrop(u);   // T-279: a follower of the previous evasion ends
                run = new EvRun { WasFlagOn = true };
                run.S.Phase = "evading"; run.S.StartedS = now; run.S.LastSeenS = now;
                _evRuns[u] = run;
                TacticsEnsureMission();
                _ctlMem.RememberRoute(u, PlayerWaypoints(u), DemandedCourse(u));
                EvasionReturnDrop(u, "evasion");   // T-278: a running detour of the previous evasion ends; its points are not the player's route
                RememberFields(u, new[] { "telegraph" });
                EvLine("auto_evade", "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"phase\":\"start\",\"torpedo_track_id\":" + (st.TrackId >= 0 ? st.TrackId.ToString() : "null") + ",\"mode\":\"own\"}");
            }
            run.NextTick = now + EvasionTickS;
            EvasionMath.NoteThreat(run.S, now, torps);   // T-280 (ADR-0113): the last close threat is remembered before any end rule
            bool? frozenEnd = null;   // T-273: the end rule evaluated in the frozen-tracks branch (before the slow command)

            if (!ResolveFlag(u, "auto_evade")) { _evRuns.Remove(u); AtwTurnEnd(UidOf(u), "released"); EvasionHelmRelease(u, "off"); Log("[Evasion] evasion stopped uid=" + UidOf(u) + " (auto_evade turned off)"); return; }

            if (fresh.Count > 0)
            {
                bool changed;
                EvTorp tp = EvasionMath.PickPrimary(fresh, run.S, out changed);
                double[] tpos;
                poses.TryGetValue(tp.TrackId, out tpos);
                if (changed) { run.PrevTorp = null; run.S.Side = 0; }   // ADR-0083 section 5: a new primary track forgets the side (PickPrimary already stored PrimaryId, CourseAwayMulti cannot see the change)
                EvasionAtwStep(u, lat0, lon0, tp, tpos);   // T-141: anti-torpedo salvo before the turn (ADR-0048 section 5)
                run.S.LastSeenS = now; run.LastDist = tp.DistNm; run.S.LastDistNm = nearest.DistNm; run.Holding = false;
                if (run.Slowed)
                {
                    run.Slowed = false;
                    Log("[Evasion] evasion flank uid=" + UidOf(u) + " reason=fresh");
                }
                double? tc = null;
                if (run.PrevTorp != null && tpos != null && WorldDistanceNm(run.PrevTorp[0], run.PrevTorp[1], tpos[0], tpos[1]) >= 0.02)
                    tc = WorldBearing(run.PrevTorp[0], run.PrevTorp[1], tpos[0], tpos[1]);
                if (tpos != null) run.PrevTorp = tpos;
                double course = EvasionMath.CourseAwayMulti(hdg, tp, tc, fresh, run.S);
                course = EvasionGuardCourse(u, run, now, lat0, lon0, course, hdg, tp, tc, fresh);   // T-272 (ADR-0108): not onto land or a shoal
                double? turnOv = AtwTurnOverride(u);   // T-230 (ADR-0095): a temporary heading to bring the torpedo into an RBU arc; the evasion state is not touched
                if (turnOv.HasValue) course = turnOv.Value;
                bool turned = false, tgSet = false, decoy = false;
                if (Math.Abs(EvasionMath.Diff(run.LastCourseSent, course)) >= 3)
                {
                    double lat, lon;
                    WorldDestination(lat0, lon0, course, EvasionLegNm, out lat, out lon);
                    string wp = PlaceWaypoint("0", u, lat, lon, false, "");
                    if (wp.Contains("\"ok\":true")) { run.LastCourseSent = course; if (!turnOv.HasValue) run.S.CourseDeg = course; turned = true; _ctlMem.NoteRoutePlaced(u, lat, lon); }
                }
                if (Telegraph(u) != 5)
                {
                    tgSet = EvasionTelegraph(u, 5);
                }
                decoy = EvasionDecoyStep(u, run, tp.TrackId, now, tp.DistNm, tp.WakeHoming, EvasionMath.Tti(tp), "fresh");   // T-286 (ADR-0116): reserve for the close range
                if (EvasionDebug || turned || tgSet || decoy)
                    Log("[Evasion] evasion tick uid=" + UidOf(u) + " torp=" + tp.TrackId + " brg=" + Math.Round(tp.BrgDeg) + " dist=" + Math.Round(tp.DistNm, 2) +
                        " hdg=" + Math.Round(hdg) + " course=" + Math.Round(course) + " side=" + run.S.Side + " turned=" + turned + " flank=" + tgSet + " noisemaker=" + decoy + " phase=" + run.S.Phase +
                        " n=" + fresh.Count + " primary_changed=" + (changed ? "true" : "false") + (tp.WakeHoming ? " wake=true" : "") + TtgTag(tp.TrackId));
            }
            else if (torps.Count > 0)
            {
                // T-203 (ADR-0087): only frozen live tracks. They hold the evasion (EvasionMath.Done, up to HoldStaleMaxS): keep the last course at full speed
                run.S.LastDistNm = nearestLiveNm;
                frozenEnd = EvasionMath.Done(run.S, now, null, false, nearestLiveNm, nearestFrozenNm);   // T-273: the end is decided before the slow command (no "slow" in the "restoring" tick)
                double[] apos;
                poses.TryGetValue(nearestAny.TrackId, out apos);
                EvasionAtwStep(u, lat0, lon0, nearestAny, apos);   // the salvo rule (fresh, or a track no older than AtwStaleFireMaxS) is checked inside the allocation
                if (run.S.CourseDeg.HasValue) EvasionGuardCourse(u, run, now, lat0, lon0, run.S.CourseDeg.Value, hdg, nearestAny, null, null);   // T-272: the held course is re-checked, AtwTurnSendFrozen sends the new one
                AtwTurnSendFrozen(u, run, lat0, lon0);              // T-230: the turn onto the salvo works on the slow run too
                if (!frozenEnd.Value && EvasionMath.SonarSlowNow(run.S, now, nearestThreatNm, anyWake, run.Slowed))
                {
                    // T-224 (ADR-0091): slow down so that the own sonar hears the torpedo again; the course is kept
                    if (!run.Slowed)
                    {
                        double[] steps = EvasionSteps(u);
                        double cav = EvasionCavOnsetKn(u);
                        int tg = EvasionMath.SonarSlowTelegraph(steps, cav);
                        if (EvasionTelegraph(u, tg))
                        {
                            run.Slowed = true; run.SlowTg = tg;
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            double kn = steps != null && steps.Length >= 6 ? steps[tg + 1] : double.NaN;
                            Log("[Evasion] evasion slow uid=" + UidOf(u) + " reason=frozen tg=" + tg + " kn=" + (double.IsNaN(kn) ? "na" : kn.ToString("0.#", inv)) +
                                " cav_kn=" + (double.IsNaN(cav) ? "na" : cav.ToString("0.#", inv)) + " since_fresh=" + Math.Round(now - run.S.LastSeenS) +
                                " dist=" + nearestLiveNm.ToString("0.00", inv) + " threat=" + nearestThreatNm.ToString("0.00", inv));
                        }
                    }
                    else if (Telegraph(u) != run.SlowTg)
                    {
                        EvasionTelegraph(u, run.SlowTg);   // T-293: noted too (after Air Ops the game's onExit changed it)
                    }
                }
                else if (!frozenEnd.Value)
                {
                    if (run.Slowed)
                    {
                        run.Slowed = false;
                        Log("[Evasion] evasion flank uid=" + UidOf(u) + " reason=" + (anyWake ? "wake" : EvasionMath.LostNearHolds(run.S, now) ? "lost_near" : "close"));
                    }
                    if (Telegraph(u) != 5)
                    {
                        EvasionTelegraph(u, 5);
                    }
                }
                if (nearestThreat != null && nearestThreatNm <= EvasionDecoyMath.CloseNm && !frozenEnd.Value)
                    EvasionDecoyStep(u, run, nearestThreat.TrackId, now, nearestThreatNm, nearestThreat.WakeHoming, nearestThreat.FrozenTtiS, "frozen");   // T-286: close band only; T-297: residual tti of the last moving mark
                if (!run.Holding)
                {
                    run.Holding = true;
                    Log("[Evasion] evasion hold uid=" + UidOf(u) + " reason=frozen dist=" + Math.Round(nearestLiveNm, 2) + " n=" + torps.Count);
                }
            }
            else if (EvasionMath.LostNearHolds(run.S, now))
            {
                // T-280 (ADR-0113): no tracks left on the plot, but a close threat was known < LostNearHoldS ago: keep the course at full speed blind
                if (run.S.CourseDeg.HasValue && !double.IsNaN(run.S.NearBrgDeg))
                {
                    var mem = new EvTorp { TrackId = run.S.NearTrackId, BrgDeg = run.S.NearBrgDeg, DistNm = run.S.NearNm, Stale = true };   // synthetic track from memory (the guard dereferences it)
                    EvasionGuardCourse(u, run, now, lat0, lon0, run.S.CourseDeg.Value, hdg, mem, null, null);   // T-272: not onto land or a shoal
                }
                AtwTurnSendFrozen(u, run, lat0, lon0);   // sends the (re-guarded) held course; LastCourseSent feeds the T-279 helm below
                if (run.Slowed) { run.Slowed = false; Log("[Evasion] evasion flank uid=" + UidOf(u) + " reason=lost_near"); }
                if (Telegraph(u) != 5)
                {
                    EvasionTelegraph(u, 5);
                }
            }

            var inv268 = System.Globalization.CultureInfo.InvariantCulture;
            // T-268 R1: a frozen track whose pursuit expired (the predicted intercept passed, or the run endurance is over) no longer holds the evasion
            foreach (var q in torps)
                if (q.Stale && q.PursuitExpired && run.ExpiredLogged.Add(q.TrackId))
                    Log("[Evasion] evasion hold end uid=" + UidOf(u) + " reason=pursuit_expired torp=" + q.TrackId + " dist=" + q.DistNm.ToString("0.00", inv268) + " after=" + Math.Round(now - run.S.StartedS));
            // T-268 (ADR-0107): once per run, the near-threat rule holds the evasion past the old MaxS
            if (!run.NearLogged && now - run.S.StartedS >= EvasionMath.MaxS && nearestThreatNm <= EvasionMath.HoldReleaseNm)
            {
                run.NearLogged = true;
                string src = nearest != null && nearest.DistNm <= EvasionMath.HoldReleaseNm ? "fresh" : (nearestFrozenNm < nearestLiveNm - 1e-9 ? "pursuit" : "frozen");
                Log("[Evasion] evasion hold uid=" + UidOf(u) + " reason=near threat_nm=" + nearestThreatNm.ToString("0.00", inv268) + " src=" + src + " after=" + Math.Round(now - run.S.StartedS));
            }

            // T-280 (ADR-0113): no close live track, but the last close threat is still remembered
            if (now - run.S.StartedS >= EvasionMath.MaxS && nearestThreatNm > EvasionMath.LostNearNm && EvasionMath.LostNearHolds(run.S, now) && run.LostNearLoggedS != run.S.NearSeenS)
            {
                run.LostNearLoggedS = run.S.NearSeenS;
                Log("[Evasion] evasion hold uid=" + UidOf(u) + " reason=lost_near torp=" + run.S.NearTrackId + " last_nm=" + run.S.NearNm.ToString("0.00", inv268) + " lost_s=" + Math.Round(now - run.S.NearSeenS) +
                    " hold_s=" + Math.Round(EvasionMath.LostNearHoldS) + " n=" + torps.Count + " after=" + Math.Round(now - run.S.StartedS));
            }

            if (run.LastCourseSent > -900) EvasionHelmTick(u, now, run.LastCourseSent, hdg);   // T-279 (ADR-0112): helm for the game's Air Ops; the course is already terrain-guarded (ADR-0108)

            if (frozenEnd ?? EvasionMath.Done(run.S, now, nearest, nearest != null, nearestLiveNm, nearestFrozenNm))
            {
                string doneWhy = EvasionMath.DoneWhy(run.S, now, frozenEnd.HasValue ? null : nearest, !frozenEnd.HasValue && nearest != null);   // T-278 (N2)
                run.S.Phase = "restoring";
                Log("[Evasion] evasion tick uid=" + UidOf(u) + " phase=restoring after " + Math.Round(now - run.S.StartedS) + " s last_live_nm=" +
                    (run.S.LastDistNm >= 1e9 ? "na" : Math.Round(run.S.LastDistNm, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)) + " slowed=" + (run.Slowed ? "1" : "0") +
                    " threat_nm=" + (double.IsPositiveInfinity(nearestThreatNm) ? "na" : nearestThreatNm.ToString("0.00", inv268)) +
                    " why=" + doneWhy + " expired=" + run.ExpiredLogged.Count);
                EvasionFinish(u, now, torps, "done");
            }
        }

        /// <summary>T-293: every evasion telegraph write. On ok the live value is noted in ControlMemory (NoteFields), also after a re-set of the slow step;
        /// a deferred write (Air Ops etc.) notes the live (unchanged) one, as before. Returns the ack ok.</summary>
        private bool EvasionTelegraph(object u, int tg)
        {
            string r = TelegraphCmd("0", new Dictionary<string, object> { { "uid", (double)UidOf(u) }, { "telegraph", (double)tg } });
            bool ok = r != null && r.Contains("\"ok\":true");
            if (ok) NoteFields(u, new[] { "telegraph" });
            return ok;
        }

        /// <summary>
        /// The end of an own evasion (T-256: also for a ship restored from the save whose evasion did not resume, torps = null):
        /// restore_control returns the player's route and telegraph, then the auto_evade flag is given back.
        /// </summary>
        private void EvasionFinish(object u, double now, List<EvTorp> torps, string reason)
        {
            var helm = EvasionHelmDetach(u);   // T-279: before restore_control
            var endMem = torps == null ? null : EvasionMath.EndMemory(torps, now);   // T-215
            AtwTurnEnd(UidOf(u), "evasion_end");              // T-230
            EvRun run0; _evRuns.TryGetValue(u, out run0);   // T-293
            _evRuns.Remove(u);
            EvLine("auto_evade", "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"phase\":\"end\",\"torpedo_track_id\":null,\"mode\":\"own\"}");
            bool flagWasOn = ResolveFlag(u, "auto_evade");
            // restore_control also switches the auto_evade flag off: give it back, the evasion should work again next time
            EvasionTelegraphExitNote(u, helm, run0 != null && run0.SlowTg > 0 ? run0.SlowTg : (int?)null);   // T-293
            int? tgPlayer = EvasionPlayerTelegraph(u);   // T-283: before restore_control takes the entry
            string restoreAck = RestoreControlCmd("0", new Dictionary<string, object> { { "uid", (double)UidOf(u) } });
            if (flagWasOn && !ResolveFlag(u, "auto_evade"))
            {
                UnitFlags f;
                if (_flags.TryGetValue(u, out f)) f.AutoEvade = true;
                FlagStoreSetUnit(u, true, null, null);
            }
            if (endMem != null) _evRearm[u] = endMem;   // T-221: after restore_control (its ReleaseEvasionUid clears the memory of the unit)
            if (reason != "immobile") EvasionReturnAfterRestore(u, now, restoreAck);   // T-278 (ADR-0111): the way back is checked against land; never throws
            EvasionHelmAfter(u, helm, now, reason, tgPlayer);   // T-279 (ADR-0112): after the detour; follow | game | clear | none
        }

        /// <summary>T-224: the telegraph speeds of the ship (_telegraphVelocities, index = telegraph + 1); any failure = null.</summary>
        private double[] EvasionSteps(object u)
        {
            try
            {
                var arr = Get(Get(u, EvasionSpecs.VesselParams), EvasionSpecs.VesselSteps) as Array;
                if (arr == null || arr.Length == 0) return null;
                var res = new double[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    var d = D(arr.GetValue(i));
                    if (!d.HasValue) return null;
                    res[i] = d.Value;
                }
                return res;
            }
            catch (Exception) { return null; }
        }

        /// <summary>T-224: the speed (kn) at which the ship starts to cavitate; any failure or a non-vessel = NaN.</summary>
        private double EvasionCavOnsetKn(object u)
        {
            try { return D(Call(u, EvasionSpecs.ObjCavitationOnset)) ?? double.NaN; }
            catch (Exception) { return double.NaN; }
        }

        /// <summary>Wake-homing = an identified track whose ammo guidance is "wake" (the player knows the class only after the identification). Any failure = false.</summary>
        private bool TorpedoIsWakeHoming(object veh, int id)
        {
            try
            {
                bool cached;
                if (id >= 0 && _evWake.TryGetValue(id, out cached)) return cached;
                if (!BoolOf(RxGet(veh, EvasionSpecs.VehIdentified))) return false;
                var g = Get(Get(Get(veh, CoreSpecs.VehObject), EvasionSpecs.WpnAp), EvasionSpecs.ApGuidanceType);
                if (g == null) return false;
                bool res = TacticsMath.GuidanceName(Convert.ToInt32(g)) == "wake";
                if (id >= 0) _evWake[id] = res;
                return res;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// T-193: every hostile torpedo track within 10 nm on the plotting table (closing from the per-unit history, frozen flag, wake-homing), nearest first;
        /// when there is none, the torpedo the game announced for this unit. pos: track id -> lat, lon.
        /// </summary>
        private List<EvTorp> KnownTorpedoes(object u, double lat0, double lon0, double nowS, out Dictionary<int, double[]> pos)
        {
            var res = new List<EvTorp>();
            pos = new Dictionary<int, double[]>();
            int uid = UidOf(u);
            var live = new List<int>();
            var vehicles = Vehicles();
            if (vehicles != null)
                foreach (var c in vehicles)
                {
                    if (Kind(c) != "Torpedo") continue;
                    bool gone = IsGone(c);
                    var id = D(Get(c, CoreSpecs.VehId));
                    int tid = id.HasValue ? (int)id.Value : -1;
                    if (tid >= 0 && !gone) live.Add(tid);
                    var p = TrackPos(c);
                    if (!TorpedoTrack.Live(gone, Relation(c), p != null)) continue;   // T-203: the one live-track rule, shared with the anti-torpedo fire
                    double d = WorldDistanceNm(lat0, lon0, p[0], p[1]);
                    if (d > 10.0) continue;
                    _evFresh.Globe = _evPursuit.Globe = AswMath.WorldGlobe;   // T-352: flat on stable
                    if (tid >= 0) { _evFresh.Update(tid, p[0], p[1], nowS); _evClosing.Update(uid, tid, d, nowS); }
                    var live2 = Get(c, EvasionSpecs.VehHasLiveTrack);
                    bool stale = (tid >= 0 && _evFresh.Stale(tid, nowS)) || (live2 is bool && !(bool)live2);
                    bool wake268 = TorpedoIsWakeHoming(c, tid);
                    if (tid >= 0) _evPursuit.Update(uid, tid, p[0], p[1], !stale, wake268, lat0, lon0, nowS);   // T-268 (ADR-0107)
                    var t = new EvTorp
                    {
                        TrackId = tid, BrgDeg = WorldBearing(lat0, lon0, p[0], p[1]), DistNm = d, AgeS = 0,
                        ClosingKn = tid >= 0 ? _evClosing.ClosingKn(uid, tid) : null, Stale = stale, WakeHoming = wake268
                    };
                    if (stale && tid >= 0) { t.ThreatNm = _evPursuit.PursuitNm(uid, tid, lat0, lon0); t.PursuitExpired = _evPursuit.Expired(uid, tid, nowS); }   // T-268
                    if (tid >= 0 && !stale && _evFresh.MovedWithin(tid, nowS, TrackClosing.WindowS)) _evLastTti.Note(uid, tid, EvasionMath.Tti(t), nowS);   // T-297
                    if (stale && tid >= 0) t.FrozenTtiS = _evLastTti.Residual(uid, tid, nowS);   // T-297
                    res.Add(t);
                    pos[tid] = p;
                }
            _evClosing.Prune(live); _evFresh.Prune(live); _evPursuit.Prune(live); _evLastTti.Prune(live);
            if (_evWake.Count > 64) _evWake.Clear();
            res.Sort((x, y) => x.DistNm.CompareTo(y.DistNm));
            if (res.Count > 0) return res;
            bool found = false; double ib = 0, ir = 0; int? it = null;
            LastInboundTorpedo(u, ref found, ref ib, ref ir, ref it);
            if (found) res.Add(new EvTorp { TrackId = it ?? -1, BrgDeg = ib, DistNm = ir, AgeS = 0 });
            return res;
        }


        /// <summary>Nearest hostile torpedo track within 10 nm on the plotting table, else the latest torpedo the game announced for this unit. Used by the submarine evasion (SubEvasion.cs).</summary>
        private bool NearestKnownTorpedo(object u, double lat0, double lon0, out EvTorp t, out double[] pos)
        {
            t = null; pos = null;
            double best = double.MaxValue;
            var vehicles = Vehicles();
            if (vehicles != null)
                foreach (var c in vehicles)
                {
                    if (Kind(c) != "Torpedo" || Relation(c) == "friendly" || IsGone(c)) continue;
                    var p = TrackPos(c); if (p == null) continue;
                    double d = WorldDistanceNm(lat0, lon0, p[0], p[1]);
                    if (d > 10.0 || d >= best) continue;
                    best = d; pos = p;
                    var id = D(Get(c, CoreSpecs.VehId));
                    t = new EvTorp { TrackId = id.HasValue ? (int)id.Value : -1, BrgDeg = WorldBearing(lat0, lon0, p[0], p[1]), DistNm = d, AgeS = 0 };
                }
            if (t != null) return true;
            bool found = false; double ib = 0, ir = 0; int? it = null;
            LastInboundTorpedo(u, ref found, ref ib, ref ir, ref it);
            if (!found) return false;
            t = new EvTorp { TrackId = it ?? -1, BrgDeg = ib, DistNm = ir, AgeS = 0 };
            return true;
        }
    }
}
