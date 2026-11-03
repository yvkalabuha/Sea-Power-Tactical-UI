using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-127 (R4): the Noesis tree walk of CoverageOverlay.TryFindMap spread over frames. Same visit order and result as the one-shot walk
    /// (stack, children 0..n-1, the last child is popped first). The tree access comes in as delegates; the lists are reused between walks.
    /// </summary>
    internal sealed class MapTreeWalk
    {
        public const int FrameBudget = 2000;    // nodes per frame (P2-5 b)
        public const int TotalBudget = 60000;   // the limit of one whole walk (as before)
        private const int MaxWindows = 16;

        private readonly Func<object, int> _childCount;
        private readonly Func<object, int, object> _child;
        private readonly Func<object, bool> _isWindow;
        private readonly Func<object, bool> _isMap;
        private object[] _stackNode = new object[64];   // Noesis tree nodes of the map search, cleared on every walk
        private object[] _stackWin = new object[64];    // nearest Window ancestor of each stacked node
        private int _count;
        private readonly List<object> _maps = new List<object>();         // Noesis map nodes found by the walk
        private readonly List<object> _mapWindows = new List<object>();   // Noesis windows hosting the found maps
        private readonly List<object> _windows = new List<object>();      // Noesis window nodes seen by the walk (at most 16)
        private object _root;                                              // Noesis root view of the running walk

        public MapTreeWalk(Func<object, int> childCount, Func<object, int, object> child, Func<object, bool> isWindow, Func<object, bool> isMap)
        {
            _childCount = childCount; _child = child; _isWindow = isWindow; _isMap = isMap;
        }

        public bool Running { get; private set; }
        public object Root { get { return _root; } }
        public int Visited { get; private set; }
        public int Steps { get; private set; }
        public IList<object> Maps { get { return _maps; } }
        public IList<object> MapWindows { get { return _mapWindows; } }
        public IList<object> Windows { get { return _windows; } }

        public void Start(object root)
        {
            Clear();
            _root = root;
            Visited = 0; Steps = 0;
            if (root == null) { Running = false; return; }
            Push(root, null);
            Running = true;
        }

        public void Abort()
        {
            Clear();
            Running = false;
        }

        private void Clear()
        {
            Array.Clear(_stackNode, 0, _count);
            Array.Clear(_stackWin, 0, _count);
            _count = 0;
            _maps.Clear(); _mapWindows.Clear(); _windows.Clear();
        }

        private void Push(object node, object win)
        {
            if (_count == _stackNode.Length)
            {
                Array.Resize(ref _stackNode, _count * 2);
                Array.Resize(ref _stackWin, _count * 2);
            }
            _stackNode[_count] = node; _stackWin[_count] = win; _count++;
        }

        /// <summary>Visits at most budget nodes. true = the walk is over (stack empty or TotalBudget spent).</summary>
        public bool Step(int budget)
        {
            if (!Running) return true;
            Steps++;
            int n = 0;
            while (_count > 0 && n < budget && Visited < TotalBudget)
            {
                n++; Visited++;
                _count--;
                object node = _stackNode[_count], parentWin = _stackWin[_count];
                _stackNode[_count] = null; _stackWin[_count] = null;
                bool isWin = _isWindow(node);
                object win = isWin ? node : parentWin;
                if (isWin && _windows.Count < MaxWindows) _windows.Add(node);
                if (_isMap(node)) { _maps.Add(node); _mapWindows.Add(parentWin); }
                int kids;
                try { kids = _childCount(node); } catch { continue; }
                for (int i = 0; i < kids; i++)
                {
                    try { var c = _child(node, i); if (c != null) Push(c, win); } catch { }
                }
            }
            if (_count == 0 || Visited >= TotalBudget)
            {
                Array.Clear(_stackNode, 0, _count);
                Array.Clear(_stackWin, 0, _count);
                _count = 0;
                Running = false;
            }
            return !Running;
        }

        // ---------- self test ----------

        private sealed class Fake
        {
            public Fake[] Kids = new Fake[0];
            public bool Win, Map;
        }

        private static Fake Build(Random rnd, int nodes)
        {
            var all = new List<Fake>(nodes);
            var kidLists = new List<List<Fake>>(nodes);
            var root = new Fake();
            all.Add(root); kidLists.Add(new List<Fake>());
            for (int i = 1; i < nodes; i++)
            {
                var f = new Fake { Win = rnd.Next(20) == 0, Map = rnd.Next(25) == 0 };
                int parent = rnd.Next(3) == 0 ? rnd.Next(all.Count) : Math.Max(0, all.Count - 1 - rnd.Next(4));
                kidLists[parent].Add(f);
                all.Add(f); kidLists.Add(new List<Fake>());
            }
            for (int i = 0; i < all.Count; i++) all[i].Kids = kidLists[i].ToArray();
            return root;
        }

        private static Fake Chain(int nodes)
        {
            var root = new Fake(); var cur = root;
            for (int i = 1; i < nodes; i++) { var f = new Fake(); cur.Kids = new[] { f }; cur = f; }
            return root;
        }

        private static MapTreeWalk MakeWalk()
        {
            return new MapTreeWalk(o => ((Fake)o).Kids.Length, (o, i) => ((Fake)o).Kids[i], o => ((Fake)o).Win, o => ((Fake)o).Map);
        }

        /// <summary>The one-shot walk of TryFindMap as it was before T-127 (the reference).</summary>
        private static int Reference(object root, List<object> found, List<object> hostWin)
        {
            var stack = new Stack<KeyValuePair<object, object>>();
            stack.Push(new KeyValuePair<object, object>(root, null));
            int budget = 60000, visited = 0;
            while (stack.Count > 0 && budget-- > 0)
            {
                var item = stack.Pop();
                visited++;
                var node = item.Key;
                object win = ((Fake)node).Win ? node : item.Value;
                if (((Fake)node).Map) { found.Add(node); hostWin.Add(item.Value); }
                int n = 0;
                try { n = ((Fake)node).Kids.Length; } catch { continue; }
                for (int i = 0; i < n; i++)
                {
                    try { var c = ((Fake)node).Kids[i]; if (c != null) stack.Push(new KeyValuePair<object, object>(c, win)); } catch { }
                }
            }
            return visited;
        }

        private static string Compare(MapTreeWalk w, Fake root, int budget, int tree)
        {
            var fm = new List<object>(); var fw = new List<object>();
            int refVisited = Reference(root, fm, fw);
            w.Start(root);
            int guard = 0;
            while (!w.Step(budget)) { if (++guard > 200000) return "walk does not end, tree " + tree; }
            if (w.Visited != refVisited) return "visited " + w.Visited + " != " + refVisited + " tree " + tree + " budget " + budget;
            if (w.Maps.Count != fm.Count || w.MapWindows.Count != fw.Count) return "maps count tree " + tree + " budget " + budget;
            for (int i = 0; i < fm.Count; i++)
                if (!ReferenceEquals(w.Maps[i], fm[i]) || !ReferenceEquals(w.MapWindows[i], fw[i])) return "map order tree " + tree + " budget " + budget + " at " + i;
            if (w.Windows.Count > MaxWindows) return "windows > 16";
            return null;
        }

        public static string SelfTest()
        {
            var rnd = new Random(127);
            var walk = MakeWalk();
            int[] budgets = { int.MaxValue, 1, 7, FrameBudget };
            for (int t = 0; t < 200; t++)
            {
                var root = Build(rnd, 1 + rnd.Next(5000));
                foreach (int b in budgets)
                {
                    string err = Compare(walk, root, b, t);
                    if (err != null) return err;
                }
            }
            // per-step limit and total budget
            var big = Build(new Random(5), 60000);
            walk.Start(big);
            int steps = 0, prev = 0;
            while (!walk.Step(FrameBudget))
            {
                steps++;
                if (walk.Visited - prev > FrameBudget) return "step visited > FrameBudget";
                prev = walk.Visited;
            }
            steps++;
            if (walk.Visited - prev > FrameBudget) return "last step > FrameBudget";
            if (walk.Visited != 60000 || steps != 30 || walk.Steps != 30) return "60000 nodes: visited " + walk.Visited + " steps " + steps;
            var huge = Chain(70000);
            walk.Start(huge);
            while (!walk.Step(FrameBudget)) { }
            if (walk.Visited != TotalBudget) return "70000 nodes: visited " + walk.Visited;
            // abort
            walk.Start(Build(new Random(9), 500));
            walk.Step(10);
            walk.Abort();
            if (walk.Running) return "abort: still running";
            walk.Start(Chain(3));
            if (walk.Maps.Count != 0 || walk.MapWindows.Count != 0 || walk.Windows.Count != 0) return "lists not cleared on Start";
            // exceptions in the delegates are skipped like before
            var bad = new MapTreeWalk(o => { if (((Fake)o).Win) throw new InvalidOperationException(); return ((Fake)o).Kids.Length; },
                (o, i) => { if (i == 1) throw new InvalidOperationException(); return ((Fake)o).Kids[i]; }, o => ((Fake)o).Win, o => ((Fake)o).Map);
            bad.Start(Build(new Random(3), 300));
            while (!bad.Step(FrameBudget)) { }
            // windows cap
            var many = new Fake { Kids = new Fake[40] };
            for (int i = 0; i < 40; i++) many.Kids[i] = new Fake { Win = true };
            walk.Start(many);
            while (!walk.Step(FrameBudget)) { }
            if (walk.Windows.Count != MaxWindows) return "windows cap " + walk.Windows.Count;
            // allocations: the second walk of the same tree creates no garbage
            var tree = Build(new Random(11), 4000);
            walk.Start(tree); while (!walk.Step(FrameBudget)) { }
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            walk.Start(tree); while (!walk.Step(FrameBudget)) { }
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            if (a1 - a0 != 0) return "second walk allocated " + (a1 - a0) + " B";
            // bench (informative)
            var sw = Stopwatch.StartNew();
            int nodes = 0, maxStep = 0;
            for (int r = 0; r < 20; r++)
            {
                walk.Start(tree);
                int p = 0;
                while (!walk.Step(FrameBudget)) { maxStep = Math.Max(maxStep, walk.Visited - p); p = walk.Visited; }
                nodes += walk.Visited;
            }
            sw.Stop();
            Console.WriteLine("pure mapscan bench: " + (sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, nodes)).ToString("F0") + " ns/node (fake tree), max nodes/step " + maxStep);
            return MapScanSchedule.SelfTest();
        }
    }

    internal enum MapScanState { Idle, Waiting, Scanning, Parked }

    /// <summary>T-127: when to walk. Back-off 2, 4, 8 s (CoverageMath.NextScanDelayS); after ParkAfterMisses misses no walks until the view changes.</summary>
    internal sealed class MapScanSchedule
    {
        public const int ParkAfterMisses = 3;    // 2 + 4 + 8 s without a map
        public const double CheckEveryS = 0.5;   // parked: signature check period
        public const double ParkedRescanS = 30;  // parked: safety walk period

        private double _next;        // Waiting: not before; Scanning: the back-off deadline after the walk
        private double _safetyAt;    // Parked: the safety walk
        private double _checkAt;     // Parked: the next signature check
        private bool _hasSig;
        private long _sig;

        public MapScanSchedule() { State = MapScanState.Waiting; }

        public MapScanState State { get; private set; }
        public int Misses { get; private set; }
        /// <summary>"view changed" or "safety": why the last walk was started out of the parked state (for the log).</summary>
        public string WakeReason { get; private set; }

        public bool ShouldStart(double now)
        {
            if (State == MapScanState.Waiting) return now >= _next;
            if (State == MapScanState.Parked) return now >= _safetyAt;
            return false;
        }

        public void Started(double now)
        {
            if (State == MapScanState.Parked) WakeReason = "safety";
            State = MapScanState.Scanning;
            _next = now + CoverageMath.NextScanDelayS(Misses);
        }

        public void Finished(bool found, double now) { Finished(found, now, null); }

        /// <summary>sigNow: the view signature at this moment; when the search parks it is the baseline, so a view change right after parking wakes it.</summary>
        public void Finished(bool found, double now, long? sigNow)
        {
            if (found) { State = MapScanState.Idle; Misses = 0; _next = now; return; }
            Misses++;
            if (Misses >= ParkAfterMisses)
            {
                State = MapScanState.Parked;
                _safetyAt = now + ParkedRescanS; _checkAt = now + CheckEveryS;
                _hasSig = sigNow.HasValue; _sig = sigNow ?? 0;
            }
            else State = MapScanState.Waiting;
        }

        public void MapLost(double now)
        {
            if (State == MapScanState.Idle) { State = MapScanState.Waiting; _next = now; }
        }

        public bool ShouldCheckSignature(double now) { return State == MapScanState.Parked && now >= _checkAt; }

        public bool Signature(long sig, double now)
        {
            if (State != MapScanState.Parked) return false;
            _checkAt = now + CheckEveryS;
            if (!_hasSig) { _hasSig = true; _sig = sig; return false; }
            if (sig == _sig) return false;
            State = MapScanState.Waiting; _next = now; Misses = 0; _hasSig = false; WakeReason = "view changed";
            return true;
        }

        public void Reset(double now)
        {
            State = MapScanState.Waiting; _next = now; Misses = 0; _hasSig = false;
        }

        public string Text
        {
            get
            {
                switch (State)
                {
                    case MapScanState.Idle: return "idle";
                    case MapScanState.Waiting: return "wait";
                    case MapScanState.Scanning: return "scan";
                    default: return "parked";
                }
            }
        }

        /// <summary>true when the next Finished(false) would park the search (the caller computes the baseline signature only then).</summary>
        public bool WillPark { get { return Misses + 1 >= ParkAfterMisses; } }

        public static long Mix(long h, long v) { unchecked { return h * 31 + v; } }

        /// <summary>Folds the state code of each window into the signature; a window whose read throws (destroyed) is skipped with a fixed code, the rest still count.</summary>
        public static long WindowsSignature(long h, IList<object> wins, Func<object, int> state)
        {
            if (wins == null) return h;
            for (int i = 0; i < wins.Count; i++)
            {
                try { h = Mix(h, state(wins[i])); } catch { h = Mix(h, 3); }
            }
            return h;
        }

        public static string SelfTest()
        {
            var s = new MapScanSchedule();
            if (s.State != MapScanState.Waiting || !s.ShouldStart(0)) return "initial state";
            s.Started(0);
            if (s.State != MapScanState.Scanning || s.ShouldStart(100) || s.Text != "scan") return "scanning state";
            s.Finished(false, 0.1);
            if (s.State != MapScanState.Waiting || s.Misses != 1 || s.ShouldStart(1.9) || !s.ShouldStart(2.0)) return "first miss waits 2 s";
            s.Started(2); s.Finished(false, 2.1);
            if (s.ShouldStart(5.9) || !s.ShouldStart(6.0) || s.Misses != 2) return "second miss waits 4 s";
            s.Started(6); s.Finished(false, 6.1);
            if (s.State != MapScanState.Parked || s.Misses != 3 || s.Text != "parked") return "parked after 3 misses";
            if (s.ShouldCheckSignature(6.3) || !s.ShouldCheckSignature(6.6)) return "check period";
            if (s.Signature(7, 6.6) || s.ShouldCheckSignature(7.0) || !s.ShouldCheckSignature(7.1)) return "first signature remembered";
            for (double t = 6.2; t < 36.0; t += 0.25) if (s.ShouldStart(t)) return "parked walk at " + t;
            if (s.Signature(7, 7.1)) return "same signature woke";
            if (!s.ShouldStart(36.1)) return "no safety walk after 30 s";
            s.Started(36.1);
            if (s.WakeReason != "safety") return "safety reason";
            s.Finished(false, 36.2);
            if (s.State != MapScanState.Parked || s.ShouldStart(60)) return "parked again after the safety walk";
            if (!s.ShouldStart(66.3)) return "second safety walk";
            // wake by a signature change
            if (s.Signature(1, 40.0) || !s.Signature(2, 41.0)) return "signature wake";
            if (s.State != MapScanState.Waiting || s.Misses != 0 || !s.ShouldStart(41.0) || s.WakeReason != "view changed") return "state after wake";
            s.Started(41.0); s.Finished(true, 41.2);
            if (s.State != MapScanState.Idle || s.Misses != 0 || s.Text != "idle" || s.ShouldStart(50)) return "found -> idle";
            s.MapLost(42.0);
            if (s.State != MapScanState.Waiting || !s.ShouldStart(42.0)) return "map lost allows a search";
            s.MapLost(43.0);
            s.Started(42.0); s.Finished(false, 42.1); s.Started(44.1); s.Finished(false, 44.2); s.Started(48.2); s.Finished(false, 48.3);
            if (s.State != MapScanState.Parked) return "park again";
            s.Reset(50);
            if (s.State != MapScanState.Waiting || s.Misses != 0 || !s.ShouldStart(50) || s.Text != "wait") return "reset from parked";
            if (s.ShouldCheckSignature(100) || s.Signature(5, 100)) return "signature outside parked";
            // baseline at park time: a change at the first check wakes the search
            var p = new MapScanSchedule();
            p.Started(0); p.Finished(false, 1); p.Started(2); p.Finished(false, 3);
            if (!p.WillPark) return "WillPark before the 3rd miss";
            p.Started(6); p.Finished(false, 10, 42);
            if (p.State != MapScanState.Parked || p.Signature(42, 10.6) || !p.Signature(43, 11.2)) return "baseline signature at park time";
            var q = new MapScanSchedule();
            q.Started(0); q.Finished(false, 1); q.Started(2); q.Finished(false, 3); q.Started(6); q.Finished(false, 10);
            if (q.Signature(43, 10.6)) return "no baseline: first value is remembered";
            // a throwing window does not collapse the signature
            var wins = new List<object> { 1, 2, 3 };
            long good = WindowsSignature(5, wins, o => (int)o);
            long withBad = WindowsSignature(5, wins, o => { if ((int)o == 2) throw new InvalidOperationException(); return (int)o; });
            long withBad2 = WindowsSignature(5, wins, o => { if ((int)o == 2) throw new InvalidOperationException(); return (int)o; });
            long other = WindowsSignature(5, wins, o => (int)o + 1);
            if (withBad == 0 || withBad != withBad2 || withBad == good) return "throwing window signature";
            if (WindowsSignature(5, wins, o => { if ((int)o == 2) throw new InvalidOperationException(); return (int)o + 1; }) == withBad || other == good) return "window state change not seen";
            return null;
        }
    }
}
