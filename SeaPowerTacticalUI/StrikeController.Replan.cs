using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>T-342 C: how a plan is asked for. Purpose: button | wave | auto | clear_stage2 | clear_rewave | clear_main.</summary>
    internal sealed class PlanOpts
    {
        public bool Queued;
        public string Why, Purpose;
        public double ShiftS = -1;
        public StrikeTargetSet Target;               // another target set than the one of the group (the group keeps its own)
        public HashSet<int> Only, Exclude;           // the aircraft of the plan: only these / all but these
        public bool NoDeck, NoClearPause;            // no deck order after this plan; no clear_pause variant in it
        public double RequestedT = -1;               // the mission time the request was queued (the log: waited_s)
    }

    // T-342 C (plan 23.1): a plan without the button never runs inside the tick directly. The request goes to a queue (one per group, a newer one replaces the older), and Warm() serves
    // it: at most ReplanPerTick plans a tick and never in the tick of a cold warm-up call. The queued plan has no EstimateShot (CheapTof: the cache or the measured scale).
    internal sealed partial class StrikeController
    {
        private readonly List<KeyValuePair<StrikeGroup, PlanOpts>> _replanQ = new List<KeyValuePair<StrikeGroup, PlanOpts>>();

        internal bool ReplanQueued(StrikeGroup g) { return _replanQ.Any(kv => kv.Key == g); }

        internal int ReplanQueueLength { get { return _replanQ.Count; } }

        internal void QueueReplan(StrikeGroup g, PlanOpts o)
        {
            if (g == null || o == null) return;
            o.Queued = true; o.RequestedT = _g.Now;
            _replanQ.RemoveAll(kv => kv.Key == g);
            _replanQ.Add(new KeyValuePair<StrikeGroup, PlanOpts>(g, o));
            _log("[Strike] strike replan queued grp=" + g.Name + " why=" + (o.Why ?? "-") + " purpose=" + (o.Purpose ?? "-"));
        }

        /// <summary>The queued requests of the group are dropped (the group was cancelled, disbanded, or the target changed).</summary>
        internal void DropQueued(StrikeGroup g) { _replanQ.RemoveAll(kv => kv.Key == g); }

        private void DropQueuedAll() { _replanQ.Clear(); }

        /// <summary>Warm(): the first request whose group is still there. One plan per call. The caller does not call it in a tick that made a cold warm-up.</summary>
        private bool ServeReplan(double t)
        {
            for (int served = 0; served < StrikeCalib.ReplanPerTick && _replanQ.Count > 0; served++)
            {
                var kv = _replanQ[0];
                _replanQ.RemoveAt(0);
                var g = kv.Key;
                if (!_groups.Contains(g)) { served--; continue; }
                string r;
                try { r = PlanCore(g, kv.Value); }
                catch (Exception e) { r = "!" + e.GetType().Name; _log("[Strike] strike replan failed: " + e.GetType().Name + ": " + e.Message); }
                if (IsErr(r)) _log("[Strike] strike replan grp=" + g.Name + " purpose=" + (kv.Value.Purpose ?? "-") + " -> " + r);
                if (IsErr(r) && kv.Value.Purpose == "auto") AutoPlanFailed(g, r);
                if (kv.Value.Purpose == "wave" && IsErr(r)) _log("[Strike] strike wave " + g.Name + " plan -> " + r);
                return true;
            }
            return false;
        }
    }
}
