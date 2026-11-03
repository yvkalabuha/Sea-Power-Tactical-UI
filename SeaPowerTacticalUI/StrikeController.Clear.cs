using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase C (ADR-0133 section 5; plan 23.3): clear_pause. One launch gives stage 1 (the neighbour with the strong air defence is sunk by whole aircraft); the aircraft of stage 2 circle at their
    // gate and are not touched by the wait logic (Busy). Every second the sensor reads whether the neighbour is destroyed (IStrikeGame.Destroyed). After the observed sinking AND the end of stage 1
    // (one timeline per group) stage 2 is planned through the queue (the main target, only the stage-2 aircraft) and issued by itself, with the same rechecks as every launch. A neighbour that
    // lives gets a rewave (the auto) or a draft (the button). The wait is bounded by ClearTimeoutS and the fuel of the stage-2 aircraft.
    internal sealed partial class StrikeController
    {
        private readonly Dictionary<int, double> _clearNextT = new Dictionary<int, double>();   // uid -> the next try of the order to the gate
        private readonly HashSet<int> _clearSent = new HashSet<int>();                          // uid -> the orbit order at the gate was accepted

        private static bool ClearFinal(string stage) { return stage == "timeout" || stage == "fuel" || stage == "lost"; }

        /// <summary>The clearing is running (the button "Recalculate" must not plan over it).</summary>
        internal bool ClearHolds(StrikeGroup g)
        {
            if (g.Clear == null || !StrikeAutoMath.ClearActive(g.Clear.Stage)) return false;
            return !(g.Clear.Stage == "wait_sunk" && g.Clear.RewaveOpen);
        }

        private bool ClearBusy(StrikeGroup g, int uid) { return g.Clear != null && g.Clear.Stage2Uids.Contains(uid); }

        private PlanOpts RewaveOpts(StrikeGroup g, string why)
        {
            var c = g.Clear;
            var set = new StrikeTargetSet { Mode = "single", MainId = c.NeighbourId }; set.Ships.Add(c.NeighbourId);
            return new PlanOpts { Why = why, Purpose = "clear_rewave", Target = set, Exclude = new HashSet<int>(c.Stage2Uids), NoDeck = true, NoClearPause = true };
        }

        private PlanOpts MainOpts(StrikeGroup g, string why, string purpose)
        {
            var c = g.Clear;
            var set = new StrikeTargetSet { Mode = "single", MainId = c.MainId }; set.Ships.Add(c.MainId);
            return new PlanOpts { Why = why, Purpose = purpose, Target = set, Only = new HashSet<int>(c.Stage2Uids), NoDeck = true, NoClearPause = true };
        }

        /// <summary>From LaunchCore of a clear_pause variant (only the rows of stage 1 exist): the state, the timeout, the fuel bound, the orbit of the stage-2 aircraft at their gate.</summary>
        private void ClearStart(StrikeGroup g, StrikeVariant v, double now, bool confirm)
        {
            var a2 = v.Assignments.Where(a => a.Stage == 2).ToList();
            var c = new StrikeClearState
            {
                Stage = "stage1", NeighbourId = v.ClearNeighbour, MainId = a2.Count > 0 ? a2[0].TargetId : g.Target.MainId, WaitStartT = now, TimeoutT = now + _p.ClearTimeoutS,
                RedConfirmed = confirm && v.Red, Stage2Green = !v.Red, SensorT = -1,
            };
            c.Stage1Uids.AddRange(v.Assignments.Where(a => a.Stage == 1).Select(a => a.Uid).Distinct());
            c.Stage2Uids.AddRange(a2.Select(a => a.Uid).Distinct());
            if (a2.Count > 0) c.Gate2 = a2[0].GatePt;
            double fuel = -1;
            foreach (var a in a2)
            {
                var ac = Air(a.Uid);
                double w = ac == null ? -1 : StrikeAutoMath.FuelWaitS(ac, a.GatePt, a.LaunchPt, _p);
                if (w >= 0) fuel = fuel < 0 ? now + w : Math.Min(fuel, now + w);
            }
            c.FuelT = fuel;
            g.Clear = c;
            _clearSent.RemoveWhere(u => c.Stage2Uids.Contains(u));
            foreach (int u in c.Stage2Uids) _clearNextT.Remove(u);
            AutoNote(g, "clear_wait:" + I(c.NeighbourId));
            _log("[Strike] strike clear start grp=" + g.Name + " nb=" + I(c.NeighbourId) + " main=" + I(c.MainId) + " stage1=" + I(c.Stage1Uids.Count) + " stage2=" + I(c.Stage2Uids.Count) + " timeout_s=" + F1(_p.ClearTimeoutS)
                 + " fuel_s=" + (fuel < 0 ? "-" : F1(fuel - now)) + " gate=" + (a2.Count > 0 ? Pt(c.Gate2) : "-"));
            ClearGate(g, c, now);
        }

        /// <summary>The stage-2 aircraft circle at the gate: one order each (retried after IssueRetryS), and a gate that became unsafe moves to the wait point of the group.</summary>
        private void ClearGate(StrikeGroup g, StrikeClearState c, double t)
        {
            if (c.Stage2Uids.Count == 0) return;
            if (c.Gate2NextT <= 0 || t < c.Gate2NextT - _p.StaleCheckS - 1 || t >= c.Gate2NextT)
            {
                c.Gate2NextT = t + _p.StaleCheckS;
                if (g.WaitPt.HasValue && AswMath.DistNm(c.Gate2, g.WaitPt.Value) > MovedNm && StrikeWaitMath.WaitUnsafe(c.Gate2, Hostiles(), _p))
                {
                    c.Gate2 = g.WaitPt.Value;
                    _clearSent.RemoveWhere(u => c.Stage2Uids.Contains(u));
                    _log("[Strike] strike clear gate_moved grp=" + g.Name + " pt=" + Pt(c.Gate2));
                }
            }
            foreach (int uid in c.Stage2Uids.ToList())
            {
                if (_clearSent.Contains(uid)) continue;
                double next; if (_clearNextT.TryGetValue(uid, out next) && t < next) continue;
                var a = Air(uid);
                if (a == null || !a.Airborne) continue;
                string err = _g.Orbit(uid, c.Gate2, GateOrbitNm);
                _log("[Strike] strike clear gate grp=" + g.Name + " uid=" + I(uid) + " -> " + (err ?? "ok"));
                if (err == null) _clearSent.Add(uid); else _clearNextT[uid] = t + _p.IssueRetryS;
            }
        }

        private string SafeDestroyed(int id)
        {
            try { return _g.Destroyed(id) ?? "unknown"; } catch (Exception) { return "unknown"; }
        }

        private bool Stage1HasMissiles(StrikeGroup g, StrikeClearState c)
        {
            foreach (int uid in c.Stage1Uids)
            {
                if (!g.Members.Contains(uid)) continue;
                var a = Air(uid);
                if (a != null && a.Airborne && StrikeAirMath.UsableCount(a) > 0) return true;
            }
            return false;
        }

        private void TickClear(double t)
        {
            foreach (var g in _groups.ToList())
            {
                if (g.Clear == null) continue;
                try { ClearTickGroup(g, t); } catch (Exception e) { _log("[Strike] strike clear tick failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void ClearTickGroup(StrikeGroup g, double t)
        {
            var c = g.Clear;
            if (c.Stage != "released" && !ClearFinal(c.Stage)) ClearGate(g, c, t);
            if (ClearFinal(c.Stage))
            {
                if (c.PendingMain && !ExecRunning(g) && !ReplanQueued(g)) { c.PendingMain = false; QueueReplan(g, MainOpts(g, "clear_" + c.Stage, "clear_main")); }
                return;
            }
            if (c.Stage == "released") return;   // stage 2 runs: its Finish ends the clearing
            if (c.SensorT < 0 || t < c.SensorT || t - c.SensorT >= 1)
            {
                c.SensorT = t;
                c.Neighbour = SafeDestroyed(c.NeighbourId);
                int h = 0;
                try { _g.Fate(c.NeighbourId, out h); } catch (Exception) { h = 0; }
                List<StrikeHitSeen> l; _hits.TryGetValue(c.NeighbourId, out l);
                c.Hits = Math.Max(h, l == null ? 0 : l.Count);
            }
            if (c.Stage == "release") { ClearRelease(g, c, t); return; }
            if (c.Stage == "red_wait" || c.Stage == "stale_wait")
            {
                if (c.FuelT >= 0 && t >= c.FuelT) { ClearAbort(g, c, "fuel", t); return; }
                if (g.Auto.On && g.Auto.Waves < _p.AutoWaves) { c.Stage = "release"; c.Replans = 0; _log("[Strike] strike clear release grp=" + g.Name + " how=auto_on"); }
                return;
            }
            var inp = new StrikeClearInput
            {
                Stage = c.Stage, Neighbour = c.Neighbour, NeighbourHits = c.Hits, ExecRunning = ExecRunning(g), Auto = g.Auto.On, Stage1Missiles = Stage1HasMissiles(g, c),
                Waves = g.Auto.Waves, MaxWaves = _p.AutoWaves, Now = t, TimeoutT = c.TimeoutT, FuelT = c.FuelT,
            };
            string step = StrikeAutoMath.ClearStep(inp);
            switch (step)
            {
                case "release":
                    c.Stage = "release"; c.SunkT = t; c.How = c.Neighbour == "destroyed" ? "destroyed" : "gone_after_hits"; c.Replans = 0;
                    if (g.Timeline != null && !ExecRunning(g)) { g.Timeline = null; g.Variants = new List<StrikeVariant>(); g.Chosen = -1; }   // a draft of a rewave is not needed any more
                    c.RewaveOpen = false;
                    _replanQ.RemoveAll(kv => kv.Key == g && kv.Value.Purpose == "clear_rewave");
                    AutoNote(g, "clear_release:" + c.How);
                    _log("[Strike] strike clear release grp=" + g.Name + " nb=" + I(c.NeighbourId) + " how=" + c.How + " hits=" + I(c.Hits) + " wait_s=" + F1(t - c.WaitStartT));
                    QueueReplan(g, MainOpts(g, "clear_release", "clear_stage2"));
                    break;
                case "rewave":
                case "draft_rewave":
                    if (c.RewaveOpen) break;
                    c.RewaveOpen = true; c.Rewaves++; c.Stage = "wait_sunk";
                    AutoNote(g, "clear_rewave:" + I(c.NeighbourId));
                    _log("[Strike] strike clear rewave grp=" + g.Name + " nb=" + I(c.NeighbourId) + " n=" + I(c.Rewaves) + " mode=" + (step == "rewave" ? "auto" : "draft"));
                    QueueReplan(g, RewaveOpts(g, "clear_rewave"));
                    break;
                case "timeout":
                case "fuel":
                case "lost":
                    ClearAbort(g, c, step, t);
                    break;
            }
            // the auto launches the rewave by the general rules once its plan is there
            if (c.Stage == "wait_sunk" && c.RewaveOpen && g.Auto.On && !ExecRunning(g) && !ReplanQueued(g) && g.Timeline != null && (g.Timeline.Status == "draft" || g.Timeline.Status == "armed"))
                ClearAutoRewave(g, c);
        }

        private void ClearAutoRewave(StrikeGroup g, StrikeClearState c)
        {
            if (g.Auto.Waves >= _p.AutoWaves) { AutoStop(g, "waves"); return; }
            var x = AutoInput(g, _g.Now);
            bool red;
            int vi = StrikeAutoMath.Pick(x.Variants, g.Chosen, g.Auto.ChosenByPlayer, out red);
            if (vi < 0) { AutoStop(g, "blocked:" + (x.Variants.Count > 0 && x.Variants[0].Block != null ? x.Variants[0].Block : "no_rows")); return; }
            if (!x.Variants.First(v => v.Index == vi).AllAirborne) return;   // wait for the gather
            var v0 = g.Variants[vi];
            string r = LaunchCore(g, vi, "auto", true);
            if (IsErr(r)) { string code = r.Substring(1); int bar = code.IndexOf('|'); if (bar >= 0) code = code.Substring(0, bar); AutoStop(g, "launch_err:" + code); return; }
            g.Auto.State = "launch";
            AutoNote(g, "auto_launch:" + I(g.Auto.Waves) + ":" + v0.Kind);
            _log("[Strike] strike auto grp=" + g.Name + " wave=" + I(g.Auto.Waves) + " max=" + I(_p.AutoWaves) + " action=launch why=clear_rewave var=" + I(vi) + " kind=" + v0.Kind + " red=" + (red ? "1" : "0"));
        }

        /// <summary>Stage 1 is over and the neighbour is sunk: the plan of stage 2 (made by the queue) is issued by itself.</summary>
        private void ClearRelease(StrikeGroup g, StrikeClearState c, double t)
        {
            if (c.FuelT >= 0 && t >= c.FuelT && g.Timeline == null) { ClearAbort(g, c, "fuel", t); return; }
            if (ReplanQueued(g)) return;
            var tl = g.Timeline;
            if (tl == null || g.Variants == null)
            {
                if (c.Replans++ < 2) { QueueReplan(g, MainOpts(g, "clear_release", "clear_stage2")); return; }
                AutoNote(g, "clear_lost:" + I(c.NeighbourId));
                _log("[Strike] strike clear end grp=" + g.Name + " why=no_plan");
                AutoStop(g, "no_plan"); ClearEnd(g, "no_plan");
                return;
            }
            if (tl.Status == "stale")
            {
                if (c.Replans++ < 3) { QueueReplan(g, MainOpts(g, "clear_stale", "clear_stage2")); return; }
                // 3 stale replans in a row: stage 2 is NOT launched by itself; it stays a draft for the player (the button), the auto stops
                c.Stage = "stale_wait";
                AutoNote(g, "clear_stale");
                _log("[Strike] strike clear stale_wait grp=" + g.Name + " replans=" + I(c.Replans));
                if (g.Auto.On) AutoStop(g, "clear_stale");
                return;
            }
            if (tl.Status != "draft" && tl.Status != "armed" && tl.Status != "stale") return;
            if (g.Variants.Count == 0)
            {
                AutoNote(g, "clear_lost:" + I(c.NeighbourId));
                _log("[Strike] strike clear end grp=" + g.Name + " why=no_variants");
                AutoStop(g, "no_missiles"); ClearEnd(g, "no_variants");
                return;
            }
            var x = AutoInput(g, t);
            bool red;
            int vi = StrikeAutoMath.Pick(x.Variants, -1, false, out red);
            if (vi < 0)
            {
                string block = x.Variants.Select(v => v.Block).FirstOrDefault(b => b != null) ?? "no_rows";
                _log("[Strike] strike clear end grp=" + g.Name + " why=blocked:" + block);
                AutoStop(g, "blocked:" + block); ClearEnd(g, "blocked");
                return;
            }
            if (g.Auto.On && g.Auto.Waves >= _p.AutoWaves) AutoStop(g, "waves");   // the limit of the auto is reached: stage 2 follows by itself, as without the auto
            bool allowed = !red || c.RedConfirmed || g.Auto.On;
            if (!allowed)
            {
                c.Stage = "red_wait";
                AutoNote(g, "clear_red");
                _log("[Strike] strike clear red_wait grp=" + g.Name + " p=" + F2d(MainP(g.Variants[vi], c.MainId)));
                return;
            }
            string r = LaunchCore(g, vi, "stage2", true);
            if (IsErr(r))
            {
                string code = r.Substring(1); int bar = code.IndexOf('|'); if (bar >= 0) code = code.Substring(0, bar);
                if (code == "target_excluded" || code == "no_target") { _log("[Strike] strike recheck grp=" + g.Name + " tgt=" + I(c.MainId) + " why=" + code + " stage=2"); AutoStop(g, "recheck"); }
                else AutoStop(g, "launch_err:" + code);
                _log("[Strike] strike clear end grp=" + g.Name + " why=launch_err:" + code);
                ClearEnd(g, "launch_err");
                return;
            }
            c.Stage = "released";
            AutoNote(g, "clear_stage2:" + I(c.MainId));
            _log("[Strike] strike clear stage2 grp=" + g.Name + " var=" + I(vi) + " kind=" + g.Variants[vi].Kind + " red=" + (red ? "1" : "0") + " rows=" + I(g.Timeline == null ? 0 : g.Timeline.Exec.Count));
        }

        private static double MainP(StrikeVariant v, int mainId)
        {
            var t = v.PerTarget.FirstOrDefault(x => x.TrackId == mainId) ?? v.PerTarget.FirstOrDefault();
            return t == null ? 0 : (t.PSunk >= 0 ? t.PSunk : t.P);
        }

        /// <summary>timeout | fuel | lost: the clearing is over without the sinking. The auto stops with the reason; the stage-2 aircraft stay at their gate; a draft "by the main target without clearing" is made for them (the button) or the player cancels.</summary>
        private void ClearAbort(StrikeGroup g, StrikeClearState c, string why, double t)
        {
            c.Stage = why;
            AutoNote(g, "clear_" + why + ":" + I(c.NeighbourId));
            _log("[Strike] strike clear " + why + " grp=" + g.Name + " nb=" + I(c.NeighbourId) + " wait_s=" + F1(t - c.WaitStartT) + " neighbour=" + c.Neighbour);
            AutoStop(g, "clear_" + why);
            _replanQ.RemoveAll(kv => kv.Key == g && kv.Value.Purpose != null && kv.Value.Purpose.StartsWith("clear_", StringComparison.Ordinal));
            c.RewaveOpen = false;
            if (c.Stage2Uids.Count == 0) { ClearEnd(g, why); return; }
            if (ExecRunning(g)) { c.PendingMain = true; return; }
            if (g.Timeline != null) { g.Timeline = null; g.Variants = new List<StrikeVariant>(); g.Chosen = -1; }
            QueueReplan(g, MainOpts(g, "clear_" + why, "clear_main"));
        }

        /// <summary>Instead of NextWave: stage 1 (or a rewave) is over. The neighbour decides what follows (TickClear); the target of the group is not changed.</summary>
        private void ClearAfterFinish(StrikeGroup g, StrikeTimeline tl)
        {
            var c = g.Clear;
            double now = _g.Now;
            if (c.Stage == "released")
            {
                ClearEnd(g, "done");
                NextWave(g, tl);
                return;
            }
            c.RewaveOpen = false;
            if (c.Stage == "stage1") c.Stage = "wait_sunk";
            c.WaitStartT = c.WaitStartT < 0 ? now : c.WaitStartT;
            c.SensorT = -1;
            _log("[Strike] strike clear wait grp=" + g.Name + " nb=" + I(c.NeighbourId) + " stage=" + c.Stage + " rewaves=" + I(c.Rewaves));
            RecomputeWait(g, "done");
        }

        /// <summary>target_changed | cancel | disband | loaded | done | empty | no_plan | launch_err | blocked | main_launched | reset: the state is dropped, the stage-2 aircraft go back to the normal wait logic.</summary>
        internal void ClearEnd(StrikeGroup g, string why)
        {
            var c = g.Clear;
            if (c == null) return;
            foreach (int u in c.Stage2Uids) { _ms.Remove(u); _clearSent.Remove(u); _clearNextT.Remove(u); }
            _replanQ.RemoveAll(kv => kv.Key == g && kv.Value.Purpose != null && kv.Value.Purpose.StartsWith("clear_", StringComparison.Ordinal));
            g.Clear = null;
            _log("[Strike] strike clear end grp=" + g.Name + " why=" + why + " stage=" + c.Stage);
        }

        /// <summary>One aircraft left the group (removed, released, lost): it is not a stage-2 aircraft any more; none left = the clearing is over.</summary>
        private void ClearDrop(StrikeGroup g, int uid)
        {
            var c = g.Clear;
            if (c == null) return;
            c.Stage1Uids.Remove(uid);
            if (c.Stage2Uids.Remove(uid) && c.Stage2Uids.Count == 0 && c.Stage != "released") ClearEnd(g, "empty");
            _clearSent.Remove(uid); _clearNextT.Remove(uid);
        }

        /// <summary>"clear" of a group for ui_state (ADR-0133 section 7); null without a clearing.</summary>
        internal string ClearJson(StrikeGroup g)
        {
            var c = g.Clear;
            if (c == null) return "null";
            double now = _g.Now;
            return "{\"stage\":" + Q(c.Stage) + ",\"neighbour\":" + I(c.NeighbourId) + ",\"main\":" + I(c.MainId) + ",\"stage1\":[" + string.Join(",", c.Stage1Uids.Select(I)) + "],\"stage2\":[" + string.Join(",", c.Stage2Uids.Select(I)) + "]"
                   + ",\"wait_s\":" + N(Math.Max(0, now - c.WaitStartT)) + ",\"timeout_s\":" + N(c.TimeoutT - now) + ",\"fuel_s\":" + (c.FuelT < 0 ? "null" : N(c.FuelT - now))
                   + ",\"how\":" + (c.How == null ? "null" : Q(c.How)) + ",\"rewaves\":" + I(c.Rewaves) + ",\"neighbour_state\":" + Q(c.Neighbour) + ",\"red_confirmed\":" + Json.Bool(c.RedConfirmed) + "}";
        }
    }
}
