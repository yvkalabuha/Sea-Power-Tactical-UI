using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase C (plan 24.3, Q4-Q5): the missiles in flight, the arrival at the targets (the spread of the arrival is the measure of the time on target), the result by target, the return of the
    // aircraft, the plan of the next wave, and "Cancel" in the middle of a strike. Only the game clock.
    internal sealed partial class StrikeController
    {
        private sealed class MslTrack { public int Uid, TargetId; public double Lat, Lon, T; }

        private readonly Dictionary<int, MslTrack> _msl = new Dictionary<int, MslTrack>();
        private readonly Dictionary<int, List<double>> _arrT = new Dictionary<int, List<double>>();   // target -> the times the missiles vanished at it
        private readonly Dictionary<int, GeoPt> _lastShipPos = new Dictionary<int, GeoPt>();
        private double _waitLogAt = double.NegativeInfinity;

        private StrikeTargetResult Result(StrikeTimeline tl, int tid)
        {
            var tr = tl.Results.FirstOrDefault(x => x.TrackId == tid);
            if (tr == null)
            {
                var s = Ship(tid);
                tr = new StrikeTargetResult { TrackId = tid, ClassName = s == null ? null : s.ClassName };
                tl.Results.Add(tr);
            }
            return tr;
        }

        /// <summary>New missiles of the aircraft of the strike are bound to the target (the game's last target, else the target of the row launched last); a missile that vanished is an arrival.</summary>
        private void UpdateMissiles(StrikeGroup g, double now)
        {
            var tl = g.Timeline;
            foreach (var s in _g.Ships() ?? new List<StrikeShip>()) if (s != null) _lastShipPos[s.TrackId] = new GeoPt(s.Lat, s.Lon);
            foreach (int uid in tl.Exec.Where(r => r.LaunchedT >= 0).Select(r => r.Uid).Distinct().ToList())
            {
                IList<StrikeMslSeen> seen = null;
                try { seen = _g.InFlight(uid); } catch (Exception) { }
                if (seen == null) seen = new List<StrikeMslSeen>();
                var last = tl.Exec.Where(r => r.Uid == uid && r.LaunchedT >= 0).OrderByDescending(r => r.LaunchedT).FirstOrDefault();
                var ids = new HashSet<int>();
                foreach (var m in seen)
                {
                    ids.Add(m.Id);
                    MslTrack tr;
                    if (!_msl.TryGetValue(m.Id, out tr))
                    {
                        tr = new MslTrack { Uid = uid, TargetId = m.TargetId > 0 ? m.TargetId : (last == null ? 0 : last.TargetId) };
                        _msl[m.Id] = tr;
                    }
                    tr.Lat = m.Lat; tr.Lon = m.Lon; tr.T = now;
                }
                foreach (int id in _msl.Where(kv => kv.Value.Uid == uid && !ids.Contains(kv.Key)).Select(kv => kv.Key).ToList())
                {
                    var tr = _msl[id]; _msl.Remove(id);
                    GeoPt tp;
                    var ts = Ship(tr.TargetId);
                    if (ts != null) tp = new GeoPt(ts.Lat, ts.Lon); else if (!_lastShipPos.TryGetValue(tr.TargetId, out tp)) tp = new GeoPt(tr.Lat, tr.Lon);
                    double dist = AswMath.DistNm(new GeoPt(tr.Lat, tr.Lon), tp);
                    var res = Result(tl, tr.TargetId);
                    bool near = dist <= _p.ArriveNearNm;
                    if (near)
                    {
                        res.Arrived++;
                        List<double> l;
                        if (!_arrT.TryGetValue(tr.TargetId, out l)) { l = new List<double>(); _arrT[tr.TargetId] = l; }
                        l.Add(now);
                        if (res.FirstArriveT < 0 || now < res.FirstArriveT) res.FirstArriveT = now;
                        if (now > res.LastArriveT) res.LastArriveT = now;
                    }
                    else res.ShotDown++;
                    _log("[Strike] strike arrive tgt=" + I(tr.TargetId) + " msl=" + I(id) + " uid=" + I(uid) + " t=" + F1(now) + " dist_nm=" + F1(dist) + " kind=" + (near ? "at_target" : "shot_down"));
                }
            }
        }

        private void TryFinish(StrikeGroup g, double now)
        {
            var tl = g.Timeline;
            if (tl == null || !IsExecStatus(tl.Status)) return;
            if (tl.Exec.Any(r => !StrikeExecMath.Terminal(r.State))) return;
            bool flying = _msl.Values.Any(m => tl.Exec.Any(r => r.Uid == m.Uid));
            double lastT = StrikeExecMath.ResultDeadlineT(tl.Exec, now);   // T-353: the plan time plus the real lateness of the launch
            if (flying && now <= lastT + _p.ResultDelayS)
            {
                if (now - _waitLogAt >= 30 || now < _waitLogAt) { _waitLogAt = now; _log("[Strike] strike result wait " + g.Name + " flying=" + I(_msl.Values.Count(m => tl.Exec.Any(r => r.Uid == m.Uid))) + " now=" + F1(now) + " last_t=" + F1(lastT)); }
                return;
            }
            Finish(g, now);
        }

        private void Finish(StrikeGroup g, double now)
        {
            var tl = g.Timeline;
            bool cancelled = tl.Status == "cancelled";
            foreach (int tid in tl.Exec.Select(r => r.TargetId).Distinct()) Result(tl, tid);
            foreach (var tr in tl.Results)
            {
                string state; int hits = 0;
                try { state = _g.Fate(tr.TrackId, out hits); } catch (Exception) { state = "unknown"; }
                int hb; if (tl.HitBase != null && tl.HitBase.TryGetValue(tr.TrackId, out hb) && hb > 0) { hits = Math.Max(0, hits - hb); if (state == "damaged" && hits == 0) state = "intact"; }   // only the hits of this wave
                tr.State = state ?? "unknown"; tr.Hits = hits;
                List<double> l;
                if (_arrT.TryGetValue(tr.TrackId, out l) && l.Count > 0) tr.SpreadS = StrikeTotMath.SpreadS(l);
                _log("[Strike] strike spread tgt=" + I(tr.TrackId) + " n=" + I(tr.Arrived) + " s=" + F1(tr.SpreadS));
                _log("[Strike] strike result tgt=" + I(tr.TrackId) + " state=" + tr.State + " hits=" + I(tr.Hits) + " fired=" + I(tr.Fired) + " arrived=" + I(tr.Arrived) + " shot_down=" + I(tr.ShotDown));
            }
            RestoreDone(tl, true);
            foreach (int uid in tl.Exec.Select(r => r.Uid).Distinct()) { Book.Clear(uid); _ms.Remove(uid); }
            foreach (int id in _msl.Where(kv => tl.Exec.Any(r => r.Uid == kv.Value.Uid)).Select(kv => kv.Key).ToList()) _msl.Remove(id);
            tl.DoneT = now;
            g.Results = tl.Results; g.LastExec = tl.Exec; g.ExecStatus = cancelled ? "cancelled" : "done"; g.ResultT = now;
            tl.Status = cancelled ? "cancelled" : "done";
            _log("[Strike] strike done " + g.Name + " wave=" + I(g.Wave) + " status=" + g.ExecStatus);
            g.Timeline = null; g.Variants = new List<StrikeVariant>(); g.Chosen = -1;
            if (!cancelled) { if (g.Clear != null) ClearAfterFinish(g, tl); else NextWave(g, tl); }
        }

        /// <summary>The targets that are not sunk and the members that still have missiles: the main target is the first not sunk by the priority, the plan is counted (draft, no launch).</summary>
        private void NextWave(StrikeGroup g, StrikeTimeline tl)
        {
            // T-346 D2: the survivors are the ships of the set (not only the ones fired at in this wave): a spread set whose main target alone was hit still has its other ships
            var sunk = new HashSet<int>(tl.Results.Where(r => r.State == "sunk").Select(r => r.TrackId));
            var valid = StrikePlanMath.Survivors(g.Target, tl.Results.Select(r => r.TrackId), _g.Ships(), id => sunk.Contains(id) || _killLogged.Contains(id), StrikeCalib.TonBandFrac);
            bool missiles = false;
            foreach (int uid in g.Members) { var a = Air(uid); if (a != null && a.Airborne && StrikeAirMath.UsableCount(a) > 0) { missiles = true; break; } }
            if (valid.Count == 0 || !missiles) { _log("[Strike] strike wave " + g.Name + " none targets=" + I(valid.Count) + " missiles=" + (missiles ? "yes" : "no")); RecomputeWait(g, "done"); return; }
            g.Wave++;
            string mode = g.Target.Mode == "spread" && valid.Count > 1 ? "spread" : "single";
            g.Target = new StrikeTargetSet { Mode = mode, MainId = valid[0].TrackId };
            foreach (var s in valid) g.Target.Ships.Add(s.TrackId);
            _log("[Strike] strike wave " + g.Name + " wave=" + I(g.Wave) + " main=" + I(g.Target.MainId) + " targets=" + I(valid.Count));
            QueueReplan(g, new PlanOpts { Why = "wave", Purpose = "wave" });   // T-342 C: the draft of the next wave is counted by the queue one tick later (never inside the Finish)
        }

        /// <summary>"Cancel" in the middle of a strike: the rows not yet ordered are cancelled, the aircraft go to the wait point; what was fired flies on and is counted. No next wave is planned.</summary>
        public string CancelExec(StrikeGroup g, string why)
        {
            var tl = g.Timeline;
            if (tl == null || tl.Status != "executing") return Fail("not_executing");
            int cancelled = 0;
            foreach (var r in tl.Exec)
                if (!StrikeExecMath.Terminal(r.State) && r.State != "issued") { r.State = "cancelled"; r.Why = why; cancelled++; }
            tl.Status = "cancelled";
            EwOffAll(g, "cancel");   // phase D: the jammers go off in the same tick and back to the wait point
            DeckAbortAll(g, why == "player" ? "cancel" : why);
            AutoStop(g, why == "player" ? "cancel" : why); ClearEnd(g, why == "player" ? "cancel" : why); DropQueued(g);
            g.Variants = new List<StrikeVariant>(); g.Chosen = -1;
            foreach (int uid in tl.Exec.Select(r => r.Uid).Distinct())
            {
                if (!tl.Exec.Any(r => r.Uid == uid && r.State == "issued")) Book.Clear(uid);
                _ms.Remove(uid);
            }
            RestoreDone(tl, false);
            RecomputeWait(g, "cancel_exec");
            _log("[Strike] strike cancel exec " + g.Name + " why=" + why + " rows=" + I(cancelled));
            TryFinish(g, _g.Now);
            return Ack(g.Name, "\"cancelled\":" + I(cancelled));
        }

        /// <summary>The mission was left: the cache of the time of flight, the book, the missiles and the arrival times belong to it.</summary>
        public void ResetMission()
        {
            _tofs.Clear(); _warmFailed.Clear(); Book.Clear(); _msl.Clear(); _arrT.Clear(); _lastShipPos.Clear(); _prevRoe.Clear(); _roeFallbackLogged = false;
            ResetKill(); DeckReset(); ResetAuto();
            _speedSet.Clear(); _speedTries.Clear(); _speedNextT.Clear(); _rewarm.Clear(); _rewarmKeys.Clear();
        }
    }
}
