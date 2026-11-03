using System;
using System.Diagnostics;
using System.Threading;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-225 (ADR-0092): the only door from the coverage worker to the game's terrain. Worker thread: Fetch blocks until the main thread answered every point of the
    /// batch (or the build was cancelled / the worker stopped). Main thread: Service answers the points of the pending request within budgetMs per frame.
    /// One request at a time. The lock only guards the slot (cursor, flags, copies of at most MaxChunk points); the game is called outside it, into the broker's own buffers.
    /// Pure (System only): checked by SelfTest on a fake clock.
    /// </summary>
    internal sealed class TerrainBroker
    {
        public const double BudgetMs = 2.0, BurstBudgetMs = 4.0;
        public const int MaxChunk = 16384, ClockEvery = 32, WaitTickMs = 100;
        public enum FetchResult { Done, Cancelled, Stopped }

        private readonly object _lock = new object();
        private readonly Func<double> _clock;
        private readonly double[] _inLatLon = new double[2 * MaxChunk];
        private readonly float[] _answers = new float[MaxChunk];
        // slot (under _lock)
        private double[] _reqLatLon;
        private float[] _reqHeights;
        private int _n, _cursor, _done;
        private bool _active, _burst, _cancelNext;
        private long _reqId;
        // statistics: Interlocked-free, written by the main thread (Service) or under _lock
        public long Points, Frames, Requests, Cancels;
        public double MaxFrameMs, SumFrameMs, WaitMs;

        /// <summary>clockMs: monotonic milliseconds (test seam: a fake clock). Null = Stopwatch.</summary>
        public TerrainBroker(Func<double> clockMs = null)
        {
            if (clockMs != null) _clock = clockMs;
            else
            {
                var sw = Stopwatch.StartNew();
                _clock = () => sw.Elapsed.TotalMilliseconds;
            }
        }

        /// <summary>Main thread: a request is waiting for answers.</summary>
        public bool Pending { get { lock (_lock) { return _active && _cursor < _n; } } }

        // ------------------------------------------------------------------------------------------------ worker thread

        /// <summary>
        /// Posts n points (latLon[2i], latLon[2i + 1]) and waits until heights[i] holds the answer of every point (float.NaN = unknown). burst = the first build after the layer
        /// was switched on (bigger frame budget). stop() = the worker is being disposed, superseded() = a newer job replaced this build; both are polled every WaitTickMs.
        /// </summary>
        public FetchResult Fetch(double[] latLon, int n, float[] heights, bool burst, Func<bool> stop, Func<bool> superseded)
        {
            if (n <= 0) return FetchResult.Done;
            long t0 = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                if (_cancelNext) { _cancelNext = false; Cancels++; return FetchResult.Cancelled; }
                _reqLatLon = latLon; _reqHeights = heights; _n = n; _cursor = 0; _done = 0; _burst = burst; _active = true; _reqId++; Requests++;
            }
            try
            {
                while (true)
                {
                    lock (_lock)
                    {
                        if (_active && _done >= _n) { _active = false; return FetchResult.Done; }
                        if (!_active) return FetchResult.Cancelled;   // Cancel() took the slot
                        Monitor.Wait(_lock, WaitTickMs);
                        if (_active && _done >= _n) { _active = false; return FetchResult.Done; }
                        if (!_active) return FetchResult.Cancelled;
                    }
                    bool st = false, sup = false;
                    try { st = stop != null && stop(); sup = !st && superseded != null && superseded(); } catch { }
                    if (st || sup)
                    {
                        lock (_lock) { _active = false; _reqId++; }
                        return st ? FetchResult.Stopped : FetchResult.Cancelled;
                    }
                }
            }
            finally
            {
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                lock (_lock) { WaitMs += ms; _reqLatLon = null; _reqHeights = null; }
            }
        }

        /// <summary>Wakes a waiting Fetch so it re-checks stop / superseded now (Submit and Dispose of the worker call it).</summary>
        public void Wake() { lock (_lock) Monitor.PulseAll(_lock); }

        // ------------------------------------------------------------------------------------------------ main thread

        /// <summary>Answers points of the pending request until the budget (burst or normal) is spent; the clock is read every ClockEvery points. Returns the points answered.</summary>
        public int Service(Func<double, double, double> game, out double spentMs)
        {
            spentMs = 0;
            if (game == null) return 0;
            long id;
            int from, take;
            bool burst;
            lock (_lock)
            {
                if (!_active || _cursor >= _n) return 0;
                id = _reqId; from = _cursor; burst = _burst;
                take = Math.Min(MaxChunk, _n - _cursor);
                Array.Copy(_reqLatLon, 2 * from, _inLatLon, 0, 2 * take);
            }
            double budget = burst ? BurstBudgetMs : BudgetMs;
            double t0 = _clock();
            int k = 0;
            for (; k < take; k++)
            {
                double v;
                try { v = game(_inLatLon[2 * k], _inLatLon[2 * k + 1]); } catch (Exception) { v = double.NaN; }
                _answers[k] = (float)v;
                if (((k + 1) % ClockEvery) == 0 && _clock() - t0 >= budget) { k++; break; }
            }
            spentMs = _clock() - t0;
            lock (_lock)
            {
                Frames++; Points += k; SumFrameMs += spentMs; if (spentMs > MaxFrameMs) MaxFrameMs = spentMs;
                if (_active && _reqId == id)   // the request may have been cancelled / replaced meanwhile: then the answers are dropped
                {
                    Array.Copy(_answers, 0, _reqHeights, from, k);
                    _cursor = from + k; _done = from + k;
                    if (_done >= _n) Monitor.PulseAll(_lock);
                }
            }
            return k;
        }

        /// <summary>Main thread: the overlay went off / the player left the mission. Releases a waiting worker with Cancelled; with no request waiting the next Fetch is cancelled (until Arm).</summary>
        public void Cancel()
        {
            lock (_lock)
            {
                if (_active) { _active = false; _reqId++; Cancels++; }
                else _cancelNext = true;
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>Main thread: the layer is going to work (a job is submitted): forget a Cancel that nobody consumed.</summary>
        public void Arm() { lock (_lock) { _cancelNext = false; } }

        /// <summary>Reads and resets the statistics of the window (main-thread frames: points, count, sum and max of ms; the worker's total wait).</summary>
        public void Drain(out long points, out int frames, out double sumMs, out double maxMs, out double waitMs)
        {
            lock (_lock)
            {
                points = Points; frames = (int)Frames; sumMs = SumFrameMs; maxMs = MaxFrameMs; waitMs = WaitMs;
                Points = 0; Frames = 0; SumFrameMs = 0; MaxFrameMs = 0; WaitMs = 0;
            }
        }

        // ------------------------------------------------------------------------------------------------ self test

        private sealed class FakeClock
        {
            public double Ms;
            public double Now() { return Ms; }
        }

        /// <summary>layertest / puretest: null = ok. Fake clock moves 0.04 ms per game call, so 2 ms hold about 50 points.</summary>
        internal static string SelfTest()
        {
            try
            {
                var clock = new FakeClock();
                var b = new TerrainBroker(clock.Now);
                int gameCalls = 0;
                Func<double, double, double> game = (la, lo) => { gameCalls++; clock.Ms += 0.04; return la + lo; };
                double spent;

                // no request: nothing answered, the game is not called
                if (b.Service(game, out spent) != 0 || gameCalls != 0 || b.Pending) return "broker: idle Service called the game";

                // budget, cursor across frames, no point lost or repeated
                int n = 200;
                var ll = new double[2 * n];
                var h = new float[n];
                for (int i = 0; i < n; i++) { ll[2 * i] = i; ll[2 * i + 1] = 1000 + i; h[i] = float.NaN; }
                TerrainBroker.FetchResult res = TerrainBroker.FetchResult.Stopped;
                var t = new Thread(() => { res = b.Fetch(ll, n, h, false, () => false, () => false); });
                t.Start();
                var wait = Stopwatch.StartNew();
                while (!b.Pending && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                if (!b.Pending) return "broker: request never became pending";
                int got1 = b.Service(game, out spent);
                if (got1 < 50 || got1 > 50 + ClockEvery) return "broker: first frame answered " + got1;
                if (spent > BudgetMs + ClockEvery * 0.04 + 1e-9) return "broker: first frame spent " + spent;
                int total = got1;
                int frames = 1;
                while (b.Pending && frames < 100) { total += b.Service(game, out spent); frames++; }
                t.Join(2000);
                if (t.IsAlive || res != TerrainBroker.FetchResult.Done) return "broker: Fetch result " + res;
                if (total != n || gameCalls != n) return "broker: points answered " + total + " game calls " + gameCalls;
                for (int i = 0; i < n; i++) if (Math.Abs(h[i] - (float)(i + 1000 + i)) > 1e-3) return "broker: answer " + i + " = " + h[i];
                if (b.Points != n || b.Frames != frames) return "broker: counters " + b.Points + "/" + b.Frames;

                // burst: 4 ms ~ 100 points
                gameCalls = 0;
                h = new float[n];
                var t2 = new Thread(() => { res = b.Fetch(ll, n, h, true, () => false, () => false); });
                t2.Start();
                wait.Restart();
                while (!b.Pending && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                int gotB = b.Service(game, out spent);
                if (gotB < 100 || gotB > 100 + ClockEvery) return "broker: burst frame answered " + gotB;
                while (b.Pending) b.Service(game, out spent);
                t2.Join(2000);

                // a batch above MaxChunk is cut into pieces (a frozen clock never spends the budget)
                var frozen = new TerrainBroker(() => 0.0);
                int big = 3 * MaxChunk / 2;
                var bll = new double[2 * big];
                var bh = new float[big];
                var t3 = new Thread(() => { res = frozen.Fetch(bll, big, bh, false, () => false, () => false); });
                t3.Start();
                wait.Restart();
                while (!frozen.Pending && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                int c1 = frozen.Service((la, lo) => 1.0, out spent), c2 = frozen.Service((la, lo) => 1.0, out spent);
                t3.Join(2000);
                if (c1 != MaxChunk || c2 != big - MaxChunk || res != TerrainBroker.FetchResult.Done) return "broker: chunking " + c1 + "/" + c2 + " " + res;

                // Cancel wakes the waiting worker within a tick
                var cb = new TerrainBroker(clock.Now);
                var t4 = new Thread(() => { res = cb.Fetch(ll, n, new float[n], false, () => false, () => false); });
                t4.Start();
                wait.Restart();
                while (!cb.Pending && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                var cw = Stopwatch.StartNew();
                cb.Cancel();
                t4.Join(2000);
                if (t4.IsAlive || res != TerrainBroker.FetchResult.Cancelled || cw.ElapsedMilliseconds > WaitTickMs + 100) return "broker: Cancel " + res + " after " + cw.ElapsedMilliseconds + " ms";
                if (cb.Pending) return "broker: pending after Cancel";
                // a Cancel with nothing waiting cancels the next Fetch once, Arm forgets it
                cb.Cancel();
                if (cb.Fetch(ll, n, new float[n], false, () => false, () => false) != TerrainBroker.FetchResult.Cancelled) return "broker: sticky Cancel";
                cb.Cancel(); cb.Arm();
                var t5 = new Thread(() => { res = cb.Fetch(ll, 4, new float[4], false, () => false, () => false); });
                t5.Start();
                wait.Restart();
                while (!cb.Pending && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                if (!cb.Pending) return "broker: Arm did not forget the Cancel";
                cb.Service(game, out spent);
                t5.Join(2000);
                if (res != TerrainBroker.FetchResult.Done) return "broker: Fetch after Arm " + res;

                // stop() and superseded()
                bool stopFlag = false, supFlag = false;
                var sb = new TerrainBroker(clock.Now);
                var t6 = new Thread(() => { res = sb.Fetch(ll, n, new float[n], false, () => stopFlag, () => supFlag); });
                t6.Start();
                wait.Restart();
                while (!sb.Pending && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                stopFlag = true; sb.Wake();
                t6.Join(2000);
                if (t6.IsAlive || res != TerrainBroker.FetchResult.Stopped) return "broker: stop() " + res;
                stopFlag = false; supFlag = true;
                var t7 = new Thread(() => { res = sb.Fetch(ll, n, new float[n], false, () => stopFlag, () => supFlag); });
                t7.Start();
                wait.Restart();
                while (!sb.Pending && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                sb.Wake();
                t7.Join(2000);
                if (t7.IsAlive || res != TerrainBroker.FetchResult.Cancelled) return "broker: superseded() " + res;

                // the game throws: NaN, the broker lives on
                var gb = new TerrainBroker(clock.Now);
                var gh = new float[8];
                var t8 = new Thread(() => { res = gb.Fetch(ll, 8, gh, false, () => false, () => false); });
                t8.Start();
                wait.Restart();
                while (!gb.Pending && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                int thrown = 0;
                while (gb.Pending) gb.Service((la, lo) => { if (((int)la) % 2 == 0) { thrown++; throw new InvalidOperationException("x"); } return 5.0; }, out spent);
                t8.Join(2000);
                if (res != TerrainBroker.FetchResult.Done || thrown != 4 || !float.IsNaN(gh[0]) || gh[1] != 5f) return "broker: game exception " + res + " " + thrown;

                // statistics are read and reset
                long pts; int frs; double sum, max, wt;
                b.Drain(out pts, out frs, out sum, out max, out wt);
                if (pts != n + n || frs <= 0 || max <= 0) return "broker: Drain " + pts + "/" + frs;
                b.Drain(out pts, out frs, out sum, out max, out wt);
                if (pts != 0 || frs != 0) return "broker: Drain did not reset";
                return null;
            }
            catch (Exception e) { return "broker selftest exception: " + e; }
        }
    }
}
