using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-138 / ADR-0054 and ADR-0039: the queue of RouteJob (A* around land, SarRouteMath unchanged) for all intercepts.
    /// At most <see cref="MaxJobs"/> jobs run at once, the others wait; <see cref="ProbeBudget"/> height probes per frame in total;
    /// Solve goes to the background. Pure: heights and the background runner are injected. Main thread only (Solve is the only background part).</summary>
    internal sealed class InterceptRoute
    {
        public const int MaxJobs = 2;
        public const double ObstacleM = 100;   // the "height" of an enemy zone cell for a detour job

        private sealed class Entry
        {
            public int Uid;
            public GeoPt From, To;
            public RouteParams P;
            public RouteJob Job;          // null = waiting for a slot
            public bool SolveQueued;
            public RouteResult Result;    // set when done, handed over once by Poll
            public Func<GeoPt, bool> Blocked;   // T-169: enemy zones of a detour count as land for this job only (null = land only)
        }

        private readonly Func<GeoPt, double> _heightM;
        private readonly Action<Action> _background;
        private readonly List<Entry> _entries = new List<Entry>();   // request order

        public InterceptRoute(Func<GeoPt, double> heightM, Action<Action> background)
        {
            if (heightM == null) throw new ArgumentNullException("heightM");
            _heightM = heightM;
            _background = background ?? (a => a());
            ProbeBudget = 500;
        }

        /// <summary>[Intercept] ProbeBudget, Live.</summary>
        public int ProbeBudget { get; set; }

        public int ActiveJobs { get { int n = 0; foreach (var e in _entries) if (e.Job != null && e.Result == null) n++; return n; } }
        public int WaitingJobs { get { int n = 0; foreach (var e in _entries) if (e.Job == null && e.Result == null) n++; return n; } }

        private Entry Find(int uid)
        {
            foreach (var e in _entries) if (e.Uid == uid) return e;
            return null;
        }

        /// <summary>Replaces the pending job (or an undelivered result) of this uid.</summary>
        public void Request(int uid, GeoPt from, GeoPt to, RouteParams p)
        {
            Request(uid, from, to, p, null);
        }

        /// <summary>T-169 / ADR-0067: blocked = extra obstacles (enemy zones) seen by this job as land (+100 m); the terrain cache is not touched.</summary>
        public void Request(int uid, GeoPt from, GeoPt to, RouteParams p, Func<GeoPt, bool> blocked)
        {
            Cancel(uid);
            _entries.Add(new Entry { Uid = uid, From = from, To = to, P = p ?? new RouteParams(), Blocked = blocked });
            Promote();
        }

        /// <summary>null = not done yet (or unknown uid); the result is handed over once.</summary>
        public RouteResult Poll(int uid)
        {
            Entry e = Find(uid);
            if (e == null || e.Result == null) return null;
            _entries.Remove(e);
            return e.Result;
        }

        /// <summary>A job or an undelivered result exists for the uid.</summary>
        public bool Busy(int uid) { return Find(uid) != null; }

        public void Cancel(int uid)
        {
            Entry e = Find(uid);
            if (e == null) return;
            _entries.Remove(e);   // a Solve already in the background ends on its own; nobody reads its result
            Promote();
        }

        private void Promote()
        {
            int active = ActiveJobs;
            foreach (var e in _entries)
            {
                if (active >= MaxJobs) break;
                if (e.Job != null || e.Result != null) continue;
                e.Job = new RouteJob(e.From, e.To, e.P);
                active++;
            }
        }

        /// <summary>Once per frame: probes within <see cref="ProbeBudget"/>, queues Solve, collects results, gives free slots to waiting jobs.
        /// Returns the probes used.</summary>
        public int FramePump()
        {
            Promote();
            int left = ProbeBudget, used = 0;
            foreach (var e in _entries.ToArray())
            {
                if (e.Job == null || e.Result != null) continue;
                string st = e.Job.State;
                if ((st == "probe_line" || st == "probe_grid") && left > 0)
                {
                    Func<GeoPt, bool> bl = e.Blocked;
                    int n = e.Job.Step(bl == null ? _heightM : (g => bl(g) ? ObstacleM : _heightM(g)), left);
                    left -= n; used += n;
                    st = e.Job.State;
                }
                if (st == "ready_to_solve" && !e.SolveQueued)
                {
                    e.SolveQueued = true;
                    _background(e.Job.Solve);
                    st = e.Job.State;
                }
                if (st == "probe_grid" || st == "probe_line") e.SolveQueued = false;   // the frame was widened: Solve again later
                if (st == "done") e.Result = e.Job.Result;
            }
            Promote();
            return used;
        }

        /// <summary>Is the leg a-b free of land and shore margin (SarRouteMath.SegmentClear on the cached heights)?</summary>
        public bool LegClear(GeoPt a, GeoPt b, RouteParams p)
        {
            int probes;
            return SarRouteMath.SegmentClear(a, b, _heightM, p ?? new RouteParams(), out probes);
        }

        /// <summary>Menu only: rough probe of a straight line, step <see cref="InterceptMath.ProbeStepNm"/>, at most <see cref="InterceptMath.MaxProbes"/> probes.</summary>
        public bool LineHitsLand(GeoPt a, GeoPt b, RouteParams p)
        {
            p = p ?? new RouteParams();
            double d = SarRouteMath.Nm(a, b);
            int n = (int)Math.Ceiling(d / InterceptMath.ProbeStepNm);
            if (n < 1) n = 1;
            if (n + 1 > InterceptMath.MaxProbes) n = InterceptMath.MaxProbes - 1;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                var g = new GeoPt(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t);
                if (!SarRouteMath.Passable(_heightM(g), p.MinDepthM)) return true;
            }
            return false;
        }

        public void Clear() { _entries.Clear(); }
    }
}
