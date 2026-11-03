using System;
using System.Threading;

namespace VoiceBotBridge
{
    /// <summary>T-125 (R2): game values read at most once per Unity frame. Pure: the frame number and the readers come in.
    /// frame() &lt; 0 (no clock: selfcheck, stands) or a call off the owner thread = every access reads the game (old behaviour).</summary>
    internal sealed class FrameState
    {
        private readonly Func<long> _frame;
        private readonly Func<object> _readOm;
        private readonly Func<double?> _readTime;
        private readonly Func<double> _readScale;
        private readonly int _owner;

        // frame number + 1 of the cached value; 0 = never read
        private long _omFrame, _timeFrame, _scaleFrame;
        private object _om;
        private double? _time;
        private double _scale = 1.0;
        private long _reads;

        public FrameState(Func<long> frame, Func<object> objectsManager, Func<double?> missionTimeS, Func<double> uiScale)
        {
            _frame = frame; _readOm = objectsManager; _readTime = missionTimeS; _readScale = uiScale;
            _owner = Environment.CurrentManagedThreadId;
        }

        /// <summary>frame(); -1 = no per-frame caching.</summary>
        public long Now
        {
            get
            {
                try { return _frame == null ? -1 : _frame(); }
                catch { return -1; }
            }
        }

        /// <summary>null outside a mission (the old ObjectsManagerInstance).</summary>
        public object ObjectsManager
        {
            get
            {
                long f = CacheFrame();
                if (f < 0) return ReadOm();
                if (_omFrame == f + 1) return _om;
                var v = ReadOm();
                _om = v; _omFrame = f + 1;
                return v;
            }
        }

        public bool InMission { get { return ObjectsManager != null; } }

        public double? MissionTimeS
        {
            get
            {
                long f = CacheFrame();
                if (f < 0) return ReadTime();
                if (_timeFrame == f + 1) return _time;
                var v = ReadTime();
                _time = v; _timeFrame = f + 1;
                return v;
            }
        }

        /// <summary>1.0 when the reader throws.</summary>
        public double UiScale
        {
            get
            {
                long f = CacheFrame();
                if (f < 0) return ReadScale();
                if (_scaleFrame == f + 1) return _scale;
                var v = ReadScale();
                _scale = v; _scaleFrame = f + 1;
                return v;
            }
        }

        /// <summary>Drops every cached value (tests; a caller that knows the world changed).</summary>
        public void Invalidate()
        {
            _omFrame = 0; _timeFrame = 0; _scaleFrame = 0; _om = null; _time = null;
        }

        /// <summary>Real reads so far (tests, diag).</summary>
        public long Reads { get { return Interlocked.Read(ref _reads); } }

        // the frame to cache under; -1 = read through (no clock, or not the owner thread)
        private long CacheFrame()
        {
            if (Environment.CurrentManagedThreadId != _owner) return -1;
            return Now;
        }

        private object ReadOm()
        {
            Interlocked.Increment(ref _reads);
            try { return _readOm == null ? null : _readOm(); } catch { return null; }
        }

        private double? ReadTime()
        {
            Interlocked.Increment(ref _reads);
            try { return _readTime == null ? null : _readTime(); } catch { return null; }
        }

        private double ReadScale()
        {
            Interlocked.Increment(ref _reads);
            try { return _readScale == null ? 1.0 : _readScale(); } catch { return 1.0; }
        }

        // ---------- self-test (puretest framestate) ----------

        private sealed class Fx
        {
            public long Clock = 5;
            public int Om, Time, Scale;
            public bool Throw;
            public readonly object Obj = new object();
            public FrameState S;
            public Fx()
            {
                S = new FrameState(() => Clock,
                    () => { Om++; if (Throw) throw new InvalidOperationException("x"); return Obj; },
                    () => { Time++; if (Throw) throw new InvalidOperationException("x"); return 12.5; },
                    () => { Scale++; if (Throw) throw new InvalidOperationException("x"); return 2.0; });
            }
        }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            try
            {
                var a = new Fx();
                for (int i = 0; i < 100; i++) { var x = a.S.InMission; var y = a.S.MissionTimeS; var z = a.S.UiScale; }
                if (a.Om != 1 || a.Time != 1 || a.Scale != 1) return "case1: readers " + a.Om + "/" + a.Time + "/" + a.Scale + " instead of 1/1/1";
                if (!a.S.InMission || a.S.MissionTimeS != 12.5 || a.S.UiScale != 2.0) return "case1: values";
                a.Clock = 6;
                for (int i = 0; i < 100; i++) { var x = a.S.InMission; var y = a.S.MissionTimeS; var z = a.S.UiScale; }
                if (a.Om != 2 || a.Time != 2 || a.Scale != 2) return "case2: readers " + a.Om + "/" + a.Time + "/" + a.Scale + " instead of 2/2/2";
                // 3: an unused value is not read
                var b = new Fx();
                for (int i = 0; i < 10; i++) { var y = b.S.MissionTimeS; }
                if (b.Om != 0 || b.Scale != 0 || b.Time != 1 || b.S.Reads != 1) return "case3: unused value was read";
                // 4: no clock = read through
                var c = new Fx(); c.Clock = -1;
                for (int i = 0; i < 10; i++) { var x = c.S.InMission; }
                if (c.Om != 10) return "case4: " + c.Om + " reads instead of 10";
                // 5: a throwing reader: defaults, once per frame
                var d = new Fx(); d.Throw = true;
                for (int i = 0; i < 10; i++)
                {
                    if (d.S.InMission || d.S.ObjectsManager != null || d.S.MissionTimeS.HasValue || d.S.UiScale != 1.0) return "case5: defaults";
                }
                if (d.Om != 1 || d.Time != 1 || d.Scale != 1) return "case5: a failing reader called " + d.Om + "/" + d.Time + "/" + d.Scale + " times";
                // 6: Invalidate
                var e = new Fx();
                var v1 = e.S.InMission; e.S.Invalidate(); v1 = e.S.InMission;
                if (e.Om != 2) return "case6: Invalidate";
                // 7: another thread reads through and does not spoil the cache
                var g = new Fx();
                var m0 = g.S.ObjectsManager;
                object seen = null; bool inM = false;
                var th = new Thread(() => { seen = g.S.ObjectsManager; inM = g.S.InMission; });
                th.Start(); th.Join();
                if (g.Om != 3 || seen != g.Obj || !inM) return "case7: foreign thread reads " + g.Om;
                var m1 = g.S.ObjectsManager;
                if (g.Om != 3 || m1 != g.Obj) return "case7: owner cache spoiled";
                // 8: no allocations on the hit path
                var h = new Fx();
                var w = h.S.InMission; var w2 = h.S.MissionTimeS; var w3 = h.S.UiScale;
                long before = GC.GetAllocatedBytesForCurrentThread();
                double acc = 0;
                for (int i = 0; i < 10000; i++)
                {
                    if (h.S.InMission) acc += h.S.UiScale;
                    acc += h.S.MissionTimeS ?? 0;
                    if (h.S.ObjectsManager == null) acc += 1;
                }
                long after = GC.GetAllocatedBytesForCurrentThread();
                if (after - before != 0) return "case8: " + (after - before) + " bytes allocated on the hit path (acc " + acc + ")";
                // 9
                var k = new Fx();
                if (k.S.InMission != (k.S.ObjectsManager != null)) return "case9: InMission";
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }
    }
}
